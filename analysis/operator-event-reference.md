# Operator event reference

This reference defines the operator-facing view of a parcel event. The viewer must show what the system actually did before showing an interpretation.

## Display order

1. Exact timestamp.
2. Physical station or system location.
3. Layer: `Logic`, `Db`, `PLC`, `Transport`, `Scanner`, `Printer`, or `Other`.
4. Operation: the method, message, database action, or device action that occurred.
5. Channel or connection, when present.
6. Result or response, including the actual PLC acknowledgement or error.
7. Values carried by the event: parcel ID, tracking ID, target, status, weight, carrier, carton type, and other tudata.
8. Source file and line, expandable for investigation.
9. Human meaning, optional and secondary.

The operator view must not replace technical facts with phrases such as “the parcel requires workstation handling”. Instead show the command, target, response, and state that caused that conclusion.

## Layer handling

| Layer | What it represents | Typical operator fields |
|---|---|---|
| Scanner | A physical read or scanner input | station, scanned ID, raw scanner payload |
| Logic | Application processing and decisions | operation/method, target, decision, status |
| Db | State written to or read from persistence | INSERT/UPDATE/read, affected parcel, stored values |
| PLC | Controller command or acknowledgement | command, target, response, acknowledgement/error |
| Transport | Message sent between services/controllers | connection, telegram type, payload, send/receive result |
| Printer | Label request, print result, or verification | printer, label data, job/result |
| Other | A known event that does not belong to the above layers | original source category and raw operation are required |

`Other` is not an error and must not hide the original log category. Its tab should retain the original source, method/message name, channel, and raw evidence.

## Examples

### Database write

**Primary:** `Db · SaveToDB · result: PENDING`

**Values:** `PlcTarget=WG · TrackingId=<empty>`

**Meaning:** Parcel state was persisted.

### PLC route

**Primary:** `PLC · SendTaskToPlc · target: WG`

**Response:** `OnPlcAcknowledge · acknowledged`

**Meaning:** The controller confirmed the route it used.

### Scanner

**Primary:** `Scanner · OnScannerData · SC_WAVL`

**Values:** scanned parcel ID, carrier, carton type, expected weight, and the original payload.

## Implementation rule

The compact event fields `Source`, `Operation`, `Channel`, and `Outcome` are authoritative when present. Legacy fields (`EventType`, `Summary`, `Layer`, and `LocationId`) remain compatibility fallbacks only. The 2D and 3D viewers should use the same formatter so the same event is displayed identically in both views.
