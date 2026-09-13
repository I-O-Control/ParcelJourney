# Spatial replay prototype

Open `/3d` on the standalone app's loopback URL. The original `/` viewer is retained.
Three.js, OrbitControls and the shared replay engine are bundled locally; no CDN,
telemetry, 3D model downloads or runtime Node installation are needed.

Rebuild after changing `viewer.js`, `trail.mjs`, `route-labels.mjs` or the shared replay engine:

```powershell
cd tools/viewer3d
npm ci
npm run build
npm test
cd ../..
dotnet publish ./ParcelJourney.App -c Release -o ./standalone
```

The build restores ignored synthetic JSON artifacts from the committed offline
HTML viewer, so it does not need the original machine's customer source folders.
Commit the generated `ParcelJourney.App/Viewer3d/viewer.js` and license along with
source changes. HTML and CSS in that directory are authored directly.

Geometry heights/shapes are illustrative; the horizontal topology and simulation
timestamps come from the existing model. The 3D renderer requires WebGL2, with a
visible link to the 2D viewer if initialization fails. Geometry is instanced,
pixel ratio is capped at 1.5, and there are no realtime shadows/postprocessing.
Rendering sleeps while paused until a view/control changes. Follow, parcel and
trail use a single frame clock. Inspector text updates at 10 Hz during playback.

Controls: drag to orbit, right-drag to pan, scroll to zoom; Isometric/Top change
projection direction, Fit plant resets the view, Focus parcel zooms and follows.
Manual camera interaction releases follow. Space toggles playback outside form
fields. Select any timeline event or use the bottom scrubber to seek backwards.

Selected-route stations have persistent HTML labels with SVG anchor lines, not
extra WebGL objects. Visited / Here now / Next / Later states and observed results
are derived from replay time, including repeat visits and backward seeks. Offscreen
stations are counted in the legend; Fit plant restores the overview. Label placement
prefers edge rails where possible and switches to compact slots when crowded.
Hover other equipment for its name/code and route membership, or click equipment
or a route label for the latest observed event in the inspector. Keyboard focus on
route labels also reveals the tooltip. Hover picking is throttled, layout is reused
between playback passes, and paused rendering still sleeps when there is no input.

The tests cover every synthetic scenario, no future result disclosure, repeat visits,
and representative non-overlapping label layouts with a protected parcel area.
