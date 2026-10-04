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

## OCR Dependencies

The project uses Tesseract 5.2.0 for offline receipt OCR. On Windows, the NuGet package includes native binaries. On Linux, you must install the Tesseract and Leptonica development libraries via your system package manager (apt, yum, dnf, etc.) before running tests or the application.

### Troubleshooting OCR on Linux

If you see `DllNotFoundException` for `libleptonica-1.82.0.so`, install the required libraries:

```bash
# Ubuntu/Debian
sudo apt-get install -y libtesseract-dev libleptonica-dev

# Fedora/RHEL
sudo dnf install -y tesseract-devel leptonica-devel
```

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
