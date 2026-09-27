# M1 evidence — open a weekend

The new application is isolated in `rewrite/OpenSkiTime.Rewrite.slnx`. It does not reference the legacy projects or open/migrate the legacy database. Its `.ost` file contains one event series and its competitions; file selection, edits, close/reopen and backup are reachable from the desktop window. Each write uses a short-lived EF Core context, a transaction, a revision check and SQLite constraints. New files and older new-format files use EF migrations. An upgrade makes a SQLite backup before changing schema; unknown newer migrations and non-new-format files are rejected.

The Open file control keeps the picker as its primary action and lists up to ten recently opened existing `.ost` files in its drop-down. This convenience list lives in the current user's local application data, outside portable event files. A successful create or open moves the file to the top; a missing file is omitted on the next load. A desktop workflow test opens a file from the list, and a store test covers ordering, deduplication and the ten-file limit.

Run with the .NET 10 SDK:

```powershell
dotnet run --project rewrite/src/OpenSkiTime.Rewrite.Desktop/OpenSkiTime.Rewrite.Desktop.csproj
```

Verification on Windows, 2026-09-26:

- New solution Release build: 0 warnings/errors. Four tests passed: create/edit/reopen/backup/series isolation; v1-to-v2 migration with preserved data and pre-upgrade backup; rejected edits/open leave data unchanged; headless Avalonia buttons create, edit, back up and reopen a series.
- The desktop process started during the implementation check. The headless test exercised the actual window and commands with synthetic file-picker responses. This did not prove that a fresh local build would launch reliably under Windows Smart App Control.
- Subsequent UI review replaced the spinner date picker with numeric `DD.MM.YYYY` entry, aligned labels/controls and separated the form rows. A rendered headless frame was inspected; the UI test checks field spacing at 980 and 1280 pixels, normalized dates and rejection of an invalid day without changing the saved series. Native theme/DPI and operator review remain open.
- Legacy solution source remains unchanged. Legacy Release build completed. Its Domain 94, Application 37, Import 93 and Persistence 12 tests passed; the Release desktop test DLL was blocked by this machine's Windows Application Control policy (`0x800711C7`). The existing Debug desktop test DLL ran and passed 5 tests.

Still to verify on an operator desktop: visual density and keyboard use at relevant Windows scaling/theme settings, and opening a copied `.ost` file on a physically different computer. The integration test moves a SQLite backup to another folder and reopens it without source-path dependencies; it is not a second-machine trial. M1 contains no competitor, timing or legacy import workflow; those start at later milestones.

## Local Windows launch blocker

On 2026-09-26, launching the Debug desktop build produced a Windows Security notification that part of the app was blocked. Code Integrity events 3033/3077 identify `OpenSkiTime.Rewrite.Desktop.dll` and `OpenSkiTime.Rewrite.Persistence.dll`, policy `{0283ac0f-fff1-49ae-ada1-8a933130cad6}`, because the locally built binaries do not meet signing/reputation requirements. `Get-AuthenticodeSignature` reports `NotSigned`. This is a Smart App Control decision outside the application; builds and headless tests can pass while interactive launch is blocked. Do not treat this machine as a completed native UI acceptance test. The supported distribution remedy is to sign all shipped executable binaries with a certificate trusted by Windows; local development also requires an environment whose application-control policy permits locally built code. Do not alter the user's Windows protection settings as part of the build.
