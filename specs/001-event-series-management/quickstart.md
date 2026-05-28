# Quickstart — Event Series Management

**Feature**: 001-event-series-management

For developers picking up this feature.

## Prerequisites

- Windows 10/11 x64 (primary target).
- .NET 10 LTS SDK installed (`dotnet --version` ≥ `10.0`).
- Git.
- (Optional) Visual Studio 2026 / Rider 2026.x / VS Code with C# Dev Kit.

## Clone & build

```powershell
git clone <repo-url> OpenSkiTime
cd OpenSkiTime
git checkout 001-event-series-management
dotnet restore
dotnet build
```

## Run the desktop app

```powershell
dotnet run --project src/OpenSkiTime.Desktop
```

On first run the app creates the local SQLite file at
`%LOCALAPPDATA%\OpenSkiTime\openskitime.db` and runs EF Core migrations
automatically.

## Run all tests

```powershell
dotnet test
```

Run a single project:

```powershell
dotnet test tests/OpenSkiTime.Import.Tests
```

Run with name filter (xUnit):

```powershell
dotnet test tests/OpenSkiTime.Import.Tests --filter FullyQualifiedName~ParticipationValueMatcher
```

## Reset the local database

```powershell
Remove-Item "$env:LOCALAPPDATA\OpenSkiTime\openskitime.db" -Force
```

The next launch recreates and re-migrates the file.

## Apply a new EF Core migration

```powershell
dotnet ef migrations add <MigrationName> `
  --project src/OpenSkiTime.Persistence `
  --startup-project src/OpenSkiTime.Desktop
```

Migrations are idempotent and applied at startup by the Desktop
composition root.

## Key entry points

| Concern | Project | File |
|---|---|---|
| App startup / DI | `OpenSkiTime.Desktop` | `Program.cs`, `App.axaml.cs` |
| Domain model | `OpenSkiTime.Domain` | `EventSeries/`, `Competitions/`, `Competitors/` |
| Use cases | `OpenSkiTime.Application` | `EventSeries/`, `Import/` |
| EF Core context | `OpenSkiTime.Persistence` | `OpenSkiTimeDbContext.cs` |
| Importer | `OpenSkiTime.Import` | `Headers/`, `Names/`, `Participation/`, `Preview/` |
| FIS placeholder | `OpenSkiTime.Fis.Placeholder` | `NotImplementedFisUpdater.cs` |

## Sanity check after pulling

A successful state of this feature must satisfy all of:

- `dotnet build` clean (warnings as errors, per `Directory.Build.props`).
- `dotnet test` green across all test projects.
- App launches, lets you create an Event Series, add a Competition,
  paste a small TSV, see a preview, and apply it — all with the network
  cable unplugged.
- Clicking "Update from FIS API" produces a clear "not yet available"
  notification and zero network activity.
