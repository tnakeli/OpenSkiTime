# Changelog: OpenSkiTime live timing server

All notable changes to the hosted live timing server (`live.openskiti.me`) are recorded here. Each section becomes the release notes of the matching `live-vX.Y.Z` GitHub release. The desktop application is versioned separately; see [CHANGELOG.md](CHANGELOG.md).

Server versions are independent of application versions. Compatibility between them is defined by the **live protocol** version (`LiveProtocol.Version`). A server lists the protocol it supports; an application release states the protocol it uses. Increment the protocol only for an incompatible publishing API change.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.0] - 2026-10-14

Live protocol: 1

### Added

- Forerunners: the race page shows each forerunner's progress (at start, on course, finished, DNS/DNF) and includes forerunners on course in the banner. Their times are never published.

### Changed

- The top of the race page is one compact bar (two rows on phones): competition, run and split selectors, update time and connection. The page no longer repeats the product name; it is in the browser tab title.
- On course is a list: every racer on course of the current run, one per line, with a running time that ticks in real time. The banner also shows the last finisher of the current run with time and rank, or DNF/DSQ when that was the latest outcome, highlighted for a few seconds when it changes.
- Refreshed live results page: light theme by default with white surfaces and strong blue accents, a compact header and run selectors so the results start higher on phones, larger result text and aligned times. Viewers whose device uses a dark theme keep a dark page.
- When every starter of the current run has finished or been classified, the race banner shows "Run N complete" instead of an empty "On course · —".

## [0.2.0] - 2026-10-05

Live protocol: 1

### Added

- Viewers can switch between intermediate times (from the start) and sector times (between consecutive timing points, the last sector ending at the finish), each in its own column. The choice is remembered in the browser.
- From Run 2 on, the race view shows each earlier run's time next to the current run and always shows the total.

### Changed

- Viewers follow the race to the new current run when it changes; a run picked by the viewer stays selected only until the race moves on.

## [0.1.3] - 2026-10-04

Live protocol: 1

### Added

- Responses and `/health` report the supported live protocol version. Publishers that declare a different protocol are refused with HTTP 426 before any change, and the application reports that the server is incompatible.

## [0.1.2] - 2026-10-04

Live protocol: 1 (implicit)

### Fixed

- Active publishers restore the full live state after a server restart, new revision or scale to zero, and connected viewers reconnect.

## [0.1.1] - 2026-10-04

Live protocol: 1 (implicit)

### Fixed

- Deployment waits for the new revision before verification.

## [0.1.0] - 2026-10-04

Live protocol: 1 (implicit)

### Added

- First hosted deployment: public race list, responsive race view, publisher keys for session creation and `security.txt`.

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.3.0...HEAD
[0.3.0]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.2.0...live-v0.3.0
[0.2.0]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.3...live-v0.2.0
[0.1.3]: https://github.com/tnakeli/OpenSkiTime/releases/tag/live-v0.1.3
[0.1.2]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.1...live-v0.1.2
[0.1.1]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.0...live-v0.1.1
[0.1.0]: https://github.com/tnakeli/OpenSkiTime/tree/live-v0.1.0
