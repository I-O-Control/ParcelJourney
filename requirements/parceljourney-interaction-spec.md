# ParcelJourney — Required Interaction and Explanation Specification

## Purpose

ParcelJourney must present the complete, time-oriented journey of one parcel through the Fiege system using the real middleware output. The UI must explain what the system is doing in simple language while retaining the exact technical evidence behind every explanation.

## Data source and processing

- Read the middleware-created parcel journey files, not the full historical log set on every search.
- Support live files and rotated weekly folders such as `KW_38`, `KW_39`, etc.
- Continue tracking a parcel when its events cross file rotation or calendar-week boundaries.
- Store one local journey file per parcel and update it as new positions/events arrive.
- Preserve exact timestamps, source file, line number, layer, location, raw evidence reference, and parsed values.
- Keep the compact dictionary-based schema, but allow unknown future values through extension dictionaries.
- Keep parcel ID, order ID, tracking ID, carrier, and other identifiers as strings; preserve leading zeroes.
- Store numeric measurements in typed units, especially integer grams for weight.

## Journey model

- The journey is chronological from parcel entry to exit.
- Include all correlated system events, not only physical scanner events:
  - physical scanner observations
  - PLC commands
  - PLC acknowledgements
  - transport messages
  - logic decisions
  - database insert/update/read events
  - weighing events
  - workstation allocation
  - label printing and verification
  - volume-reducer events
  - LVS/FWMS messages
  - closure, NIO, fault, or outbound events
- Different events at the same timestamp remain separate if they differ semantically.
- Duplicate events from the same system/layer may be grouped under one expandable entry.
- Events from different layers must remain distinct and visually identifiable.
- The UI must never silently discard an event merely because it is repetitive.

## Physical and virtual locations

- Every event must be associated with a physical station or conveyor position when the code/configuration allows it.
- Virtual events must be linked to the physical station or trigger that caused them.
- Use the known topology and scanner mappings to explain positions such as:
  - `SC_START`: entry scanner and start-weight area
  - `SC_WAAP`: workstation-routing decision scanner
  - `SC_AP`: workstation allocation/processing point
  - `SC_UMR`: quality/manual decision and volume-reducer area
  - `SC_WAVL`: sealer-loop entry decision
  - `SC_VL1` / `SC_VL2`: sealer selection points
  - gross scales, label printers, label verifiers, hall scanners, chutes, and exits
- Do not expose only raw codes when a human-readable station role is known.

## Main floating journey label

The floating label above the current station/position must contain:

1. Exact event time, including milliseconds where available.
2. Human-readable station or position name.
3. Station role, for example `SC_AP — workstation allocation scanner`.
4. Current event trigger explained in plain language.
5. Current event category/layer.
6. Next position or target.

Example structure:

```text
13:21:16.597
Workstation allocation
SC_AP · scanner and workstation-routing point

The scanner reported the parcel. The material-flow logic used this scan
to assign the parcel to workstation 02 and sent the routing instruction
to the PLC.

Next position: Gross scale 4
```

The label must use meaningful explanations rather than presenting values such as `WEIGHTERR`, `PENDING`, `SPSPlcAcknowledge`, or `SC_AP` without translation.

## Simultaneous events and layer tabs

- Events occurring at the same relevant moment and position must be grouped into one visible label.
- The label must provide tabs or left/right arrows to move between layers.
- Each tab represents a real layer, such as:
  - Scanner/physical
  - Logic
  - PLC
  - Transport
  - Database
  - LVS/FWMS
- The active tab must show the explanation for that layer’s event.
- The user must be able to move between tabs without losing the current timestamp or position.
- Same-layer duplicates belong under one expandable layer entry.
- Different-layer events must not be merged into one indistinguishable message.

## Station versus between-station behavior

- If the parcel is currently at a station/position, show the station event label.
- If the parcel is travelling between positions, show a `Next target` label instead.
- The between-position label must show:
  - current/last confirmed position
  - next target position
  - expected next event or action
  - elapsed or remaining travel time when available
- Do not pretend that a parcel is physically at a station before its recorded event occurs.

## Pause and selection behavior

- Clicking the parcel pauses the journey.
- Clicking the floating station/next-target label pauses the journey.
- Clicking a timeline event pauses the journey and moves the replay to that exact event time.
- Changing layer tabs must not resume playback.
- Playback controls must clearly show paused/running state.

## Parcel-data detail label

- Clicking the current station event tab, next-target label, or parcel must open a second label beside the main label.
- This second label must show the parcel’s correlated data at the exact selected timestamp.
- It must be separate from the station explanation label.
- It must include, when available:
  - parcel ID
  - order ID
  - tracking ID
  - principal/customer ID
  - carrier
  - carton type
  - expected gross weight
  - start weight
  - measured gross weight
  - volume-reducer height
  - last scan position
  - PLC target and acknowledged target
  - current database status with human meaning
  - current layer/source
  - exact source evidence
- Values must be the latest known values at that replay timestamp, not merely the final values of the journey.
- If a value is not known at that point, display `Not yet available`, not a later value.
- Clicking outside the detail label closes it.
- Switching the active layer or event updates the detail label to that exact event context.

## Human-readable explanations

The explanation catalog must cover all code-defined outcomes discovered in the service, configuration, and database mappings, including:

- PLC telegram type, counter, sender, receiver, data type, status, and payload length.
- Scanner reads and scanner-to-logic forwarding.
- Routing commands and PLC destination acknowledgements.
- Database lifecycle statuses:
  - `NEW`: parcel record created
  - `PENDING`: parcel remains in processing
  - `WEIGHTERR`: weight outside expected tolerance; special handling may follow
  - `WEIGHTOK`: weight accepted
  - `WORKSTATION`: assigned to a workstation
  - `CLOSED`: outbound processing completed
  - `FAULTY`: required parcel record could not be loaded or was invalid
- Weight values, including conversion from 2D-label encoding to grams.
- LVS/FWMS message types:
  - weight handover
  - carton-height handover
  - outbound-chute handover
- Transport send/acknowledgement and retry/error conditions.
- Label printing, side/top verification, sealer selection, workstation occupancy, telescope, and volume-reducer events.

## Fault tracking and correctness

ParcelJourney must identify and display faults rather than hiding them:

- missing or malformed event data
- unknown enum/dictionary values
- invalid dictionary references
- missing source evidence
- chronology regressions
- scanner event without a corresponding logic interpretation
- routing command without acknowledgement
- acknowledgement without a preceding command
- database state transition that conflicts with the code-defined lifecycle
- parcel appearing at impossible or unmapped topology locations
- weight error and NIO/fault routing
- closure without a valid outbound/exit event
- duplicate/conflicting values at the same time

Faults must be shown in human language with severity and source evidence.

## Storage and reconstruction

- Use dictionary IDs and enums for repeated finite values.
- Use delta timestamps relative to the parcel journey start.
- Use sparse typed event fields instead of repeating field names.
- Store source file IDs and line numbers as compact evidence references.
- Preserve raw lines or stable references when exact raw-log reproduction is required.
- Make clear when exact raw text cannot be reconstructed because the original rotated file is unavailable.
- The compact representation must decode back into the full semantic ParcelJourney used by the UI.

## Acceptance tests

The implementation is acceptable only when:

- all 10 real demonstration parcels can be loaded from the compact feed;
- event counts and parcel identifiers remain correct;
- the journey can be replayed from entry to exit;
- station labels show human explanations and exact times;
- same-time multi-layer events are navigable as tabs;
- the parcel-data detail label shows time-correct `tudata` values;
- clicking the parcel/label pauses playback;
- between-station state shows the next target;
- clicking outside closes the parcel-data label;
- detected faults are visible and tied to evidence;
- the UI does not report a false physical location for a virtual/system-only event.
