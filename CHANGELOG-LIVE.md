# Changelog: OpenSkiTime live timing server

All notable changes to the hosted live timing server (`live.openskiti.me`) are recorded here. Each section becomes the release notes of the matching `live-vX.Y.Z` GitHub release. The desktop application is versioned separately; see [CHANGELOG.md](CHANGELOG.md).

Server versions are independent of application versions. Compatibility between them is defined by the **live protocol** version (`LiveProtocol.Version`). A server lists the protocol it supports; an application release states the protocol it uses. Increment the protocol only for an incompatible publishing API change.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Race page: the banner follows the course from the top: the racer after next, the next racer, then the racers on course from the one who started last down to the one nearest the finish. Both groups were shown in start order.

## [0.6.0] - 2026-10-24

Live protocol: 2

### Changed

- Race page: the banner follows the course with one row per racer and a pictogram: the next two racers in the start gate, everyone on course with the running time, and the latest finisher with time and rank. The competition name has place, discipline and date under it.
- A race whose racers all have the same nation shows no nation information.
- The banner is as wide as the results, and a highlighted result row has one frame around the whole row.

### Fixed

- Country flags show on Windows desktop browsers too, drawn with a bundled flag font (Twemoji Country Flags, CC-BY 4.0) where the system has no flag emoji.
- A racer on course always has a ticking running time, in the banner and in the results, also when the timing device's start time or date differs from real time; the time then runs from when the viewer saw the start.

## [0.5.0] - 2026-10-24

Live protocol: 2

### Added

- Race page: the country flag beside each nation code (where the device draws flag emoji) with the country name as tooltip, the next two racers in the start gate, and race statistics under the results: racers, nations and clubs, and per run the starters, finished, DNS, DNF and DSQ. Racers by nation and by club open on click.
- Phones get two-line cells (name over club, Run 1 over Run 2, total over difference) instead of a wide table that scrolls sideways.
- Operator load report `GET /api/sessions/{id}/stats` (session token): viewers now and over the last hour, viewers on the whole server, response times, CPU, memory and queued work. The live timing control panel of the next OpenSkiTime release shows it for Local and Cloud. Compatible addition; live protocol stays 2.

### Changed

- Race page: no status column; DNS, DNF, DSQ and NPS are shown in the run's time column and a racer on course has the running time there. The latest finisher stays highlighted for ten seconds instead of six.

### Fixed

- Race radio: the viewer no longer waits for a line of a conversation that the server already withdrew.

## [0.4.1] - 2026-10-23

Live protocol: 2

### Fixed

- Race radio: a line that becomes outdated while it plays (a start or finish moves places and counts on) is no longer cut off mid-word; it is spoken to the end and the rest of its conversation is skipped.

## [0.4.0] - 2026-10-23

Live protocol: 2

### Added

- Optional race radio: viewers can press ▶ Radio on the race page to hear short Finnish AI commentary when the organizer has enabled it. The control appears only while the radio is healthy and never starts playing by itself. Clips are kept only in memory, expire within minutes, are dropped when the results contradict them and are deleted with the session. Optional background music chosen by the organizer plays softly under the commentary and can be turned off with ♪ Music; it is kept in memory (`LiveTiming__RadioMusicMaxMegabytes`, default 256) and deleted with the session.

### Changed

- Live protocol 2: adds the radio status, clip and music endpoints, the `Radio` announcement and the competition's optional run count. Publishers using protocol 1 are refused with HTTP 426; update OpenSkiTime to publish to this server.

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

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.4.1...HEAD
[0.4.1]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.4.0...live-v0.4.1
[0.4.0]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.3.0...live-v0.4.0
[0.3.0]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.2.0...live-v0.3.0
[0.2.0]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.3...live-v0.2.0
[0.1.3]: https://github.com/tnakeli/OpenSkiTime/releases/tag/live-v0.1.3
[0.1.2]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.1...live-v0.1.2
[0.1.1]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.0...live-v0.1.1
[0.1.0]: https://github.com/tnakeli/OpenSkiTime/tree/live-v0.1.0
