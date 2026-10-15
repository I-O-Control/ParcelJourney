# Physical network and replay review — 2026-10-15

Checkpoint pushed before follow-up fixes: `44456a7` on github/master. The accepted topology is preserved.

## Verified

- Existing 142 directed route identities retained. Shared segments are compiled by tools/physical-network.py. The revised schematic passes strict perpendicular-crossing, four-arm junction, opposing-flow and parallel-clearance checks. Seven independent positive/negative geometry scenarios pass.
- 1,522 historical PJ01 histories decoded: 1,506 have directed graph connectivity for scanner succession; 16 have unresolved transitions. All 275 histories in the 25-minute capture have graph connectivity. These counts include partial histories and are not proof of a unique physical route or correct belt travel speed.
- 53 equipment/scanner pairs match the capture registry. All 50 non-layout source hashes match the capture contract; the layout hash changed intentionally.
- The viewer loads all 84 CLOSED histories from the 25-minute capture with zero loader errors. Fixed string-encoded boolean and empty optional route JSON handling.
- A real exceptional history, 0015913728, has SC_ETIOTL5 -> SC_BWL2 marked Possible external handling, with no connecting edge. Playback hides its unknown intermediate position and shows it at the next recorded arrival; no connecting trail is drawn. Ambiguous mapped corridors retain Route unknown.
- Replay checks: 9,004 trail checks and 309,176 label-state checks passed. Dedicated external-handling playback checks passed.
- The UI fallback extractor had incorrectly escaped regex whitespace and excluded Unicode method names. Corrected and checked with ScanAbzweigungArbeitsplätze. The raw capture text already contains valid German characters.

### Layout text corruption: root cause and prevention

The later screenshot of `Select sealer 1–4` showed genuine double mojibake in authored layout metadata. The previous `da2ba22` revision has the correct label. The layout transformation tools used Python `Path.read_text()` without an explicit encoding, while this Windows host defaults to CP1252. Reading UTF-8 with that default, then writing UTF-8, corrupts punctuation; doing it twice reproduces the observed label exactly. The production-log parser was not the source of this label.

Fixed explicit UTF-8 reads in the layout repair tool, replay-model builder, network audit and generated-journey reader, and explicit UTF-8 report writes. Restored 28 affected metadata fields per layout copy, including the saved local layout, while preserving geometry. `tools/test-layout-encoding.py` runs the actual writer twice on Unicode fixtures under legacy Windows defaults and checks that both metadata and geometry survive unchanged. The viewer build additionally rejects known mojibake markers in its authored model metadata. This guard does not rewrite raw log evidence.

## Still incomplete

- Exact CAD/SCADA coordinate registration. Available CAD section evidence supports arrangement, but the available SCADA extraction provides no direct equipment-prefix matches for the 53 anchors. No nearest-neighbour links were promoted to verified conveyors.
- Independent coverage of every production routing decision, branch condition and target change. The current seven geometry fixtures validate geometry rules, not every possible production journey.
- Measured travel-time bounds. Source timestamps and ordering are retained; interpolation is schematic.
- Confirmed external handling. A protocol class exists in the source, but no active handscanner path or parcel-specific transfer event was established. The 16 exceptional histories remain hypotheses, not confirmed manual moves.
- Full solid collision/elevation validation. Current geometry checks cover centerline crosses, split crosses, opposing flow and parallel rail spacing; they are not a CAD solid intersection engine.

## Reproduce

```powershell
python tools/physical-network.py --capture C:/Users/porfy/Desktop/TempTestParser/Data-live-1.9 --capture C:/Users/porfy/Desktop/TempTestParser/Data-live-MFC-25min-route-topology
node tools/test-external-handling.cjs
```

Generated reports under analysis/physical-network contain real parcel evidence and remain local. Geometry violations produce a nonzero exit code. The parser's separate encoding/manual-handling review records source details and follow-up fixes.
