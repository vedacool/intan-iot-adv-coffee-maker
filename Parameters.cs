// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using CommandLine;

namespace Learn.CoffeeMaker
{
    /// <summary>
    /// Command line parameters for the SimulatedDevice sample
    /// </summary>
    internal class Parameters
    {
        [Option(
            'i',
            "IdScope",
            HelpText = "The ID Scope to use during device provisioning." +
            "\nDefaults to environment variable 'ID_SCOPE'.")]
        public string IdScope { get; set; } = Environment.GetEnvironmentVariable("ID_SCOPE");

        [Option(
            'd',
            "DeviceId",
            HelpText = "The device Id to use during device provisioning." +
            "\nDefaults to environment variable 'DEVICE_ID'.")]
        public string DeviceId { get; set; } = Environment.GetEnvironmentVariable("DEVICE_ID");

        [Option(
            'k',
            "DevicePrimaryKey",
            HelpText = "The device Primary key to use during device provisioning." +
            "\nDefaults to environment variable 'DEVICE_KEY'.")]
        public string DevicePrimaryKey { get; set; } = Environment.GetEnvironmentVariable("DEVICE_KEY");

        [Option(
            'n',
            "Application running time (in seconds)",
            Required = false,
            HelpText = "The running time for this console application. Leave it unassigned to run the application until it is explicitly canceled using Control+C.")]
        public double? ApplicationRunningTime { get; set; }

        public bool Validate()
        {
            return !string.IsNullOrWhiteSpace(IdScope)
                    && !string.IsNullOrWhiteSpace(DeviceId)
                    && !string.IsNullOrWhiteSpace(DevicePrimaryKey);
        }

        // Names the specific environment variables that are missing, so a validation
        // failure can say exactly what to fix instead of just printing generic usage text.
        public IEnumerable<string> GetMissingEnvironmentVariables()
        {
            if (string.IsNullOrWhiteSpace(IdScope)) yield return "ID_SCOPE";
            if (string.IsNullOrWhiteSpace(DeviceId)) yield return "DEVICE_ID";
            if (string.IsNullOrWhiteSpace(DevicePrimaryKey)) yield return "DEVICE_KEY";
        }
    }
}
