# Implementation status — 2026-09-17

This document records tested behavior, not completion of the full specification.

## Implemented and checked

- Replaced lossy v1 compact conversion with reversible v2 dictionaries plus gzip. Unknown fields, explicit nulls, missing fields, raw evidence, identifiers, ordering and metadata survive.
- Separate compressed file per parcel; middleware emits the same format using .NET Framework 4.8.
- Reader rejects unsupported or malformed compact structures explicitly. Unknown business values are not rejected.
- All 9,525 source events across 10 parcels compare equal field-for-field after decoding. C# reconstructed journeys compare equal to the original reader output.
- Compressed parcel files total 220,174 bytes versus 3,481,163 source JSON bytes. This includes raw evidence; there is no claim of less than 1 KB per parcel.
- Unknown search no longer returns the first unrelated parcel.
- Middleware writes only dirty parcel snapshots, replaces files atomically, reads bounded chunks, and maintains line numbers across polls.
- 3D labels use exact time, actual layer tabs, expandable identical same-system observations, explanations, next-target travel mode, click-to-pause and separate as-of-time database values with evidence.
- No future values are used by the state reducer. Millisecond comparisons avoid floating-point boundary errors.
- Additional tests cover unknown statuses and fields, alphanumeric identifiers, zero measurements, same-time cross-layer events, duplicates and midnight.

## Still required before production completion

- Exhaustive source/configuration/database branch audit and versioned semantic catalog. Current explanations cover common observed event families; unfamiliar cases retain evidence and say their meaning is unmapped.
- Full command/acknowledgement pairing, configured topology validation, conflict reporting and closure validation. Current warnings expose recorded WEIGHTERR/FAULTY observations, not a complete fault engine.
- Durable restart checkpoints, file-identity-aware rotation, bounded retained history and indexed alias lookup. The existing live watcher still recursively discovers history and retains its index in memory; it is not yet production-ready.
- Shared behavior in the 2D UI, and production route generation for arbitrary newly stored journeys. The 3D preview uses existing demo route geometry enriched from decoded compact observations.
- Source-backed station roles and target interpretations for every deployment configuration. A raw new target remains valid pending interpretation.

## Reproduce

```powershell
python tools/compact_real_feed.py real-feed/parcels analysis/compact-real-10.json
dotnet run --project tools/CompactVerification -- .
node tools/viewer3d/test-parcel-state.mjs
```

The JSON round trip proves preservation of normalized values and raw log strings, not byte-identical JSON whitespace or exhaustive coverage of service behavior.
