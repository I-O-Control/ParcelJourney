# Parcel data extraction and minimum-storage instructions

## Purpose

This document defines what the middleware should extract from Fiege logs and how the parcel output should store it with the smallest safe representation.

This is an analysis and storage specification. It does not change the existing middleware or ParcelJourney implementation.

The target is **minimum storage without loss of journey meaning**. Repeated vocabulary may be dictionary-coded; event-specific values and evidence must not be discarded.

## Results of the three analysis passes

### Pass 1 — service and parser code

The code and current parser expose these event-level concepts:

- timestamp, source file, line number and raw line
- event type, layer/source system and system filename prefix
- scanner/location/phase and location confidence
- parcel ID, order ID, tracking ID and other identifiers
- PLC target and status
- database fields from `tudata`
- evidence references
- live-file offsets, partial-line state, weekly partition and completeness

The current parser explicitly recognizes database updates, scanner data, PLC acknowledgements, packed `ZP` identifiers and tracking fields. The semantic classifier now has the vocabulary for Logic, PLC, Transport, LVS, Camera, UI, Labeler and Other, plus operation/channel/outcome dimensions.

The code also shows additional possible outcomes that must be represented even if a particular demo feed does not contain them:

- scanner input and scanner acknowledgement
- route command and route acknowledgement
- database insert/update
- weight publication to UI
- camera notification
- label request, print-file search, print-file found/missing, fallback label and tracking-ID assignment
- LVS/transport send and receive
- close, NIO, no-read, error and fault paths

### Pass 2 — real log and journey data

The real dataset contains 10 reconstructed parcels and 9,525 events. It demonstrates that one parcel can use several parallel paths at nearly the same moment:

```text
Scanner/PLC input
  -> Logic processing
  -> database state update
  -> PLC command or acknowledgement
  -> UI weight publication
  -> camera notification
  -> labeler processing
  -> transport/LVS exchange
  -> close or exception outcome
```

Observed source families include:

| Source family | Meaning | Important values |
|---|---|---|
| `ProcLogic*` | material-flow and routing decisions | scanner, target, status, database values |
| `ProcPLC*` | PLC-side RPC and acknowledgements | scanner ID, station, barcode, target, direction |
| `Con*` | communication telegrams | sender, receiver, type, counter, length, payload |
| `ProcCamera*` | sends parcel/weight information to camera | scale, order, parcel, weight, camera timestamp |
| `RemoteManagement*` | publishes scale data to operator UI | scale ID, actual/target weight, tolerance, timestamp |
| `ProcEtikettierer*` | label lookup, printing and tracking assignment | scanner, printer, ZPL file, retries, tracking ID |
| `ProcLVS*` | warehouse-system exchange | message direction, payload, acknowledgement |

Observed database fields include `PrincipalID`, `OrderID`, `ParcelID`, `ControlFlag`, `GrossWeight`, `CarrierID`, `CartonType`, `LastScanPos`, `WeightStart`, `WeightBrutto`, `PlcTarget`, `TrackingId`, `Status` and `VRNewHeight`.

Important observation: the event sequence is not fixed. The reusable structure is the set of possible event types and correlations, not a mandatory order.

### Pass 3 — minimum safe storage

Every candidate value falls into one of four storage classes:

1. **Dictionary value** — stored once and referenced by an integer.
2. **Enum value** — finite, stable vocabulary represented by a small integer.
3. **Typed event value** — stored only when that event contains it.
4. **Evidence** — source file/line, and optionally raw text, retained for verification and reconstruction.

## Required parcel record

Each parcel output should contain:

```text
parcel identity and aliases
first and last event time
completion/completeness state
source partitions
ordered sparse event records
dictionary version
evidence references
```

It should not contain a copy of the global dictionaries.

## Required event record

The smallest semantically complete event consists of:

```text
timestamp delta
source ID
operation ID
channel ID
outcome ID
position ID, when known
status ID, when present
correlation ID(s), when present
typed values present in this log line
evidence reference(s)
```

Recommended compact shape:

```json
{
  "dt": 1542,
  "src": 5,
  "op": 18,
  "ch": 2,
  "out": 3,
  "pos": 31,
  "status": 2,
  "ids": [0, 4],
  "v": { "actualWeight": 2400, "targetWeight": 2774, "tolerance": 5 },
  "ev": [[12, 12733]]
}
```

`dt` is relative to the previous event in milliseconds. Exact source timestamps remain available through the evidence record or a configured timestamp precision policy.

## Global dictionaries and enums

### Stable enums

Use numeric enums for small, controlled vocabularies:

- source: Logic, PLC, Transport, LVS, Camera, UI, Labeler, Database, Unknown
- channel: Log, RPC, PLC, Database, FileSystem, UI, Transport
- outcome: Started, Sent, Received, Accepted, Rejected, Completed, Missing, Failed, Retried, Observed
- status: NEW, PENDING, WEIGHTERR, WEIGHTOK, WORKSTATION, CLOSED, NIO, NOREAD, ERROR, FAULTY
- operation kind: Scan, Weight, StateUpdate, RouteCommand, RouteAcknowledgement, CameraNotification, WeightPublished, LabelRequested, PrintFileSearch, PrintFileFound, LabelPrint, TrackingIdAssigned, TransportMessage, LVSMessage, Close

### Dictionaries

Use dataset-level dictionaries for values that can grow or vary:

- source filenames and rotated-file IDs
- method names
- physical positions and scanner names
- scanner IDs and PLC IDs
- equipment and connection names
- PLC targets and chute IDs
- sender/receiver names
- telegram codes
- printer connections
- ZPL filenames
- error text and unknown status values
- carrier, carton and customer codes

## Values that must remain typed per event

These cannot safely be inferred later and must be stored when present:

- actual, expected and tolerance weight
- gross/brutto weight
- reducer height
- PLC target and actual target
- telegram counter and payload length
- scanner ID and equipment ID
- retry count
- tracking ID assigned by the labeler
- printer/ZPL file result
- database status transition
- NIO/no-read/fault reason
- embedded timestamps that differ from the log timestamp

Leading zeroes must be preserved. IDs must remain strings, even when numeric-looking.

## Evidence policy

Evidence is required for every semantic event:

```text
file dictionary ID + line number
```

Raw text should be retained in one of two ways:

- source logs remain available: store only file/line references;
- source logs may disappear: store deduplicated raw evidence separately by hash.

Raw text must not be repeated inside every parcel event unless it is the configured evidence policy.

## Correlation requirements

Events must not be merged solely because they share a timestamp. They should be grouped only for display. The stored records remain separate and are linked using:

- parcel/order/tracking ID
- scanner ID and station
- request/response direction
- PLC counter or telegram identity
- target
- short time window
- source connection

This preserves parallel paths while allowing ParcelJourney to show one human-readable moment with expandable events.

## Information currently at risk of being lost semantically

The current raw evidence protects these values, but the parser does not yet consistently extract them into typed fields:

- telegram sender, receiver, type, counter and payload length
- PLC request/acknowledgement pairing
- scanner ID versus scanner name
- camera timestamp and configured time adjustment
- scale ID and measurement timestamp
- labeler printer connection and retry outcome
- ZPL filename and fallback-label choice
- explicit error/fault/timeout reason
- connection direction and transport result
- embedded identifiers that are not 10-digit parcel IDs or 10–24 digit tracking IDs

These are the priority candidates for future parser improvements because they affect whether the journey can be explained or faults can be proven.

## Derived values that should not be stored per event

These can be produced by ParcelJourney from dictionaries and event records:

- display label for a source or operation
- natural-language explanation
- elapsed duration
- current parcel state
- next known position
- route grouping for the UI
- layer tab color
- station display name
- duplicate count for same-time display

## Completeness rules

A parcel is not complete merely because the latest event is a database update. Completion must be based on an explicit terminal outcome such as closed, NIO, no-read or a configured exit acknowledgement.

If the first observed event is not a parcel-entry event, store:

```text
startCompleteness = Partial
```

If the parcel remains active at the end of the watched feed, store:

```text
endCompleteness = Open
```

This distinguishes “not found in the logs” from “not yet finished.”

## Final recommendation

The smallest safe representation is not one compressed string. It is:

```text
dictionary-coded sparse event records
+ typed values only when present
+ compact identity/correlation references
+ file/line evidence references
+ optional deduplicated raw evidence
```

This supports different parcel paths without forcing unused fields or a common event sequence, while retaining enough information for ParcelJourney to reconstruct, explain and validate the original journey.
