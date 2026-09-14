# ParcelJourney – Remaining Improvements

## Current state

The application builds successfully and supports:

- Real Fiege log folders selected at runtime.
- Recursive log discovery and persistent identity indexing.
- Folder selection through the Windows folder picker.
- Parcel, order and tracking ID searches.
- Fallback full-log scans when a foreign system has no local database metadata.
- Chronological event ordering and display of source evidence.
- The existing validated Fiege demo replay.

## Required next improvement: live replay model

The current log search returns a correct `ParcelJourney` event list, but the embedded replay viewer still uses its bundled demo model for the animated route. The next implementation should add a dedicated adapter that converts real events into the viewer model:

1. Map `ScannerId` and `ScannerName` to the observed equipment nodes.
2. Build `stops` from timestamped scanner and equipment events.
3. Build `legs` between consecutive observed nodes using the real time intervals.
4. Preserve repeated scans, recirculation, retries and close events.
5. Mark unknown or unmapped equipment clearly instead of silently assigning a synthetic location.
6. Replace the selected demo parcel only after the real event list has been validated.
7. Start the parcel at the entry scan with a short loading/packing animation.
8. Enable **Play**, **Next**, **Previous**, seek and event details from the real timeline.
9. Keep the original event evidence (file and line number) visible for every action.
10. Add automated tests using the ten audited Fiege journeys and at least one foreign-system log folder.

The adapter must be tested with real logs before being treated as production-ready. Synthetic routes should remain only as a fallback demo when no source folder is configured.

## Operational improvements

- Show indexing progress and the number of files indexed.
- Cache indexes per source folder and invalidate changed files only.
- Add cancellation for searches over very large folders.
- Add a clear “no matching parcel” result with the searched folder and patterns.
- Add a small smoke-test command that verifies the selected folder, index and one known parcel.
