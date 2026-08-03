// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CommandLine;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Learn.CoffeeMaker
{
    public class Program
    {
        private const string ModelId = "dtmi:com:example:ConnectedCoffeeMaker;1";
        private const string DpsEndpoint = "global.azure-devices-provisioning.net";
        public static async Task Main(string[] args)
        {
            //Parse application parameters
            Parameters parameters = null;
            ParserResult<Parameters> result = Parser.Default.ParseArguments<Parameters>(args)
                .WithParsed(parsedParams =>
                {
                    parameters = parsedParams;
                })
                .WithNotParsed(errors =>
                {
                    Environment.Exit(1);
                });

            //Validate the environment variables
            if (!parameters.Validate())
            {
                string missing = string.Join(", ", parameters.GetMissingEnvironmentVariables());
                Console.WriteLine($"Missing required value(s): {missing}");
                Console.WriteLine(CommandLine.Text.HelpText.AutoBuild(result, null, null));
                Environment.Exit(1);
            }

            Console.WriteLine("Press Control+C to quit the sample.");
            using var cts = parameters.ApplicationRunningTime.HasValue
                ? new CancellationTokenSource(TimeSpan.FromSeconds(parameters.ApplicationRunningTime.Value))
                : new CancellationTokenSource();

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Sample execution cancellation requested; will exit.");
            };

            Console.WriteLine($"Set up the device client.");


            try
            {
                using DeviceClient deviceClient = await SetupDeviceClientAsync(parameters, cts.Token);
                var sample = new CoffeeMaker(deviceClient);
                await sample.PerformOperationsAsync(cts.Token);
                await deviceClient.CloseAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) { } // User canceled operation
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Failed to provision or connect the device.");
                Console.WriteLine("Double-check that ID_SCOPE, DEVICE_ID, and DEVICE_KEY are all correct,");
                Console.WriteLine("and that this device has actually been created under that template in IoT Central.");
                Console.WriteLine();
                Console.WriteLine($"Underlying error: {ex.Message}");
                Environment.Exit(1);
            }
        }
        
        //<Provisioning>
        private static async Task<DeviceClient> SetupDeviceClientAsync(Parameters parameters, CancellationToken cancellationToken)
        {
            // Provision a device via DPS, by sending the PnP model Id as DPS payload.
            using SecurityProvider symmetricKeyProvider = new SecurityProviderSymmetricKey(parameters.DeviceId, parameters.DevicePrimaryKey, null);
            using ProvisioningTransportHandler mqttTransportHandler = new ProvisioningTransportHandlerMqtt();
            ProvisioningDeviceClient pdc = ProvisioningDeviceClient.Create(DpsEndpoint, parameters.IdScope,
                symmetricKeyProvider, mqttTransportHandler);

            var pnpPayload = new ProvisioningRegistrationAdditionalData
            {
                JsonData = $"{{ \"modelId\": \"{ModelId}\" }}",
            };

            DeviceRegistrationResult dpsRegistrationResult = await pdc.RegisterAsync(pnpPayload, cancellationToken);

            // Initialize the device client instance using symmetric key based authentication, over Mqtt protocol (TCP, with fallback over Websocket) and setting the ModelId into ClientOptions.
            DeviceClient deviceClient;

            var authMethod = new DeviceAuthenticationWithRegistrySymmetricKey(dpsRegistrationResult.DeviceId, parameters.DevicePrimaryKey);

            var options = new ClientOptions
            {
                ModelId = ModelId,
            };

            deviceClient = DeviceClient.Create(dpsRegistrationResult.AssignedHub, authMethod, TransportType.Mqtt, options);

            // The SDK retries transient connection failures indefinitely and silently by
            // default - without this handler, a network blip mid-run looks exactly like a
            // frozen app (no error, no output, nothing). This surfaces what's actually
            // happening so a stall is diagnosable instead of a mystery.
            deviceClient.SetConnectionStatusChangesHandler((status, reason) =>
            {
                Console.WriteLine($" ** Connection status changed: {status} (reason: {reason})");
            });

            // Bound the retries instead of the SDK's default of retrying transient faults
            // forever. This gives up after a predictable window (roughly a couple of
            // minutes with this backoff) and throws, so a genuinely broken connection
            // surfaces as an error instead of hanging indefinitely - a real network blip
            // that recovers quickly will still succeed well within this window.
            deviceClient.SetRetryPolicy(new ExponentialBackoffTransportRetryPolicy(
                retryCount: 5,
                minBackoff: TimeSpan.FromSeconds(1),
                maxBackoff: TimeSpan.FromSeconds(30),
                deltaBackoff: TimeSpan.FromSeconds(2)));

            return deviceClient;
        }
        //</Provisioning>
    }
}