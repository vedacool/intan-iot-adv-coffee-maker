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

            // Mqtt_WebSocket_Only forces MQTT over port 443 (looks like ordinary HTTPS)
            // instead of raw MQTT on port 8883. Plain TransportType.Mqtt connects fine
            // initially but many firewalls/proxies reset the raw TCP connection shortly
            // after, causing constant Disconnected_Retrying/Connected cycling even though
            // it keeps recovering. WebSocket-wrapped traffic passes through those far more
            // reliably.
            deviceClient = DeviceClient.Create(dpsRegistrationResult.AssignedHub, authMethod, TransportType.Mqtt_WebSocket_Only, options);

            // The SDK retries transient connection failures indefinitely and silently by
            // default - without this handler, a network blip mid-run looks exactly like a
            // frozen app (no error, no output, nothing). This surfaces what's actually
            // happening so a stall is diagnosable instead of a mystery.
            //
            // The messages below are deliberately plain-language rather than raw SDK enum
            // names. This project gets used in a classroom, and "Disconnected_Retrying" /
            // "Communication_Error" reads like something is broken. In practice a home/school
            // Wi-Fi network dropping and re-establishing the underlying connection every so
            // often is normal, the SDK's automatic retry handles it, and no telemetry is lost
            // (it just resends once reconnected) - only "Disconnected" with no further retry
            // is something actually worth stopping and investigating.
            // Throttled: a flaky network can cycle Disconnected_Retrying -> Connected every
            // few seconds, which would otherwise print a "network blip" line constantly and
            // bury everything else in the console. Instead, only the first blip in a given
            // disconnected episode is printed; further retry attempts before it reconnects
            // are counted silently, and the eventual "Connected" line reports the total.
            bool hasConnectedBefore = false;
            bool blipAlreadyReportedThisEpisode = false;
            int blipCountThisEpisode = 0;
            deviceClient.SetConnectionStatusChangesHandler((status, reason) =>
            {
                switch (status)
                {
                    case ConnectionStatus.Connected:
                        if (hasConnectedBefore && blipCountThisEpisode > 0)
                        {
                            Console.WriteLine(blipCountThisEpisode == 1
                                ? " ** Reconnected to Azure IoT Central. Back to normal."
                                : $" ** Reconnected to Azure IoT Central after {blipCountThisEpisode} retries. Back to normal.");
                        }
                        else if (!hasConnectedBefore)
                        {
                            Console.WriteLine(" ** Connected to Azure IoT Central.");
                        }
                        hasConnectedBefore = true;
                        blipAlreadyReportedThisEpisode = false;
                        blipCountThisEpisode = 0;
                        break;

                    case ConnectionStatus.Disconnected_Retrying:
                        blipCountThisEpisode++;
                        if (!blipAlreadyReportedThisEpisode)
                        {
                            Console.WriteLine(" ** Network blip - connection dropped, reconnecting automatically." +
                                " This is normal on some networks and no telemetry is lost." +
                                " (Further retries are counted silently until it reconnects.)");
                            blipAlreadyReportedThisEpisode = true;
                        }
                        break;

                    case ConnectionStatus.Disconnected:
                        Console.WriteLine(" ** Connection lost and the SDK has stopped retrying." +
                            " This one IS a real problem - check your network and credentials.");
                        break;

                    default:
                        Console.WriteLine($" ** Connection status changed: {status} (reason: {reason})");
                        break;
                }
            });

            // Bound the retries instead of the SDK's default (int.MaxValue - effectively
            // forever) for transient faults. This gives up after a predictable window
            // (roughly a couple of minutes with this backoff) and throws, so a genuinely
            // broken connection surfaces as an error instead of hanging indefinitely - a
            // real network blip that recovers quickly will still succeed well within this window.
            deviceClient.SetRetryPolicy(new ExponentialBackoff(
                retryCount: 5,
                minBackoff: TimeSpan.FromSeconds(1),
                maxBackoff: TimeSpan.FromSeconds(30),
                deltaBackoff: TimeSpan.FromSeconds(2)));

            return deviceClient;
        }
        //</Provisioning>
    }
}