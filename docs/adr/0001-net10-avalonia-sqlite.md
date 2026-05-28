# ADR 0001 — .NET 10 LTS + Avalonia UI + SQLite

**Date**: 2026-05-28  
**Status**: Accepted  
**Context**: Choosing the runtime, UI framework and database for an offline-first Windows desktop race management tool.

## Decision

- **Runtime**: .NET 10 LTS (C# 13). Provides long-term support, modern language features (primary constructors, collection expressions), and source-generator MVVM tooling.
- **UI**: Avalonia UI 11.x. Cross-platform XAML framework that supports Windows natively without WPF's Windows-only constraint, allowing future Linux/macOS ports if required.
- **Database**: SQLite via `Microsoft.EntityFrameworkCore.Sqlite`. Zero-configuration embedded file database; appropriate for a single-user offline desktop tool. No network listener, no installation.

## Consequences

- The app runs fully offline with a single `.db` file in `%LOCALAPPDATA%\OpenSkiTime`.
- EF Core migrations handle schema evolution automatically on startup.
- No `HttpClient` or network dependency is registered in the composition root for this feature.
- Future FIS API / live timing integrations must be injected as optional, explicitly registered services — never assumed present.
