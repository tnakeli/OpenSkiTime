# Changelog: OpenSkiTime live timing server

All notable changes to the hosted live timing server (`live.openskiti.me`) are recorded here. Each section becomes the release notes of the matching `live-vX.Y.Z` GitHub release. The desktop application is versioned separately; see [CHANGELOG.md](CHANGELOG.md).

Server versions are independent of application versions. Compatibility between them is defined by the **live protocol** version (`LiveProtocol.Version`). A server lists the protocol it supports; an application release states the protocol it uses. Increment the protocol only for an incompatible publishing API change.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.3...HEAD
[0.1.3]: https://github.com/tnakeli/OpenSkiTime/releases/tag/live-v0.1.3
[0.1.2]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.1...live-v0.1.2
[0.1.1]: https://github.com/tnakeli/OpenSkiTime/compare/live-v0.1.0...live-v0.1.1
[0.1.0]: https://github.com/tnakeli/OpenSkiTime/tree/live-v0.1.0
