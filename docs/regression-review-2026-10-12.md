# ParcelJourney regression review — 2026-10-12

Compared the local successful replay output with commit `5926959` (the viewer commit), using `e6694b3` as its parent. No product files were changed during this review. The diagnostic app build succeeded with 0 warnings and 0 errors.

## Main finding: the local successful run and the committed app use different artifacts

The successful local replay output is still present in the working folder:

- `analysis/equipment-replay.html` — 5,122,584 bytes, generated 2026-10-12 13:45.
- `analysis/fiege-replay-model.json` — 5,060,234 bytes, generated at the same time.
- `tools/build_fiege_replay.py` creates these from local inputs including `tmp/fiege-index.json` and `topology/live-1.8-route-observations.json`.

Those generated outputs are ignored by Git. The packaged app instead embeds `ParcelJourney.App/Assets/equipment-replay.html` and `ParcelJourney.App/Assets/replay-model.json`, as listed in `ParcelJourney.App/ParcelJourney.App.csproj`. The app therefore does not use the exact HTML/model from the successful local run.

## Differences measured

| | Successful local model | Bundled app model |
|---|---:|---:|
| Nodes | 120 (117 physical) | 117 (all physical) |
| Directed edges | 142 | 142 |
| Parcels | 30 | 0 until a folder is loaded |

Correction to the first comparison: 188 edges belonged to the older Git baseline, not the successful equipment run. The local equipment run and bundled app have the same 117 physical node IDs and 142 edge IDs. Their actual geometric difference is three nodes and three edges: `H3_CHUTE1`, `H3_CHUTE2`, and `H3_CHUTE3` are at x=-100 in the successful run but at x=1660/1750/1840 in the bundled default. The three `SC_H3RU` outbound paths consequently take the opposite route across Hall 3. The saved per-user layout already has the successful left-side coordinates, which masked this regression on the development computer. A fresh install has no saved layout and gets the incorrect embedded default.

The local generated 2D HTML and bundled HTML serve different purposes. The local generated HTML embeds real parcels and assumes a selected parcel in several paths; it is not a safe zero-parcel template. The app's bundled HTML is the dataset-agnostic template and already handles the zero-parcel startup. The UI regression was that its new optional folder browser appeared alongside legacy scenario/search controls, and the hidden parcel bubble could still be displayed because a later CSS rule overrode the `hidden` attribute. These empty-state issues are corrected in the working tree.

## UI and startup observations

- The 2D app showed the new folder browser inside the existing inspector, while the old `Scenario` label/select and `Find` control remained below it. Those legacy controls are now hidden when no journeys are loaded; the empty parcel bubble is hidden and playback controls are visibly disabled.
- The 2D layout renders, but starts with no journeys loaded by design. The saved folder catalogue reports 770 ParcelIDs; that does not mean they have been loaded.
- The 3D route displayed “Preparing scene…” with no canvas when no parcel was loaded. The viewer now builds and displays the static plant layout without requiring a journey.
- The tested 2D CSS palette variables (`--bg`, `--trace`, `--map`, and related tokens) match between the last local generated HTML and the bundled HTML. The review did not find a palette-token change that explains the reported colors. The scene and active parcel state are materially different, and the 3D startup still needs a focused check.

## Conclusion

The concrete topology regression was the three misplaced Hall 3 chute routes. The startup/UI regressions were the 3D plant not rendering until a journey was loaded, and legacy empty-state controls/bubble cluttering the 2D viewer. The current working tree matches the successful equipment geometry while retaining an empty-safe, optional-journey startup. Colors were not established as a root cause by the CSS palette comparison; the requested correction is now tied to concrete geometry and UI behavior observed in the successful run.
