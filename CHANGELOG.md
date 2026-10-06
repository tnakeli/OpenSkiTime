# Changelog: OpenSkiTime desktop application

All notable changes to the Windows application are recorded here. Each section becomes the release notes of the matching `vX.Y.Z` GitHub release. The hosted live timing server is versioned separately; see [CHANGELOG-LIVE.md](CHANGELOG-LIVE.md).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/). While the version is `0.x`, the `.ost` series format is not yet stable between releases.

## [Unreleased]

### Fixed

- **Release checksums:** `SHA256SUMS.txt` uses LF line endings throughout, so `sha256sum -c` verifies it directly. The release notes give a PowerShell command for checking downloads on Windows.

## [0.1.0-preview.2] - 2026-10-05

Second preview for rehearsals and evaluation. This version uses live protocol 1 and keeps `.ost` compatibility: series files from 0.1.0-preview.1 open unchanged. Do not open a series that contains manual timestamps with 0.1.0-preview.1, which does not understand them.

### Added

- **Manual timestamps:** if an impulse never arrives, double-click a START, INTERM or FINISH cell in the Timestamps view (or press F2) to enter the time of day by hand. The new row is marked as manually entered, the entry is recorded with operator and reason, and an assigned manual time is shown with a small "m". A result that uses a manual time asks for verification against backup timing.
- **Ranking:** the time column is named after the current run (Run 1, Run 2), each intermediate has its own column with the rank at that intermediate, and from Run 2 on the previous run's time is shown with its rank in parentheses. Equal times share a rank.
- **Simulator:** the simulation time follows the PC clock, so Start, Finish and intermediates can be pressed directly. Pause the clock to type a time of your own.
- **Simulator as B Clock:** for training, the simulator can be chosen as the B Clock source when primary timing is the simulator or a replay file. Test B start and Test B finish create backup impulses that are kept apart from A timing.

### Changed

- **Save Course & Homologation** now shares the course only with races of the same discipline, and the checkbox names the discipline (for example "all Giant Slalom races"). Saving the TD to all races is unchanged.
- **Cloud live timing** waits up to two minutes for a hosted server that is waking from idle and shows "Waking the live timing server" meanwhile, instead of failing. Stop takes effect immediately.

### Fixed

- Live timing no longer stops updating with "Live timing state could not update" when the number of intermediates is changed while live timing is running.
- Toolbar buttons (Open file, the competition picker) keep readable light text when hovered, pressed or open.

## [0.1.0-preview.1] - 2026-10-04

First public preview of the Windows application, for rehearsals and evaluation. Do not rely on it alone for an official race without independent backup timing.

### Added

- **Event series:** portable `.ost` series files with backups, transfer copies and recent files; optional import of an event and its competitions from the public FIS calendar.
- **Competitions:** race identity, schedule, FIS category, course, homologation and technical delegate editing, with shared course and TD saves across the series.
- **Competitors:** keyboard-friendly registration grid, Excel paste with highlighted review, category rules and an optional offline FIS points list.
- **Start lists:** FIS first-run draw for two-run SL/GS and single-run DH/SG with saved seeds and replay verification; Run 2 preparation from classified Run 1 results; TSV export and printable view.
- **Timing:** ALGE Timy 2/3 native USB, MT1 USB/serial and ALGE Results service adapters, plus simulator and ASCII replay. At start, Running, Timestamps and Ranking views, channel HOLD, impulse assignment and audited DNS/DNF/DSQ/NPS classification. Original device input and correction history are kept in the series file.
- **Results:** race information, jury and per-run course details, FIS penalty calculation, TD-approved immutable XML revisions and optional FIS submission in test mode.
- **Timing report:** automatic A timing, optional B and hand-clock evidence, local receipt OCR with operator review, and timing report XML (FIS submission in test mode only).
- **PDF Factory:** entry lists, start lists, referee forms, penalty calculations, approved results and timing reports, with optional A4 backgrounds and margins.
- **Live timing:** FIS TCP/HTTPS publishing, an offline local browser view and Cloud publishing to a server such as `live.openskiti.me`, controlled from a separate live timing panel. Cloud publishing requires a publisher key issued by the server operator. This version uses live protocol 1.
- **Settings → About:** software version and implemented FIS rule season (2026-27).
- Windows x64 per-user installer and portable ZIP, both including the .NET runtime.

### Known limitations

- The installer and executables are not code-signed; Windows may show an unknown-publisher warning.
- Series files from earlier development builds are rejected, and a later preview may not open files from this one: automatic schema upgrades are not available yet. Incompatible files are left unchanged. Keep backups.
- Local category draws, special Cup, youth, snow-seed and three-run formats are not supported. Automatic backup-time substitution and production FIS submission are pending.
- ALGE native USB needs the vendor driver and SDK, installed separately.

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/v0.1.0-preview.2...HEAD
[0.1.0-preview.2]: https://github.com/tnakeli/OpenSkiTime/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/tnakeli/OpenSkiTime/releases/tag/v0.1.0-preview.1
