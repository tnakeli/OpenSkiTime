# Development Setup

## Prerequisites

- .NET 10 SDK
- For local testing on Linux: libtesseract and libleptonica development libraries

## Building and Testing

### Windows

```bash
dotnet test OpenSkiTime.slnx -c Release
```

### Linux

Install system dependencies for Tesseract OCR:

```bash
sudo apt-get update
sudo apt-get install -y libtesseract-dev libleptonica-dev
```

Then run tests:

```bash
dotnet test OpenSkiTime.slnx -c Release
```

## Architecture

See [AGENTS.md](AGENTS.md) for engineering guidelines and development workflow.

## OCR and Platform-Specific Tests

### OCR (Tesseract) — Windows only

The project uses Tesseract 5.2.0 for offline receipt OCR. The Tesseract.Sharp NuGet package v5.2.0 only supports Windows. The timing feature tests and most domain/persistence tests run on Linux, but OCR receipt recognition tests are skipped.

When running tests locally on Linux, OCR tests are excluded:

```bash
dotnet test OpenSkiTime.slnx -c Release --filter "FullyQualifiedName !~ TimingReceiptOcrTests"
```

### Timy USB Support — Windows only

The native USB library for ALGE Timy timing devices is Windows-only. These tests are also skipped on Linux.

### Credential Storage — Platform-specific

Windows-specific credential storage (Windows Data Protection API) tests only run on Windows.

### Desktop Workflow Tests — Headless environment only

The headless Avalonia testing environment on Linux has limitations with complex UI layout and navigation tests. These tests are included in the Windows CI where the full Avalonia rendering pipeline is available.

## Testing

### Running all tests

```bash
dotnet test OpenSkiTime.slnx -c Release
```

### Running specific test class

```bash
dotnet test OpenSkiTime.slnx -c Release --filter "FullyQualifiedName~TimingAssignmentPreviewTests"
```

### Desktop UI tests (Avalonia headless)

```bash
dotnet test tests/OpenSkiTime.Desktop.Tests/OpenSkiTime.Desktop.Tests.csproj -c Release
```

Note: Some desktop tests require a display server. The CI uses headless Avalonia and may skip UI tests on limited environments.

## Full-race, real-window and Live Timing end-to-end tests

The synthetic 100-athlete two-run slalom (`tests/OpenSkiTime.Tests/FullRace/SyntheticRace.cs`, fixed seed, fictional data only) drives several opt-in suites. Results and evidence are described in `docs/ux-e2e/TEST_REPORT.md`.

| Suite | Command | Notes |
|---|---|---|
| Application boundary (always on) | `dotnet test tests/OpenSkiTime.Tests -c Release --filter "FullyQualifiedName~FullRace"` | Set `OPENSKITIME_FULL_RACE_REPORT=<absolute dir>` to keep the `.ost` files and the expected-vs-actual report. |
| View coverage screenshots | `OPENSKITIME_VIEW_COVERAGE=<absolute dir> dotnet test tests/OpenSkiTime.Desktop.Tests -c Release --filter "FullyQualifiedName~GenerateViewCoverageScreenshotsFromFullRace"` | Headless renderings of every main view at 1280×800 and 1920×1080. |
| Live Timing browser | `OPENSKITIME_LIVE_FULL_RACE=<dir> dotnet test tests/OpenSkiTime.Desktop.Tests -c Release --filter "FullyQualifiedName~ExportLiveSnapshotsOfTheFullRace"`, then `python tests/live-timing-full-race-e2e.py --snapshots <dir> --screenshots <dir>` | Needs a Release build of `OpenSkiTime.LiveTiming.Server`, Python Playwright and Chromium. Loopback only. |
| Real Windows desktop | `dotnet build src/OpenSkiTime.Desktop -c Release`, then `OPENSKITIME_DESKTOP_E2E=1 dotnet test tests/OpenSkiTime.Desktop.E2E -c Release --filter "FullyQualifiedName~OperatorRunsTheFullRace"` | FlaUI drives the built executable with mouse and keyboard on an interactive desktop (~45 min). Do not use the computer while it runs. Local data is redirected with `OPENSKITIME_LOCAL_DATA`, so the operator's own FIS cache and preferences are never used. Not part of `OpenSkiTime.slnx`. |
| Demonstration video | `OPENSKITIME_DESKTOP_E2E=1 OPENSKITIME_DEMO_VIDEO=<dir> OPENSKITIME_DEMO_LIVE_SNAPSHOTS=<dir> dotnet test tests/OpenSkiTime.Desktop.E2E -c Release --filter "FullyQualifiedName~RecordDemonstration"`, then `python scripts/demo-video/compose.py --recording <dir> --output <file.mp4>` | Records the real application with ffmpeg (gdigrab) and composes chapters and captions. Keep the screen awake and unlocked. |

On a machine with a short screen-saver timeout, the real-window suites keep the display awake for their own duration and end a running non-secure screen saver; a locked session or a password-protected screen saver is never bypassed, and the suites then fall back to UI Automation patterns without physical input.
