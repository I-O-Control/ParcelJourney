# Parcel Log Middleware

External, file-backed parcel journey reader. This project is intentionally independent of `ParcelJourney`.

Target runtime: .NET Framework 4.8, matching the Fiege `MaterialFlowControlSvc` and plugins. It watches a log root recursively, including `KW_##` folders, reconstructs records split at rotation boundaries, correlates parcel/order/tracking/barcode identifiers, and writes a chronological parcel view for the local UI.

The checked-in Fiege service configuration uses log4net size rolling: the main/database appenders are configured for 10 MB and the traffic appender for 1 MB. The reader therefore treats the configured sizes as hints only and relies on discovered file boundaries, timestamps, and incomplete-line recovery.

Planned output:

```text
output/
  events/YYYY-MM-DD.ndjson
  parcels/<parcel-id>.json
  index/identifiers.ndjson
  state/reader-state.json
```

Run:

```text
ParcelLogMiddleware.exe --logs C:\path\to\Fiege_Manual_Unloading --output .\output --port 4816
```
