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

Controls: drag to pan, Ctrl+drag or right-drag to rotate, scroll to zoom; Isometric/Top change
projection direction, Fit plant resets the view, Focus parcel zooms and follows.
Manual camera interaction releases follow. Space toggles playback outside form
fields. Select any timeline event or use the bottom scrubber to seek backwards.

One contextual HTML callout with an SVG anchor line follows the relevant station.
It appears within 3 replay seconds / 150 schematic units of arrival, remains during
the dwell, and disappears after 1.25 seconds / 90 units of departure. The final
station remains visible while the parcel is there. Results belong to the current
visit only; backward seeking recomputes them. No permanent label rails remain.
Hover other equipment for its name/code and route membership, or click equipment
or the contextual card for the latest observed event in the inspector. Hidden cards
cannot receive pointer or keyboard input. Hover picking is throttled, and paused
rendering still sleeps when there is no input.

The tests cover every synthetic scenario, no future result disclosure, repeat visits,
and contextual arrival, departure and hidden transfer intervals.

The shared 2D/3D palette lives in `ParcelJourney.App/Viewer3d/theme.css`. Theme cycles
through system, light and dark and synchronizes across views on the same origin.
Projected section labels occupy floor gutters and remain legible over equipment.
The floor, clipped grid and camera fit use a convex footprint of the plant and its
sections with 0.9 world units of padding, excluding unused rectangular corners.
See `3D-NEXT-STEPS.md` for the original requirements and implementation details.
