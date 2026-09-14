# Local setup and IocOrchestrator plugin publishing

## Standalone replay

Install .NET 8 SDK, Python 3.12 or newer, and Node.js 20 or newer. From the repository root:

```powershell
npm ci --prefix tools/viewer3d
dotnet run --project .\ParcelJourney.App -- --no-browser
```

The app prints a loopback URL. Open `/` for the 2D replay or `/3d` for the 3D replay. The checked-in model contains ten completed Fiege journeys and their source evidence; the original customer logs are intentionally excluded.

The checked-in 10-case model is a portable demonstration dataset. For a live
or different log capture, use the standalone reader and provide the actual
folder at runtime:

```powershell
dotnet run --project .\ParcelJourney.Standalone -- `
  --logs "C:\path\to\actual\logs" `
  --id "parcel-or-order-id" `
  --pattern "*.log" --full
```

The reader recursively handles rotated files matching the supplied patterns,
normalizes identifiers from each log family, and preserves source file/line
evidence. It does not assume the Fiege filenames, date, scanner count, carrier,
or route used by the checked-in examples. Use `ParcelJourney.Standalone\appsettings.example.json`
as a configuration template when the path and search ID should be stored locally.

To rebuild the model from the original log capture, keep that capture outside this repository and run:

```powershell
python .\tools\import_fiege_logs.py "C:\path\to\Fiege_Manual_Unloading"
python .\tools\build_fiege_replay.py
```

Do not commit the source logs, databases, published binaries, `bin/`, `obj/`, or `node_modules/`.

## Validation

```powershell
node .\tools\test-fiege-import.cjs
node .\tools\test-replay-engine.cjs
node .\tools\test-replay-ui.cjs
npm test --prefix tools/viewer3d
dotnet publish .\ParcelJourney.App\ParcelJourney.App.csproj -c Release -o .\standalone
```

## Build the IocOrchestrator plugin

The plugin adapter depends on two sibling source checkouts that are intentionally not copied into this repository:

- `ParcelHistoryExplorer` containing `ParcelHistoryExplorer.Core`;
- `IocOrchestrator` containing `IocOrchestrator.Abstractions` and the host.

Clone both repositories beside `ParcelJourney`, or pass their absolute roots explicitly. Build the adapter with:

```powershell
dotnet build .\ParcelJourney.IocPlugin\ParcelJourney.IocPlugin.csproj `
  -p:ParcelHistoryExplorerRoot="C:\src\ParcelHistoryExplorer" `
  -p:IocOrchestratorRoot="C:\src\IocOrchestrator"
```

To publish into a host checkout's plugin folder, use the same command with `-c Release`. The project copies the plugin assembly, `plugin.json`, dependencies, and `wwwroot` assets to:

```text
<IocOrchestratorRoot>\bin\Release\net8.0-windows\plugins\parcel-journey\
```

Start IocOrchestrator, confirm the Parcel Journey navigation entry appears under Logistics, and open it. The adapter registers `IParcelJourneyBuilder`; it does not require the standalone executable.

The project file no longer contains machine-specific absolute paths. `Directory.Build.props` supplies sibling defaults, while the command-line properties above are the portable option for CI or another developer workstation.

### Large log folders
Set PARCELJOURNEY_SOURCE_FOLDER before starting the app to warm the persistent identity index at startup. The UI also has **Choose folder…**, which opens a Windows folder picker. Index files are cached and reused when logs are unchanged; searches include subfolders and return events in timestamp order.
