# Connected Coffee Maker — Azure IoT Central Tutorial

A simulated IoT device (a "smart" coffee maker) that connects to **Azure IoT Central**, streams telemetry, reports properties, and responds to remote commands. This project follows the walkthrough in Modul 2 (S3) — *FEC dalam IoT* — slides 36–91.

## What you'll build

A C# console app that pretends to be a coffee maker. It will:

- Stream telemetry: water temperature, air humidity, cup-detected state, brewing state
- Report a read-only property: whether the device warranty has expired
- Accept a writable property: optimal brewing temperature
- Respond to two commands: `SetMaintenanceMode` and `StartBrewing`

All of this is defined by the device's **capability model** (`CoffeeMaker.json`), a DTDL file that both IoT Central and the C# code agree on.

## Repository contents

| File | Purpose |
| --- | --- |
| `CoffeeMaker.json` | DTDL device capability model — telemetry, properties, commands |
| `CoffeeMaker-csharp.csproj` | .NET project file (targets net8.0, rolls forward to whatever runtime is installed) |
| `Program.cs` | Provisions the device via DPS and opens the connection to IoT Central |
| `Parameters.cs` | Reads `ID_SCOPE` / `DEVICE_ID` / `DEVICE_KEY` from the environment |
| `CoffeeMaker.cs` | The simulated device logic — telemetry loop, command handlers, property updates |

## Prerequisites

- An Azure account with an active subscription
- Access to the [Azure Portal](https://portal.azure.com) and Azure Cloud Shell (Bash)
- This repository cloned or downloaded locally / into Cloud Shell

## Step 1 — Create an IoT Central application

1. Go to the [Azure Portal](https://portal.azure.com) and sign in.
2. Search for **IoT Central** and select **Create**, or go directly to the [Create IoT Central Application](https://portal.azure.com/#create/Microsoft.IoTCentral) page.
3. Fill in:
   - **Subscription** and **Resource group** of your choice
   - **Resource name** and **Application URL** (e.g. `my-coffee-maker`)
   - **Template**: Custom application
   - **Pricing plan**: Standard 2 (first two devices are free)
4. Select **Review + create**, then **Create**.

## Step 2 — Import the device template

1. Open your new IoT Central application.
2. Go to **Device templates** → **+ New** → **Import a model**.
3. Upload `CoffeeMaker.json` from this repo. This defines the "Connected Coffee Maker" model (`dtmi:com:example:ConnectedCoffeeMaker;1`) with:
   - Telemetry: `WaterTemperature`, `AirHumidity`, `Brewing`, `CupDetected`
   - Properties: `OptimalTemperature` (writable), `DeviceWarrantyExpired` (read-only), `CoffeeMakerMinTemperature`, `CoffeeMakerMaxTemperature` (cloud properties)
   - Commands: `SetMaintenanceMode`, `StartBrewing`
4. Publish the template.

## Step 3 — Add a device and get connection info

1. Go to **Devices** → **+ New**.
2. Give it a device ID (e.g. `coffeeMachine`) and assign it the Connected Coffee Maker template.
3. Open the device, select **Connect**, and copy:
   - **ID scope**
   - **Device ID**
   - **Primary key**

You'll use these three values as environment variables when running the C# app.

## Step 4 — Set up and run the code in Azure Cloud Shell

1. Open **Cloud Shell** (Bash) from the Azure Portal, or use the CLI locally.
2. Clone this repo:
   ```bash
   git clone https://github.com/vedacool/intan-iot-adv-coffee-maker.git
   cd intan-iot-adv-coffee-maker
   ```
3. Set your device credentials from Step 3:
   ```bash
   export ID_SCOPE=<your ID scope>
   export DEVICE_ID=<your Device ID>
   export DEVICE_KEY=<your Primary key>
   ```
4. Restore, build, and run:
   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```
5. You should see:
   ```
   Press Control+C to quit the sample.
   Set up the device client.
   Device successfully connected to Azure IoT Central
   - Set handler for "SetMaintenanceMode" command.
   - Set handler for "StartBrewing" command.
   - Set handler to receive "OptimalTemperature" updates.
   - Update "DeviceWarrantyExpired" reported property on the initial startup.
   Telemetry send: Temperature: 96.4ºC Humidity: 23.3% Cup Detected: Y Brewing: N Maintenance Mode: N
   ```

## Step 5 — Watch it live in IoT Central

1. Back in your IoT Central app, go to **Devices** and open your coffee machine device.
2. On the **Overview** tab you'll see live telemetry plots for temperature, humidity, cup detection, and brewing state.
3. On the **Properties** tab, set **Optimal temperature**, and the app running in Cloud Shell will pick up the change and start using it in its simulated readings.
4. On the **Commands** tab, try running `Start Brewing` — the console output will show the command being received and the brewing state changing for 30 seconds.

## Troubleshooting

**`You must install or update .NET to run this application` / framework version mismatch**
Azure Cloud Shell's installed .NET runtimes drift over time (it may have 9.x or 10.x installed instead of 8.x). The `.csproj` already sets `<RollForward>LatestMajor</RollForward>`, which tells the app to run on whatever major .NET version is available rather than requiring an exact match. If you still see this error, run `dotnet --list-runtimes` to confirm what's installed, then `rm -rf bin obj && dotnet build` to force a clean rebuild.

**Nothing happens / validation error on startup**
`Parameters.Validate()` requires all three of `ID_SCOPE`, `DEVICE_ID`, and `DEVICE_KEY` to be set. If any are missing, the app prints help text and exits immediately — double check your `export` commands from Step 4.

**Where did my values come from again?**
Re-open the device page in IoT Central (Step 3) — **Connect** always shows the current ID scope, device ID, and primary key.

## Security note

`DEVICE_KEY` is a live credential for your IoT Hub-provisioned device. Don't commit it to this repository or any public location. If a key is ever exposed, regenerate it from the device's **Connect** page in IoT Central.
