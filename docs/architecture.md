# Architecture

OpenSkiTime targets .NET 10 with a Windows-first Avalonia desktop application. Local race operation works offline. Source projects are under `src/`, automated tests under `tests/`, and both solutions are at the repository root.

## Project boundaries

- `OpenSkiTime.Domain` defines race models, validation and draw rules.
- `OpenSkiTime.Timing` provides deterministic timing, classification and result calculations without UI, persistence or device I/O.
- `OpenSkiTime.Application` coordinates use cases through typed contracts.
- `OpenSkiTime.Persistence` stores each series in a portable SQLite database, including raw input and audit history.
- `OpenSkiTime.Devices` implements device transports and protocol decoding. `OpenSkiTime.TimyUsbHost` isolates the Windows .NET Framework vendor USB interface.
- `OpenSkiTime.Recognition` implements local receipt OCR.
- `OpenSkiTime.Reporting` renders local PDFs with QuestPDF from application report snapshots; see [PDF Factory](pdf-factory.md).
- `OpenSkiTime.Desktop` contains operator views and keeps user preferences separate from race data.
- `OpenSkiTime.LiveTiming.*` provides optional publishing, a worker, a control panel and the browser server. Publishing consumes committed race state and must not block capture.

`OpenSkiTime.slnx` builds the complete application and tests. `OpenSkiTime.LiveTiming.slnx` provides the live timing subset. `LiveTimingArtifacts.targets` copies the companion processes into desktop build and publish output.

See [database policy](development-database-policy.md), [FIS timing review](fis-timing-review.md), [timing operation](timing.md) and [software supply chain](software-supply-chain.md) for the storage, numeric, operational and dependency requirements.
