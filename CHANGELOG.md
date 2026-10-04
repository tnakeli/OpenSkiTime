# Changelog: OpenSkiTime desktop application

All notable changes to the Windows application are recorded here. Each section becomes the release notes of the matching `vX.Y.Z` GitHub release. The hosted live timing server is versioned separately; see [CHANGELOG-LIVE.md](CHANGELOG-LIVE.md).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/). While the version is `0.x`, the `.ost` series format is not yet stable between releases.

## [Unreleased]

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

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/tnakeli/OpenSkiTime/releases/tag/v0.1.0-preview.1
