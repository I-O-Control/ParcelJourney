# ParcelJourney

Standalone parcel journey replay and the future I/O Control GmbH host module.

The current standalone build is a synthetic, schematic plant replay. It contains
source-rule scenarios and a fixed offline viewer so development does not require
customer logs or a database. Customer source folders and logs are intentionally
not part of this repository.

Phase 1 defines a shared parcel-journey contract and adapts the existing
`ParcelHistoryExplorer.Core` timeline into it.

Projects:

- `ParcelJourney.Domain`: host-neutral contracts and journey models.
- `ParcelJourney.Core`: journey builder backed by the existing log correlation engine.
- `ParcelJourney.Standalone`: command-line test harness.
- `ParcelJourney.IocPlugin`: `IocOrchestrator` module adapter.

Standalone test:

```powershell
dotnet run --project .\ParcelJourney.Standalone -- "C:\IOC_MFR\FiegeU\Logs" "0014709760" "ProcLogic*.log"
```

The standalone harness uses full-log mode so it can replay copied/test logs
without requiring the production `tudata` database. The output is JSON containing ordered events, normalized identifiers, source
evidence, duration, and mapping status. The standalone executable has no WPF,
Blazor, database, or `IocOrchestrator` dependency.

The plugin project references the existing `IocOrchestrator.Abstractions` and
registers `IParcelJourneyBuilder` through the normal module loader. It is the
integration seam for the later UI and 3D renderer. The host repository is kept
outside this repository and is not changed by the standalone app.

The current app release targets `net8.0-windows`, matching the inspected
IocOrchestrator project. Build the standalone release with:

```powershell
dotnet publish .\ParcelJourney.App\ParcelJourney.App.csproj -c Release -o .\standalone
```

Run the replay checks with:

```powershell
python .\tools\build_synthetic_replay.py
node .\tools\test-replay-engine.cjs
node .\tools\test-replay-ui.cjs
node .\tools\check-rotated-layout.cjs
```

The repository deliberately excludes published binaries, build output, copied
customer databases/logs, CAD render caches and exploratory viewers.
