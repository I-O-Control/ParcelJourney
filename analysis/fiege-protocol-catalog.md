# Fiege protocol and parcel-field catalog

This is the first source-backed catalog for compact journey encoding. Values are numeric only when the service defines a closed vocabulary; identifiers remain strings because leading zeroes and future alphanumeric values are meaningful.

## PLC telegram header

| Wire field | Example | Meaning | Storage |
|---|---:|---|---|
| `TELTYP` | `S` | `Send` (Senden). `Q` is the short acknowledgement (Quittieren). | enum `TelegramType` |
| `TELCNT` | `4313` | Four-digit telegram counter, 0..9999; used to detect repeats. | `ushort`/`uint` |
| `SEND` | `MFC` | Sender identifier, max 6 chars. | dictionary string |
| `RECV` | `WMF` | Receiver identifier, max 6 chars. | dictionary string |
| `DTYPE` | `03` | Closed `DataTypes` enum. In PLC code, 3 is `SPSPlcAcknowledge`. | enum |
| `TIME` | `13:21:16.597` | Telegram time. | journey-relative milliseconds plus date anchor |
| `STATUS` | `00` | Closed `ErrorCodes`; 0 is `NoError`. | enum |
| `DTALEN` | `0043` | Payload character length. | `ushort` |

For the example `S ... 03 ... 00 0043` on `ConMFRtoLVS`, the code means: a send telegram, counter 4313, from MFC to WMF, carrying an outbound-diversion notification to FWMS, with no protocol error and 43 payload characters. Protocol scope is essential: PLC type 03 means diversion acknowledgement, while LVS type 03 means outbound-diversion notification. Never share a numeric data-type enum across these protocols without its protocol identifier.

## PLC data types and statuses

`DataTypes`: `0 KeepAlive`, `1 SPSScanData`, `2 MFRPlcTarget`, `3 SPSPlcAcknowledge`, `4 MFRRequestWorkstationOccupancy`, `5 SPSWorkstationOccupancy`, `6 MFRRequestTelescopeSwitches`, `7 SPSTelelsopeSwitch`, `8 SPSWaageData`, `9 SPSVolumeReducer`.

`ErrorCodes`: `0 NoError`, `1 ErrTeleCounter`, `2 ErrReceiverId`, `3 ErrDataLength`, `4 KeepAlive`, `5 ErrSameTeleCount`, `11 ErrUnknown`, `12 ErrData`, `13 ErrProcessing`, `14 ErrHeader`, `15 ErrDataType`, `16 ErrDataContent`.

## 2D parcel label

The current barcode parser accepts a 59-character label (and a legacy 58-character commissioning label):

| Positions | Field | Meaning | Storage |
|---|---|---|---|
| 0..9 | `FixValue` | fixed label prefix | dictionary/string |
| 10..12 | `PrincipalID` | principal/customer code | dictionary string |
| 13..22 | `OrderID` | ten-character order number | string |
| 23..42 | `ParcelID` | parcel number, padded field | string |
| 43 | `ControlFlag` | label control character | dictionary/string |
| 44..54 | `GrossWeight` | encoded expected weight | raw string + decoded grams |
| 55..57 | `CarrierID` | carrier/service code | dictionary string |
| 58 | `CartonTypeID` | carton type | dictionary/enum with unknown fallback |

The database model confirms identifiers are strings and weights are numeric: `GrossWeight` is `long`, `WeightStart` is `long`, `WeightBrutto` and `VRNewHeight` are `int`. The 2D weight conversion produces integer grams; for example `00011,50000` becomes `11500` grams. Do not store `ParcelID`, `OrderID`, or tracking IDs as integers: their leading zeroes are part of the identity.

## `tudata` lifecycle

The service declares: `NEW=0`, `PENDING=1`, `WEIGHTERR=2`, `WEIGHTOK=3`, `WORKSTATION=4`, `CLOSED=9`, `FAULTY=99`. These are safe enum candidates, but the persisted value must retain an unknown extension path.

Fields used by the journey: `PrincipalID`, `OrderID`, `ParcelID`, `ControlFlag`, `GrossWeight`, `CarrierID`, `CartonType`, `LastScanPos`, `WeightStart`, `WeightBrutto`, `VRNewHeight`, `PlcTarget`, `TrackingId`, `Status`, `CreateTimestamp`, `UpdateTimestamp`.

## LVS payload

The LVS data formatter is fixed-width: `ZP` + order (10 chars) + parcel (20 chars) + value (8 zero-padded chars) + data type (2 digits). In the supplied payload, `... 0000400803` is not one opaque number: it is the eight-character value `00004008` followed by data type `03`, which the LVS enum names `FWMSÜbergabeWAAusschleusung` (send outbound-chute destination to FWMS). The `UpdateTask()` method is the background queue worker that reads pending `wms_send` records, creates these telegrams, and sends them; it is a process/method event, not a parcel location.

The three LVS data types are: `1 FWMSÜbergabeWiegedaten` (weight), `2 FWMSÜbergabeKartonhöhe` (carton height), `3 FWMSÜbergabeWAAusschleusung` (outbound chute). The value field is typed by data type: grams, height, or chute ID respectively; retain the original text too for exact replay.

## Encoding rule

Use a per-dataset dictionary for locations, methods, layers, senders/receivers, carriers, targets, and unknown strings. Store semantic events as compact records containing delta-time, location ID, event-kind ID, layer ID, status ID, and only the fields present for that event. Keep source file ID/line as evidence references. Exact raw-line reproduction is possible only while referenced rotated files remain available; a compact semantic record alone cannot recreate whitespace or an unrecognised future field.
