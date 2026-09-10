# ParcelJourney mock scenarios

These fixtures are synthetic and contain no production identifiers. They use
the Fiege/MFR log vocabulary observed in the existing parser and source tree.

| ID | Scenario | Expected outcome |
|---|---|---|
| 9000000001 | normal AR parcel | closed successfully |
| 9000000002 | normal alternate target | closed successfully on target B2 |
| 9000000003 | no-read | NIO / no-read route |
| 9000000004 | weight error | NIO route |
| 9000000005 | missing database row | exception / missing dataset |
| 9000000006 | wrong divert | misroute |
| 9000000007 | PLC acknowledgement missing | pending after route command |
| 9000000008 | label exit without tracking | incomplete tracking |
| 9000000009 | duplicate technical messages | deduplicated journey |
| 9000000010 | late external reporting | closed, reporting after close |

Example:

```powershell
dotnet run --project ..\ParcelJourney.Standalone -- . 9000000001 *.log
```
