# Open Ski Time

Open-source alpine skiing race timing and race management software for Windows, built with .NET 10 and Avalonia UI.

> **Status**: Active development — Feature 001 (Event Series Management) shipped.

---

## What is Open Ski Time?

Open Ski Time is a desktop application for race offices, timing teams, Technical Delegates, referees, and organizers. It replaces legacy timing software (e.g. SkiAlp Pro) with a modern, compact, SaaS-style UI that works fully **offline** under live event pressure.

Core design goals:
- **Race-office practicality** — every feature must survive a real race scenario
- **Offline-first** — no cloud dependency for local race-day operation
- **Modern UX** — clean and information-dense; no 1990s carryover
- **Modular** — domain, timing, import, FIS, and live-timing are separate concerns

---

## Features

### ✅ Feature 001 — Event Series Management

| Area | What you can do |
|---|---|
| **Event Series** | Create, edit, delete race weekends / race weeks |
| **Competitions** | Add, edit, remove individual competitions within a series |
| **Competitor import** | Paste TSV data from Excel or FIS results; preview diff before applying |
| **Competitor pool** | Per-series shared competitor pool with per-competition participation |
| **Bib assignment** | Assign and reassign bib numbers; conflict detection |

### 🔜 Planned

- Competitor list / manual edit UI
- Category rules (age groups, gender filters)
- Start list generation
- Timing capture (Alge Timy3, Alge MT1)
- FIS XML submission
- Live timing (cloud-hosted + local LAN)

---

## Screenshots

> *Screenshots will be added after the first public release. The sections below show what each view contains.*

### Event Series Overview

```
┌─────────────────────────┬──────────────────────────────────────────────┐
│  Event Series           │  Ylläs FIS Sprint Weekend                    │
│  ─────────────────────  │  ────────────────────────────────────────── │
│  Ylläs FIS Sprint       │  Name / Location / Organizer / Dates         │
│  Weekend                │  Nation / Season                             │
│                         │  [ Save Event Series ]  [ Delete ]           │
│  [ + New Event Series ] │                                              │
│                         │  Competitions                                │
│                         │  Date    Label  Name        Discipline       │
│                         │  Jan 10  3.1 SL Slalom #1   SL               │
│                         │  Jan 11  3.2 SL Slalom #2   SL               │
│                         │  [ + New Competition ]                       │
└─────────────────────────┴──────────────────────────────────────────────┘
```

### Import Screen

```
┌──────────────────────────────────────────────────────────────────────┐
│  Import Competitors                                                   │
│  Paste a tab-separated list (from Excel / FIS results)               │
│                                                                       │
│  Event Series: [ Ylläs FIS Sprint Weekend        ▾ ]                 │
│                                                                       │
│  ┌─────────────────────────────────────────────────────────────┐     │
│  │ LastName   FirstName  YOB   Nat  Bib  3.1 SL  3.2 SL        │     │
│  │ SMITH      John       2005  FIN  1    x        x             │     │
│  │ MÜLLER     Hannes     2007  GER  2    x                      │     │
│  └─────────────────────────────────────────────────────────────┘     │
│                                                                       │
│  [ Preview ]  [ Apply ]  [ ← Back ]                                  │
│                                                                       │
│  New competitors to add (2)                                           │
│  Last Name  First Name  YOB   Nation  Bib  Participating In           │
│  SMITH      John        2005  FIN     1    3.1 SL, 3.2 SL            │
│  MÜLLER     Hannes      2007  GER     2    3.1 SL                     │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows (Linux support planned)

### Build & run

```powershell
git clone https://github.com/tnakeli/OpenSkiTime.git
cd OpenSkiTime
dotnet run --project src/OpenSkiTime.Desktop/OpenSkiTime.Desktop.csproj
```

### Run tests

```powershell
dotnet test
```

All 168 tests should pass (Domain 85, Application 24, Import 51, Persistence 8).

---

## Project structure

```
OpenSkiTime/
├── src/
│   ├── OpenSkiTime.Domain/          # Aggregate roots, entities, value objects
│   ├── OpenSkiTime.Application/     # Use cases, repository abstractions
│   ├── OpenSkiTime.Persistence/     # EF Core + SQLite, migrations
│   ├── OpenSkiTime.Import/          # TSV tokenizer, diff engine, preview/apply
│   ├── OpenSkiTime.Fis.Placeholder/ # FIS integration stub (not yet wired)
│   └── OpenSkiTime.Desktop/         # Avalonia UI, shell, view models
├── tests/
│   ├── OpenSkiTime.Domain.Tests/
│   ├── OpenSkiTime.Application.Tests/
│   ├── OpenSkiTime.Persistence.Tests/
│   ├── OpenSkiTime.Import.Tests/
│   └── OpenSkiTime.Desktop.Tests/
├── specs/                           # Feature specs, plans, tasks (Spec Kit)
└── .specify/                        # Project constitution and templates
```

---

## Architecture

- **Domain-driven design** — `EventSeries` is the aggregate root owning `Competition`, `Competitor`, `Participation`, and `CategoryRule`
- **Clean architecture** — UI depends on Application; Application depends on Domain; Persistence is a plug-in
- **Concurrency control** — optimistic locking via `RowVersion` on `EventSeries`
- **Import pipeline** — `TsvTokenizer` → `HeaderMapper` → `NameProjector` → `DiffEngine` → `ImportPreviewService` / `ImportApplyService`

See [`.specify/memory/constitution.md`](.specify/memory/constitution.md) for the full governing principles.

---

## License

[MIT](LICENSE)
