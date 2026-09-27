# OpenSkiTime

OpenSkiTime is a desktop application for managing alpine ski competitions. It is built with .NET 10 and Avalonia for a compact, keyboard-friendly race-office workflow. Windows is the current target; local event work does not require an internet connection.

## Current capabilities

- Create an event series in its own portable `.ost` file, add competitions, reopen the file, and make a backup or transfer copy.
- Maintain a shared competitor list and choose which competitions each person enters. Edit the grid with the keyboard or paste tabular data from Excel.
- Review highlighted changes, undo individual changes, discard all pending changes, or save them together. Copy selected competitors as tab-separated data.
- Define category rules by birth year and gender, save a reusable rule set, and apply category updates.
- Optionally download an alpine FIS points list, search it locally, and use a competitor's FIS Code to fill or update details and points. Configure the API key in Settings if the FIS download requires one. The key is stored in the Windows credential store, outside event files.
- Reopen one of the ten most recently used event files from the Open file menu.

Bib draws, start lists, live timing, device connections, and results are **not yet available** in the current application. Do not rely on it to time or score a race.

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
