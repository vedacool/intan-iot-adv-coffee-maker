# Connected Coffee Maker — Azure IoT Central Tutorial

A simulated IoT device (a "smart" coffee maker) that connects to **Azure IoT Central**, streams telemetry, reports properties, and responds to remote commands. This project follows the walkthrough in Modul 2 (S3) — *FEC dalam IoT* — slides 36–91.

## What you'll build

A C# console app that pretends to be a coffee maker. It will:

- Stream telemetry: water temperature, air humidity, cup-detected state, brewing state
- Report a read-only property: whether the device warranty has expired
- Accept a writable property: optimal brewing temperature
- Respond to two commands: `SetMaintenanceMode` and `StartBrewing`

All of this is defined by the device's **capability model** (`CoffeeMaker.json`), a DTDL file that both IoT Central and the C# code agree on.

## Scope

This project is built and tested specifically for **Azure IoT Central + Azure CLI / Cloud Shell**, and that's the only combination it's confirmed to work on. A few things worth being upfront about:

- **Verified**: the C# code's logic (provisioning flow, telemetry loop, command handlers, property updates) has been checked for correctness, and the `.csproj` has been fixed to run on Azure Cloud Shell's current .NET runtime. Running it end-to-end against a real Azure IoT Central app and device is on you to confirm, since that requires live Azure credentials this project doesn't have access to.
- **Untested, not necessarily broken**: running this outside Cloud Shell (e.g. on your own Windows/Mac/Linux machine with .NET installed locally), or against a different IoT platform (plain Azure IoT Hub without Central, AWS IoT, Google Cloud IoT, a generic MQTT broker, etc.). It might work as-is, or it might not — the Device Provisioning Service (DPS) flow in `Program.cs` is written specifically against Azure's DPS and IoT Central, so it would need real changes, not just configuration, to point anywhere else.
- **Not intended for**: controlling an actual physical coffee machine. `CoffeeMaker.cs` simulates all its telemetry with random numbers — there's no hardware integration here, only a device *model* that mimics one for learning purposes.

If you do end up running it somewhere else and it works (or doesn't), it's worth updating this section so the scope reflects reality rather than a guess.

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

1. Go to the [Azure Portal](https://portal.azure.com) and sign in with your Azure account.
2. In the portal's left-hand menu (or the home page), select **Create a resource**.
3. Search for **IoT Central Application** in the marketplace and select it — or go directly to the [Create IoT Central Application](https://portal.azure.com/#create/Microsoft.IoTCentral) page, which skips the search step.
4. On the creation form, fill in:
   - **Subscription** — the Azure subscription you want billed
   - **Resource group** — create a new one (e.g. `coffee-maker-rg`) or reuse an existing one
   - **Resource name** — a valid Azure resource name, e.g. `coffee-maker-app`
   - **Application URL** — a unique subdomain, e.g. `coffee-maker-app` (your app will live at `https://coffee-maker-app.azureiotcentral.com`)
   - **Template** — select **Custom application** (not one of the industry templates)
   - **Region** — pick the Azure region closest to you
   - **Pricing plan** — **Standard 2** (the first two devices on any plan are free, so this costs nothing for a single test device)
5. Select **Review + create**. Once validation passes, select **Create**.
6. Wait for the deployment to finish, then select **Go to resource** (or find it later under **All resources**) to open your new IoT Central application.

## Step 2 — Import the device capability model

The capability model tells IoT Central what your coffee maker can do — what telemetry it sends, what properties it has, and what commands it accepts. This project's model lives in `CoffeeMaker.json` in this repo.

1. Inside your IoT Central application, go to **Device templates** in the left-hand menu.
2. Select **+ New**.
3. Choose **IoT device** (a custom device, not one of the pre-built templates), then select **Next: Customize**, then **Next: Review**, then **Create**.
4. On the new template's page, select the **Import model** option, or go to the model's **... (more options)** menu → **Import capability model**.
5. Select the `CoffeeMaker.json` file from this repo (download it locally first if you're not browsing the repo directly from the same machine).
6. IoT Central will parse the file and show you the model interface: **Connected Coffee Maker** (`dtmi:com:example:ConnectedCoffeeMaker;1`), containing:
   - **Telemetry**: `WaterTemperature`, `AirHumidity`, `Brewing`, `CupDetected`
   - **Properties**: `OptimalTemperature` (writable device property), `DeviceWarrantyExpired` (read-only), `CoffeeMakerMinTemperature` and `CoffeeMakerMaxTemperature` (cloud properties, not stored on the device)
   - **Commands**: `SetMaintenanceMode`, `StartBrewing`

## Step 3 — Configure a telemetry view (optional but recommended)

This lets you see live charts of the coffee maker's telemetry instead of just raw numbers.

1. Still inside the device template, select **Views** in the left-hand menu.
2. Select **+ New view** → **Visualizing the device**.
3. Give the view a name (e.g. "Coffee Maker Overview").
4. Drag telemetry tiles onto the canvas and bind them to `WaterTemperature`, `AirHumidity`, `Brewing`, and `CupDetected` so they plot over time.
5. Save the view.

## Step 4 — Set which properties are visible/editable

1. Still inside the device template, go to the **Properties** section (or the "Editing device and cloud data" view if you created one in Step 3).
2. Tick the boxes for the properties you want visible on the device's Properties page — at minimum, tick `OptimalTemperature`, `CoffeeMakerMinTemperature`, `CoffeeMakerMaxTemperature`, and `DeviceWarrantyExpired`.
3. Save.
4. Once you're happy with the template, select **Publish** at the top of the device template page. A template must be published before real devices can use it.

## Step 5 — Add the device instance

1. Go to **Devices** in the left-hand menu.
2. Check whether a "Coffee Machine" device already exists in the list. If it does, open it and skip to Step 6.
3. If not, select **+ New**.
4. Give it a device ID (e.g. `coffeeMachine`) and a display name.
5. Under **Device template**, select **Connected Coffee Maker** (the template you published in Step 4).
6. Select **Create**.

## Step 6 — Get the device connection info

1. Open the device you just created from the **Devices** list.
2. Select **Connect** at the top of the device page.
3. A panel opens showing three values you'll need shortly:
   - **ID scope**
   - **Device ID**
   - **Primary key**
4. Copy all three somewhere safe — you'll paste them as environment variables in Step 7 below.

## Step 7 — Set up and run the code in Azure Cloud Shell

1. Open **Cloud Shell** (Bash) from the Azure Portal, or use the CLI locally.
2. Clone this repo:
   ```bash
   git clone https://github.com/vedacool/intan-iot-adv-coffee-maker.git
   cd intan-iot-adv-coffee-maker
   ```
3. Set your device credentials from Step 6:
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

## Step 8 — Watch it live in IoT Central

1. Back in your IoT Central app, go to **Devices** and open your coffee machine device.
2. On the **Overview** tab you'll see live telemetry plots for temperature, humidity, cup detection, and brewing state.
3. On the **Properties** tab, set **Optimal temperature**, and the app running in Cloud Shell will pick up the change and start using it in its simulated readings.
4. On the **Commands** tab, try running `Start Brewing` — the console output will show the command being received and the brewing state changing for 30 seconds.

## Troubleshooting

**`You must install or update .NET to run this application` / framework version mismatch**
Azure Cloud Shell's installed .NET runtimes drift over time (it may have 9.x or 10.x installed instead of 8.x). The `.csproj` already sets `<RollForward>LatestMajor</RollForward>`, which tells the app to run on whatever major .NET version is available rather than requiring an exact match. If you still see this error, run `dotnet --list-runtimes` to confirm what's installed, then `rm -rf bin obj && dotnet build` to force a clean rebuild.

**Nothing happens / validation error on startup**
`Parameters.Validate()` requires all three of `ID_SCOPE`, `DEVICE_ID`, and `DEVICE_KEY` to be set. If any are missing, the app prints help text and exits immediately — double check your `export` commands from Step 7.

**Where did my values come from again?**
Re-open the device page in IoT Central (Step 6) — **Connect** always shows the current ID scope, device ID, and primary key.

**"Import model" option isn't showing up**
Make sure you're inside a device template you just created (Step 2), not the top-level Device templates list. The import option lives inside a specific template, not as a global action.

## Security note

`DEVICE_KEY` is a live credential for your IoT Hub-provisioned device. Don't commit it to this repository or any public location. If a key is ever exposed, regenerate it from the device's **Connect** page in IoT Central.
