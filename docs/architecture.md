# Architecture

## Module Map

```
src/
├── OpenSkiTime.Domain           — Entities, value objects, domain rules (no I/O)
├── OpenSkiTime.Application      — Use cases, service abstractions (depends on Domain)
├── OpenSkiTime.Persistence      — EF Core + SQLite, repository implementations (depends on Application + Domain)
├── OpenSkiTime.Import           — TSV tokenizer, header mapper, diff engine, apply service (depends on Application)
├── OpenSkiTime.Fis              — FIS placeholder updater, no HttpClient (depends on Domain)
└── OpenSkiTime.Desktop          — Avalonia MVVM UI, composition root (depends on all)

tests/
├── OpenSkiTime.Domain.Tests
├── OpenSkiTime.Application.Tests
├── OpenSkiTime.Persistence.Tests
├── OpenSkiTime.Import.Tests
└── OpenSkiTime.Desktop.Tests
```

## Dependency Rules (enforced by csproj references)

```
Desktop  →  Application  →  Domain
Desktop  →  Import       →  Application
Desktop  →  Fis          →  Domain
Desktop  →  Persistence  →  Application
```

No project may reference `Desktop` or `Persistence` except `Desktop` itself.
`Import` must not reference `Persistence` directly.

## Import Workflow

```
User pastes TSV text
        │
        ▼
  TsvTokenizer.Tokenize()
        │  string[][]
        ▼
  HeaderMapper.MapHeaders()
        │  IReadOnlyList<ColumnMapping>
        ▼
  RowParser.Parse()
        │  IReadOnlyList<RawImportRow>
        ▼
  DiffEngine.Compute(snapshot, rows)
        │  ImportDiff  (NewCompetitors, BibAssignments, Participations, Warnings)
        ▼
  [UI shows ImportPreview — user reviews]
        │
        ▼
  ImportApplyService.ApplyAsync()
        │  transactional SaveChanges via IUnitOfWork
        ▼
  Competitor grid reloads
```

## Key Design Decisions

See `docs/adr/` for ADRs:
- [0001](adr/0001-net10-avalonia-sqlite.md) — .NET 10 + Avalonia UI + SQLite
- [0002](adr/0002-efcore-over-dapper.md) — EF Core over Dapper
- [0003](adr/0003-mvvm-with-community-toolkit.md) — MVVM with CommunityToolkit.Mvvm

## Offline-first constraint

The desktop application must function entirely offline (SC-005). No `HttpClient` or `IHttpClientFactory` is registered in `Program.cs`. The FIS update feature is a future placeholder that shows a "not yet available" message and performs zero network I/O (SC-007).
