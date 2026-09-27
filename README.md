# OpenSkiTime

OpenSkiTime is a desktop application for managing alpine ski competitions. It is built with .NET 10 and Avalonia for a compact, keyboard-friendly race-office workflow. Windows is the current target; local event work does not require an internet connection.

## Current capabilities

- Create an event series in its own portable `.ost` file, add competitions, reopen the file, and make a backup or transfer copy.
- Maintain a shared competitor list and choose which competitions each person enters. Edit the grid with the keyboard or paste tabular data from Excel.
- Review highlighted changes, undo individual changes, discard all pending changes, or save them together. Copy selected competitors as tab-separated data.
- Define category rules by birth year and gender, save a reusable rule set, and apply category updates.
- Optionally download an alpine FIS points list, search it locally, and use a competitor's FIS Code to fill or update details and points. Configure the API key in Settings if the FIS download requires one. The key is stored in the Windows credential store, outside event files.
- Reopen one of the ten most recently used event files from the Open file menu.
- Prepare competition-specific bib draws from the **Draw / Start lists** dropdown: choose a competition, then its run. **Draw** saves the start list immediately, ready for TSV export or a printable HTML view. Choose the reversal when preparing Run 2. Mark the run started when racing begins to lock its order and make the next run available. Competition names, codes and course details remain editable after drawing.
- Open **Timing**, choose a competition/run, arm start and finish bibs, review unmatched impulses, and view run times and combined totals. Original input, assignments and reasoned corrections are retained in the event file; undo adds to the history.
- Timing adapters are available for ALGE Timy 2/3 native USB, MT1 USB/serial and the ALGE Results service. Simulator and ASCII replay use a separate training workflow. See [timing setup and operation](docs/timing.md).

The initial draw profile supports standard FIS two-run SL/GS and single-run DH/SG, with separate competitions for Men and Women. Run 2 uses classified Run 1 timing with the original bibs retained; external result entry remains available when no timing has been captured. Download the appropriate points list before drawing; subsequent local operation works offline. Local category draws, special Cup, youth, snow-seed and three-run formats are not supported.

Timing is an initial implementation for supervised testing. Physical-device race acceptance is still pending; rehearse with independent backup timing before race use. Official result publication, FIS result submission, penalties and automatic backup-time substitution are not yet implemented.

## Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows. From the repository root, run:

```powershell
dotnet restore rewrite/OpenSkiTime.Rewrite.slnx
dotnet run --project rewrite/src/OpenSkiTime.Rewrite.Desktop/OpenSkiTime.Rewrite.Desktop.csproj
```

Run the current application's tests with:

```powershell
dotnet test rewrite/OpenSkiTime.Rewrite.slnx
```

The current application and its tests live in `rewrite/`. The older `src/` and `tests/` projects remain in the repository as a reference to earlier behavior.
