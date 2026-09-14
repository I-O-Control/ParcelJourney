# ParcelJourney

Standalone parcel journey replay and the future I/O Control GmbH host module.

The standalone build replays 10 completed real Fiege parcels from 11 September
2026. Both 2D and 3D use recorded scanner, PLC and database events with source
file/line evidence. Parcel IDs and linked tracking IDs are searchable. Geometry
and motion between observations remain schematic. The supplied 226 logs were
checked for later occurrences; only final LVS exit notifications follow closure.
See [the case audit](analysis/fiege-real-cases.md). Raw source logs stay outside
the repository; the offline model includes relevant customer log excerpts.

Phase 1 defines a shared parcel-journey contract and adapts the existing
`ParcelHistoryExplorer.Core` timeline into it.

Projects:

- `ParcelJourney.Domain`: host-neutral contracts and journey models.
- `ParcelJourney.Core`: journey builder backed by the existing log correlation engine.
- `ParcelJourney.Standalone`: command-line test harness.
- `ParcelJourney.IocPlugin`: `IocOrchestrator` module adapter.

Standalone test against any compatible log folder:

```powershell
dotnet run --project .\ParcelJourney.Standalone -- --logs "C:\path\to\logs" --id "0014709760" --pattern "ProcLogic*.log" --full
```

The path is supplied at runtime. Rotated files, different dates, and different
log prefixes are supported through glob patterns; the parser correlates the
identifiers it finds in the selected folder instead of relying on the checked-in
Fiege examples. Copy `ParcelJourney.Standalone/appsettings.example.json`, replace
the path and ID, and run `--config path\to\your-settings.json` for repeatable use.

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
python .\tools\import_fiege_logs.py "C:\path\to\logs"
python .\tools\build_fiege_replay.py
node .\tools\test-fiege-import.cjs
node .\tools\test-replay-engine.cjs
node .\tools\test-replay-ui.cjs
npm run build --prefix tools/viewer3d
npm test --prefix tools/viewer3d
```

The repository deliberately excludes published binaries, build output, copied
customer databases/logs, CAD render caches and exploratory viewers.

The committed offline HTML can restore the real JSON model with the 3D build,
without access to source logs. `topology/replay-layout.json` retains the earlier
schematic equipment inventory; only observed scanner transitions are animated.
Legacy synthetic generators are development history and are not the release input.
