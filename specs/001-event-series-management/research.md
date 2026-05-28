# Phase 0 Research — Event Series Management

**Feature**: 001-event-series-management
**Date**: 2026-05-28

The spec was authored with informed defaults; no `[NEEDS CLARIFICATION]`
markers reached this phase. This document records the technical decisions
that shape the plan.

---

## Decision: .NET 10 LTS as the runtime

- **Rationale**: Mandated by the constitution (Technology & Platform
  Constraints). LTS gives a 3-year support window matching ski-season
  release cadence.
- **Alternatives considered**:
  - .NET 8 LTS — older; no advantage; rejected.
  - .NET Framework 4.8 — Windows-only at the language level, no path to
    Linux portability; violates constitution; rejected.

## Decision: Avalonia UI 11.x as the desktop UI framework

- **Rationale**: Constitution default. Native cross-platform XAML; same
  code path on Windows now and Linux later. Active community, MVVM-friendly,
  styles/themes via Fluent.
- **Alternatives considered**:
  - WPF — Windows-only; would force a rewrite for Linux portability;
    rejected.
  - WinUI 3 — Windows-only and MSIX-centric; rejected.
  - MAUI — primary focus is mobile; desktop story weaker than Avalonia for
    dense data-grid use cases; rejected.
  - Uno Platform — viable, but Avalonia has simpler desktop story and
    matches constitution's stated default.

## Decision: SQLite via EF Core 10 (code-first migrations)

- **Rationale**:
  - SQLite mandated by constitution.
  - EF Core gives us migrations (race-office DBs evolve over seasons),
    LINQ for grid filters/queries without hand-rolled SQL, and
    `ChangeTracker` for the Import Apply transaction.
  - .NET 10 ships with mature `Microsoft.EntityFrameworkCore.Sqlite`.
- **Alternatives considered**:
  - **Dapper + raw SQL + manual migrations**: faster at scale but our
    scale (≤ a few thousand competitors per series) makes the win
    irrelevant; we lose migrations and change tracking. Rejected.
  - **`Microsoft.Data.Sqlite` only (no ORM)**: same downside as Dapper,
    more boilerplate. Rejected.
  - **LiteDB / EF Core in-memory / custom file format**: violates
    constitution default and complicates ad-hoc inspection during a race.
    Rejected.

## Decision: MVVM via CommunityToolkit.Mvvm

- **Rationale**: Source-generator-based `[ObservableProperty]` and
  `[RelayCommand]` eliminate INPC/`ICommand` boilerplate. Maintained by
  Microsoft, MIT-licensed, no transitive Windows dependency.
- **Alternatives considered**:
  - Hand-rolled INPC: more code, more bugs.
  - ReactiveUI: powerful, steeper curve and conceptual overhead for a
    one-window race-office app. Rejected for this feature; can be added
    later for a specific screen if needed.

## Decision: xUnit + FluentAssertions for tests

- **Rationale**: De facto standard in modern .NET, parallel test runner,
  trait-based filtering. FluentAssertions improves diagnostic output for
  the importer's preview-bucket assertions.
- **Alternatives considered**:
  - NUnit / MSTest — viable but no decisive advantage; team familiarity
    favors xUnit.

## Decision: Local timezone semantics

- **Rationale**: Constitution and FR-003 explicitly forbid a timezone
  field. Event Series and competition dates are stored as **calendar
  dates only** (no time-of-day) — `DateOnly` in .NET, `TEXT` ISO-8601
  date in SQLite. This prevents the "DST shifted my race day" failure
  mode and matches how race offices think about race days.
- **Alternatives considered**:
  - `DateTime` with `DateTimeKind.Local` — leaks time-of-day into a
    domain that doesn't have one; rejected.
  - `DateTimeOffset` — implies a timezone, contradicts the constitution.

## Decision: Composition root only in `OpenSkiTime.Desktop`

- **Rationale**: Keeps `Domain`/`Application`/`Persistence`/`Import`
  reusable from a future CLI, server, or test harness without dragging
  in Avalonia.
- **Alternatives considered**:
  - Wire DI inside each module's static class — leaks ownership of the
    composition; rejected.

## Decision: Single Event Series open in UI at a time

- **Rationale**: The spec describes one race weekend at a time. Allowing
  multiple open series doubles UX complexity (which series does the grid
  show? which is the "current" one for paste?) for no in-spec benefit.
- **Alternatives considered**:
  - Tabs / MDI — defer until a real workflow demands it.

## Decision: Competitor match strategy in importer

- **Primary key**: competitor `Code` within the Event Series.
- **Fallback**: tuple `(UpperLastName, FirstName, YearOfBirth, Gender)`,
  surfaced in the preview as "matched without code" so the user can
  override.
- **Rationale**: race offices receive lists where some competitors lack
  a code (newcomers) and some are pre-coded by the federation. We must
  not silently create duplicates when the user re-imports a near-identical
  list.
- **Alternatives considered**:
  - Code-only matching — produces duplicates on re-import; rejected.
  - Fuzzy name matching — risk of incorrect merges; rejected for v1.

## Decision: Best-effort `Name` parser strategy

- **Strategy**: split on whitespace; ALL-CAPS leading tokens are taken
  as last-name tokens (handles "VAN DER POEL Jeroen"); otherwise last
  whitespace token is the last name and the rest is first name.
- **Rationale**: Aligns with how FIS publishes names. Always flagged
  uncertain regardless of confidence (FR-055), so deterministic-but-
  imperfect is acceptable.
- **Alternatives considered**:
  - First-token-is-last-name heuristic — wrong for many Anglo lists.
  - ML-based parser — overkill, opaque, hard to test.

## Decision: Test SQLite DB per test (file, not in-memory)

- **Rationale**: SQLite's `:memory:` database has subtle differences
  from on-disk (per-connection isolation issues with EF Core); using a
  temp file makes the persistence tests representative of production.
- **Alternatives considered**:
  - `:memory:` — flaky around shared connections; rejected.
  - SQL Server LocalDB — wrong engine; rejected.

## Decision: No `HttpClient` registration in DI in this feature

- **Rationale**: The FIS placeholder MUST perform zero outbound calls
  (FR-011, SC-007). The simplest enforcement is to not register an
  `HttpClient` / `IHttpClientFactory` in the composition root at all.
  When FIS is implemented in a later feature, registration is added then.
- **Alternatives considered**:
  - Register and rely on code review — easy to violate; rejected.
