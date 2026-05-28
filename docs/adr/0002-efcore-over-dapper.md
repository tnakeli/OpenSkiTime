# ADR 0002 — EF Core over Dapper (or raw SQL)

**Date**: 2026-05-28  
**Status**: Accepted  
**Context**: Choosing the data-access layer for the persistence project.

## Decision

Use **EF Core 10** with code-first migrations and a `DbContext`-based repository pattern, not Dapper or raw ADO.NET.

## Rationale

| Criterion | EF Core | Dapper |
|---|---|---|
| Schema migrations | Built-in (`dotnet ef migrations`) | Manual SQL scripts |
| Change tracking | Automatic | Manual |
| Query complexity | LINQ — adequate for this domain | Raw SQL required |
| Test isolation | `InMemory` + SQLite in-process | SQLite in-process |
| Overhead | Marginal for < 10 000 rows | Marginal |

For a race management tool (hundreds to low thousands of rows), EF Core's migration automation and change tracking eliminate an entire category of boilerplate without measurable runtime cost.

## Consequences

- All schema changes go through EF migrations in `OpenSkiTime.Persistence`.
- `DbContext` is scoped per operation via `IServiceScope` in the composition root.
- `IUnitOfWork.SaveChangesAsync()` is the single commit point — no ambient transactions elsewhere.
- If raw performance ever becomes a bottleneck (unlikely), individual queries can be replaced with `FromSqlRaw` without changing the architecture.
