# M1 evidence — open a weekend

The new application is isolated in `rewrite/OpenSkiTime.Rewrite.slnx`. It does not reference the legacy projects or open/migrate the legacy database. Its `.ost` file contains one event series and its competitions; file selection, edits, close/reopen and backup are reachable from the desktop window. Each write uses a short-lived EF Core context, a transaction, a revision check and SQLite constraints. New files and older new-format files use EF migrations. An upgrade makes a SQLite backup before changing schema; unknown newer migrations and non-new-format files are rejected.

Run with the .NET 10 SDK:

```powershell
dotnet run --project rewrite/src/OpenSkiTime.Rewrite.Desktop/OpenSkiTime.Rewrite.Desktop.csproj
```

Verification on Windows, 2026-09-26:

- New solution Release build: 0 warnings/errors. Four tests passed: create/edit/reopen/backup/series isolation; v1-to-v2 migration with preserved data and pre-upgrade backup; rejected edits/open leave data unchanged; headless Avalonia buttons create, edit, back up and reopen a series.
- The desktop process started and remained running until stopped. The headless test exercised the actual window and commands with synthetic file-picker responses.
- Legacy solution source remains unchanged. Legacy Release build completed. Its Domain 94, Application 37, Import 93 and Persistence 12 tests passed; the Release desktop test DLL was blocked by this machine's Windows Application Control policy (`0x800711C7`). The existing Debug desktop test DLL ran and passed 5 tests.

Still to verify on an operator desktop: visual density and keyboard use at relevant Windows scaling/theme settings, and opening a copied `.ost` file on a physically different computer. The integration test moves a SQLite backup to another folder and reopens it without source-path dependencies; it is not a second-machine trial. M1 contains no competitor, timing or legacy import workflow; those start at later milestones.
