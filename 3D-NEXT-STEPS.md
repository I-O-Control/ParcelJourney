# ParcelJourney 3D viewer — next steps

This file records the next visual iteration for the `/3d` spatial replay. It is
intentionally kept in the repository so the plan travels with the implementation.

## 1. Context-aware station labels

Replace the current persistent left/right route labels with contextual callouts:

- Show a station callout only when the parcel is approaching it, dwelling there,
  or has just departed from it for a short, configurable grace period.
- Hide the callout after the parcel has moved on; do not leave a permanent label
  rail around the plant.
- Give the active callout enough space for station name, equipment code, event,
  weight/scanner result, and the next destination. It may be larger than the
  current compact labels because it exists only while relevant.
- Keep the parcel-to-station leader line and use a short fade/slide transition.
- During a dwell, the callout should update in place rather than flicker. For a
  repeated station visit, show the current visit and latest result only.
- When no station is near the parcel, show no station callout; the parcel and
  route/trail remain the visual focus.
- Respect `prefers-reduced-motion` and keep the label work outside the WebGL scene.

Suggested state model: `approaching` (distance/time threshold), `at-station`,
`departed-grace`, and `hidden`. Thresholds should be based on replay time and
route distance, not screen pixels, so zooming does not change the meaning.

## 2. Shared 2D/3D visual theme

Use the existing 2D viewer as the source for dark/light theme tokens:

- Extract shared CSS variables for background, surface, border, text, muted text,
  route, active parcel, success, warning, and exception colors.
- Add a 3D theme toggle that follows the 2D viewer/system preference by default.
- Use the same semantic colors in both views: active parcel, completed path,
  warning/held, exception, and selected station should mean the same thing.
- Keep 3D materials slightly adjusted for lighting/contrast, but do not invent a
  second semantic palette.
- Verify contrast in both themes and with `prefers-reduced-motion` enabled.

## 3. Floor-section labels and occlusion

The floor labels such as `HALL 04`, `HALL 03`, and `PACKAGING` are currently too
close to or underneath the modeled geometry. Reposition them after the plant
bounds are normalized:

- Put labels in reserved floor gutters or on thin, slightly raised plates.
- Raise the label planes enough to avoid z-fighting while keeping them visually
  attached to their section.
- Prefer a camera-facing/legible treatment for the overview, without making the
  labels float disconnected from the floor.
- Test both isometric and top views, plus a zoomed-in parcel-follow view.

## 4. Normalize the model bounds and camera framing

Reduce wasted grid and camera space:

- Compute the visible plant bounds from sections, routes, and equipment rather
  than using a broad fixed floor rectangle.
- Add a small configurable padding around those bounds and remove unused grid
  margins at the outer edges.
- Fit the floor, grid, section labels, and equipment to the same normalized
  bounds so a stray floor corner cannot force an unnecessary zoom-out.
- Recalculate the default orthographic span and `Fit plant` target from those
  bounds; keep a small padding so the outermost equipment is not clipped.
- Confirm that `Focus parcel`, follow mode, panning, and mobile layout still work.

## Verification checklist

- Replay forward, pause at a station, seek backward, and test repeated visits.
- Confirm future scanner/weight results are never shown early.
- Check that hidden labels do not intercept canvas hover/pan input.
- Check label transitions and contrast in dark and light themes.
- Compare isometric/top/follow screenshots at desktop and mobile widths.
- Run `npm test`, `npm run build`, and a Release publish before merging the next
  visual iteration.
