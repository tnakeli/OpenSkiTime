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
