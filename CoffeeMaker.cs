using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Learn.CoffeeMaker
{
    internal enum StatusCode
    {
        Completed = 200,
        InProgress = 202,
        ReportDeviceInitialProperty = 203,
        BadRequest = 400,
        NotFound = 404
    }

    public class CoffeeMaker
    {
        private readonly Random _random = new();

        private readonly DeviceClient _deviceClient;

        private readonly bool _warrantyState;

        // Brew cycle length, in telemetry ticks (~1 tick = 1 second).
        private const int BrewDurationSeconds = 28;

        // Matches the OptimalTemperature bounds declared in CoffeeMaker.json.
        private const double MinOptimalTemperature = 86d;
        private const double MaxOptimalTemperature = 100d;

        // All the mutable device state below is read/written both by the telemetry
        // loop (SendTelemetryAsync, on a timer) and by command/property callbacks the
        // Azure SDK invokes from its own thread - so every access goes through this lock.
        private readonly object _stateLock = new();

        //Variables default values - matches CoffeeMaker.json's declared initialValue (98).
        private double _optimalTemperature = 98d;
        private double _currentTemperature = 98d;
        private string _cupState = "detected";
        private string _brewingState = "notbrewing";
        private int _brewingTimer = 0;
        private bool _maintenanceState = false;

        // Cup lifecycle: instead of a coin-flip every tick, the cup stays present or
        // absent for a realistic stretch of time, and never changes mid-brew - you
        // can't swap cups while the machine is actively pouring.
        private int _cupStateTimer;

        public CoffeeMaker(DeviceClient deviceClient)
        {
            _deviceClient = deviceClient ?? throw new ArgumentNullException(nameof(deviceClient));

            //When device starts it randomly sets the warranty state to either true or false.
            _warrantyState = _random.NextDouble() > 0.5 ? true : false;

            // Start with a cup present for a little while before the first random change.
            _cupStateTimer = NextCupPresentDuration();
        }

        //<Workflow>
        public async Task PerformOperationsAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"Device successfully connected to Azure IoT Central");

            Console.WriteLine($"- Set handler for \"SetMaintenanceMode\" command.");
            await _deviceClient.SetMethodHandlerAsync("SetMaintenanceMode", HandleMaintenanceModeCommand, _deviceClient, cancellationToken);

            Console.WriteLine($"- Set handler for \"StartBrewing\" command.");
            await _deviceClient.SetMethodHandlerAsync("StartBrewing", HandleStartBrewingCommand, _deviceClient, cancellationToken);

            Console.WriteLine($"- Set handler to receive \"OptimalTemperature\" updates.");
            await _deviceClient.SetDesiredPropertyUpdateCallbackAsync(OptimalTemperatureUpdateCallbackAsync, _deviceClient, cancellationToken);

            Console.WriteLine("- Update \"DeviceWarrantyExpired\" reported property on the initial startup.");
            await UpdateDeviceWarranty(cancellationToken);

            Console.WriteLine("- Report initial \"OptimalTemperature\" reported property.");
            await ReportOptimalTemperatureAsync(_optimalTemperature, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                await SendTelemetryAsync(cancellationToken);
                await Task.Delay(1000, cancellationToken);
            }
        }
        //</Workflow>

        //<Telemetry>
        //Send temperature and humidity telemetry, whether it's currently brewing and when a cup is detected.
        private async Task SendTelemetryAsync(CancellationToken cancellationToken)
        {
            // Compute the next tick's values under the lock, then release it before
            // doing the actual (awaited) network send - never hold a lock across an await.
            string cupState, brewingState;
            double temperature, humidity;
            lock (_stateLock)
            {
                UpdateCupState();
                UpdateTemperature();
                humidity = UpdateHumidity();
                UpdateBrewingState();

                cupState = _cupState;
                brewingState = _brewingState;
                temperature = _currentTemperature;
            }

            // Create JSON message
            string messageBody = JsonConvert.SerializeObject(
                new
                {
                    WaterTemperature = Math.Round(temperature, 1),
                    AirHumidity = Math.Round(humidity, 1),
                    CupDetected = cupState,
                    Brewing = brewingState
                });
            using var message = new Message(Encoding.ASCII.GetBytes(messageBody))
            {
                ContentType = "application/json",
                ContentEncoding = "utf-8",
            };

            //Show the information in console
            double infoTemperature = Math.Round(temperature, 1);
            double infoHumidity = Math.Round(humidity, 1);
            string infoCup = cupState == "detected" ? "Y" : "N";
            string infoBrewing = brewingState == "brewing" ? "Y" : "N";
            string infoMaintenance = _maintenanceState ? "Y" : "N";

            Console.WriteLine($"Telemetry send: Temperature: {infoTemperature}ºC Humidity: {infoHumidity}% " +
                $"Cup Detected: {infoCup} Brewing: {infoBrewing} Maintenance Mode: {infoMaintenance}");

            //Send the message
            await _deviceClient.SendEventAsync(message, cancellationToken);
        }

        // The cup can only change state between brews - once a brew starts, the cup
        // stays "detected" until the cycle finishes, then it lingers a while (someone
        // has to walk over and pick it up) before the machine is empty-handed again.
        // Callers must hold _stateLock.
        private void UpdateCupState()
        {
            if (_brewingState == "brewing")
            {
                return;
            }

            if (_cupStateTimer > 0)
            {
                _cupStateTimer--;
                return;
            }

            _cupState = _cupState == "detected" ? "notdetected" : "detected";
            _cupStateTimer = _cupState == "detected" ? NextCupPresentDuration() : NextCupAbsentDuration();
        }

        private int NextCupPresentDuration() => 30 + _random.Next(0, 30); // 30-59s with a cup in place
        private int NextCupAbsentDuration() => 5 + _random.Next(0, 10);   // 5-14s before someone puts a new cup down

        // Idle: temperature drifts gently around the optimal set point (thermostat noise).
        // Brewing: the heating element kicks in, so temperature ramps up toward a small
        // overshoot above optimal for the first half of the brew, then eases back down
        // toward optimal as the cycle finishes - much closer to how a real machine behaves
        // than uniform random noise regardless of what the machine is doing.
        // Callers must hold _stateLock.
        private void UpdateTemperature()
        {
            if (_brewingState == "brewing")
            {
                double brewProgress = 1d - (_brewingTimer / (double)BrewDurationSeconds); // 0 -> 1 across the brew
                double target = brewProgress < 0.5
                    ? _optimalTemperature + (brewProgress * 2 * 3d)   // ramp up to +3°C overshoot
                    : _optimalTemperature + ((1d - brewProgress) * 2 * 3d); // ease back down

                _currentTemperature += (target - _currentTemperature) * 0.3d; // smooth toward target
            }
            else
            {
                double idleTarget = _optimalTemperature + (_random.NextDouble() * 1d) - 0.5d;
                _currentTemperature += (idleTarget - _currentTemperature) * 0.2d;
            }
        }

        // Humidity ticks up while steam is being generated during a brew, and settles
        // back down to a normal room-air baseline once the machine goes idle.
        // Callers must hold _stateLock.
        private double UpdateHumidity()
        {
            double baseline = 30 + (_random.NextDouble() * 10); // 30-40% ambient
            return _brewingState == "brewing"
                ? baseline + 25 + (_random.NextDouble() * 15) // steam pushes humidity up
                : baseline;
        }

        // Callers must hold _stateLock.
        private void UpdateBrewingState()
        {
            if (_brewingTimer <= 0)
            {
                return;
            }

            _brewingTimer--;

            if (_brewingTimer == 0)
            {
                _brewingState = "notbrewing";
                Console.WriteLine(" * Brewing finished - your coffee is ready.");
            }
        }
        //</Telemetry>

        //<Commands>
        // The callback to handle "SetMaintenanceMode" command. Toggles maintenance mode
        // on/off (the original sample could only ever turn it on, with no way back).
        // Entering maintenance mode mid-brew stops the brew immediately, as a real
        // machine would if serviced while running.
        private Task<MethodResponse> HandleMaintenanceModeCommand(MethodRequest request, object userContext)
        {
            try
            {
                Console.WriteLine(" * Maintenance mode command received");

                string message;
                lock (_stateLock)
                {
                    _maintenanceState = !_maintenanceState;

                    if (_maintenanceState)
                    {
                        if (_brewingState == "brewing")
                        {
                            _brewingState = "notbrewing";
                            _brewingTimer = 0;
                            Console.WriteLine(" - Brewing stopped: entering maintenance mode.");
                        }
                        message = "Maintenance mode enabled.";
                    }
                    else
                    {
                        message = "Maintenance mode disabled.";
                    }
                }

                Console.WriteLine($" - {message}");

                byte[] responsePayload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
                return Task.FromResult(new MethodResponse(responsePayload, (int)StatusCode.Completed));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while handling \"SetMaintenanceMode\" command: {ex}");
                return Task.FromResult(new MethodResponse((int)StatusCode.BadRequest));
            }
        }

        // The callback to handle "StartBrewing" command. Unlike the original sample,
        // this now actually reports back whether brewing started or why it didn't -
        // instead of always answering "Success" even when nothing happened.
        private Task<MethodResponse> HandleStartBrewingCommand(MethodRequest request, object userContext)
        {
            try
            {
                Console.WriteLine(" * Start brewing command received");

                string failureReason = null;
                lock (_stateLock)
                {
                    if (_maintenanceState)
                    {
                        failureReason = "Cannot brew: device is in maintenance mode.";
                    }
                    else if (_brewingState == "brewing")
                    {
                        failureReason = "Cannot brew: the device is already brewing.";
                    }
                    else if (_cupState == "notdetected")
                    {
                        failureReason = "Cannot brew: no cup detected.";
                    }
                    else
                    {
                        //Start brewing
                        _brewingState = "brewing";
                        _brewingTimer = BrewDurationSeconds;
                    }
                }

                if (failureReason != null)
                {
                    Console.WriteLine($" - Warning: {failureReason}");
                    byte[] failurePayload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(failureReason));
                    return Task.FromResult(new MethodResponse(failurePayload, (int)StatusCode.BadRequest));
                }

                Console.WriteLine($" - Brewing started ({BrewDurationSeconds}s cycle).");

                byte[] responsePayload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject("Brewing started."));
                return Task.FromResult(new MethodResponse(responsePayload, (int)StatusCode.Completed));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while handling \"StartBrewing\" command: {ex}");
                return Task.FromResult(new MethodResponse((int)StatusCode.BadRequest));
            }
        }
        //</Commands>

        // The desired property update callback, which receives the OptimalTemperature as a desired property update,
        // and updates the current _optimalTemperature value over telemetry and reported property update.
        //<Properties>
        private async Task OptimalTemperatureUpdateCallbackAsync(TwinCollection desiredProperties, object userContext)
        {
            const string propertyName = "OptimalTemperature";

            try
            {
                bool optimalTempUpdateReceived;
                double optimalTemp;
                try
                {
                    (optimalTempUpdateReceived, optimalTemp) = GetPropertyFromTwin<double>(desiredProperties, propertyName);
                }
                catch (InvalidCastException)
                {
                    Console.WriteLine($" * Property: Received \"{propertyName}\" with an unexpected type - ignoring update.");
                    return;
                }

                if (!optimalTempUpdateReceived)
                {
                    Console.WriteLine($" * Property: Received an unrecognized property update from service:\n{desiredProperties.ToJson()}");
                    return;
                }

                Console.WriteLine($" * Property: Received - {{ \"{propertyName}\": {optimalTemp}°C }}.");

                // Reject anything outside the range CoffeeMaker.json declares for this property.
                if (optimalTemp < MinOptimalTemperature || optimalTemp > MaxOptimalTemperature)
                {
                    Console.WriteLine($" - Warning: {optimalTemp}°C is outside the valid range " +
                        $"({MinOptimalTemperature}-{MaxOptimalTemperature}°C) - update rejected.");

                    string jsonRejected = $"{{ \"{propertyName}\": {{ \"value\": {optimalTemp}, \"ac\": {(int)StatusCode.BadRequest}, " +
                        $"\"av\": {desiredProperties.Version}, \"ad\": \"Rejected - must be between {MinOptimalTemperature} and {MaxOptimalTemperature} degreeCelsius\" }} }}";
                    var reportedRejected = new TwinCollection(jsonRejected);
                    await _deviceClient.UpdateReportedPropertiesAsync(reportedRejected);
                    return;
                }

                //Update reported property to In Progress
                string jsonPropertyPending = $"{{ \"{propertyName}\": {{ \"value\": {optimalTemp}, \"ac\": {(int)StatusCode.InProgress}, " +
                    $"\"av\": {desiredProperties.Version}, \"ad\": \"In progress - reporting optimal temperature\" }} }}";
                var reportedPropertyPending = new TwinCollection(jsonPropertyPending);
                await _deviceClient.UpdateReportedPropertiesAsync(reportedPropertyPending);
                Console.WriteLine($" * Property: Update - {{\"{propertyName} \": {optimalTemp}°C }} is {StatusCode.InProgress}.");

                //Update the optimal temperature
                lock (_stateLock)
                {
                    _optimalTemperature = optimalTemp;
                }

                //Update reported property to Completed
                string jsonProperty = $"{{ \"{propertyName}\": {{ \"value\": {optimalTemp}, \"ac\": {(int)StatusCode.Completed}, " +
                    $"\"av\": {desiredProperties.Version}, \"ad\": \"Successfully updated optimal temperature\" }} }}";
                var reportedProperty = new TwinCollection(jsonProperty);
                await _deviceClient.UpdateReportedPropertiesAsync(reportedProperty);
                Console.WriteLine($" * Property: Update - {{\"{propertyName} \": {optimalTemp}°C }} is {StatusCode.Completed}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while handling \"{propertyName}\" property update: {ex}");
            }
        }

        private async Task UpdateDeviceWarranty(CancellationToken cancellationToken)
        {
            const string propertyName = "DeviceWarrantyExpired";

            var reportedProperties = new TwinCollection();
            reportedProperties[propertyName] = _warrantyState;

            await _deviceClient.UpdateReportedPropertiesAsync(reportedProperties, cancellationToken);
            Console.WriteLine($" * Property: Update - {{ \"{propertyName}\": {_warrantyState} }} is {StatusCode.Completed}.");
        }

        // Reports the device's current OptimalTemperature back to IoT Central right away,
        // so the Properties page shows a real value instead of "not reported" until an
        // operator happens to push the first desired-property update.
        private async Task ReportOptimalTemperatureAsync(double optimalTemperature, CancellationToken cancellationToken)
        {
            const string propertyName = "OptimalTemperature";

            var reportedProperties = new TwinCollection();
            reportedProperties[propertyName] = optimalTemperature;

            await _deviceClient.UpdateReportedPropertiesAsync(reportedProperties, cancellationToken);
            Console.WriteLine($" * Property: Update - {{ \"{propertyName}\": {optimalTemperature}°C }} is {StatusCode.Completed}.");
        }
        //</Properties>

        private static (bool, T) GetPropertyFromTwin<T>(TwinCollection collection, string propertyName)
        {
            return collection.Contains(propertyName) ? (true, (T)collection[propertyName]) : (false, default);
        }
    }
}
