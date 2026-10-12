# Physical layout editor

Open **Edit layout** from either replay screen. The separate editor starts with
the active model (including loaded capture stations), or the last saved layout.

- Drag stations; their connected route endpoints move with them. Coordinates,
  labels, equipment codes and station types are also editable in the inspector.
- Select a connection and double-click it to insert a bend. Drag the white
  handles, enter bend coordinates, remove bends or use the right-angle preset.
- Add stations and directed connections. A branch uses several connections
  from one station; insert a junction and replace a shortcut with two connections
  when the branch begins between scanners. New station IDs can be edited before
  linking them to captures; existing source IDs stay fixed.
- Undo/redo covers the draft. Pan, zoom, station search and grid snapping are
  available. Leaving with unsaved changes triggers the browser's warning.
- **Save & return** validates the graph, atomically saves it and returns to the
  originating 2D/3D view. Existing tabs need a reload to see changes.
- Export/import JSON transfers the layout. Import only replaces the draft;
  saving applies it. Keep exports as named layout versions or backups.

The app currently stores one plant layout at
`%LOCALAPPDATA%\ParcelJourney\layout.json`, independently of its random HTTP
port and independently of `.pjourney` evidence. `PARCELJOURNEY_LAYOUT_PATH`
overrides that location for isolated tests. Conflicting saves from two editors
are rejected until the stale editor reloads.

Replay keeps a recorded leg's existing edge ID when that edge remains in the
layout. If it was removed, it uses the shortest directed physical path between
the recorded endpoints. Intermediate junctions do not become observed events.
This path is a layout-based interpolation, not proof of the parcel's precise
physical route. For alternate physical paths between identical endpoints, edit
the original leg connection to explicitly draw the intended route.

A save cannot remove a station needed by the loaded capture or disconnect its
recorded legs. If a later capture is incompatible, the viewer explicitly warns
and uses its original schematic geometry. Open the editor, use **Add missing
capture stations**, place/connect them, and save again. The internal
`UNOBSERVED_POSITION` sentinel is retained automatically.

Validation: `dotnet run --project tools/LayoutChecks/LayoutChecks.csproj`.
Build/publish: `dotnet publish ParcelJourney.App/ParcelJourney.App.csproj -c Release -o standalone/layout-editor-preview`.
