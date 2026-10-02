# OpenSkiTime

OpenSkiTime is a desktop race-office application for alpine skiing. It brings registration, start lists and live timing into one compact workspace. The app is built with .NET 10 and Avalonia, runs locally on Windows, and keeps each event series in a portable `.ost` file. Race operation can continue offline.

![Race control showing the start order, athletes on course, timestamps and ranking](docs/screenshots/overview.png)

*All screenshots show fictional athletes, generated points data and simulated timing. They do not show a real race or an official FIS list.*

## Race-office workflow

1. **Event series:** create or open a series file, set its dates and organizer, and make a backup or transfer copy. The Open file menu also lists recent series.
2. **Competitions:** add races with short names for the operator workspace and public names for outward-facing use. Set disciplines, runs and course details.
3. **Competitors:** edit a dense, keyboard-friendly grid, paste rows from Excel, assign participation per competition, review highlighted changes and save them together. Category rules can be saved and applied. An optional alpine FIS points-list download supports searching and updating athletes by Code; the list can then be used offline.
4. **Start lists:** choose a competition and run, draw the first-run bib order, then export TSV or open a printable view. Run 2 can be prepared from classified Run 1 results and shows those times alongside its starters.
5. **Timing:** work from four grids: **At start**, **Running**, **Timestamps** and **Ranking**. Reorder starters, assign raw impulses by dragging athletes onto timestamps, see live elapsed times and intermediate splits, and classify DNS/DNF/DSQ from the workspace. The first assigned start impulse begins the run. Original device input and an audit trail remain in the event file.

### 1 · Event series

One portable file holds the series and its race data. **Browse FIS calendar** lets you filter alpine events by season, nation and location, review an event's races, and fill both the series and its competitions from the selection. Save a public FIS API key in Settings to use this optional online feature; missing details remain editable.

![Event series details for a fictional alpine weekend](docs/screenshots/01-event-series.png)

### 2 · Competitions

Select a race to edit its identity, schedule and course details.

![Competition list and editor with two fictional slalom races](docs/screenshots/02-competitions.png)

### 3 · Competitors

The registration grid keeps participation, identity, category and FIS points visible together.

![Competitor grid populated with fictional athletes and generated points](docs/screenshots/03-competitors.png)

### 4 · Start lists

The competition/run selector opens the active start list directly.

![First-run start list drawn for a fictional women's slalom](docs/screenshots/04-start-lists.png)

### 5 · Timing

At start and Running show the immediate race state; Timestamps and Ranking retain the full picture.

![Race-control workspace during a simulated run](docs/screenshots/05-timing.png)

## Timing sources and current scope

OpenSkiTime has adapters for ALGE Timy 2/3 native USB, MT1 USB/serial and the ALGE Results service. A simulator and ASCII replay are available for practice. Device configuration and channel HOLD controls are described in [timing setup and operation](docs/timing.md).

The current draw profile covers standard FIS two-run SL/GS and single-run DH/SG. Local category draws, special Cup, youth, snow-seed and three-run formats are not yet supported. Official result publication, FIS result submission, penalties and automatic backup-time substitution are also pending. Physical-device race acceptance still needs a complete rehearsal with independent backup timing.

## Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows. From the repository root:

```powershell
dotnet restore rewrite/OpenSkiTime.Rewrite.slnx
dotnet run --project rewrite/src/OpenSkiTime.Rewrite.Desktop/OpenSkiTime.Rewrite.Desktop.csproj
```

Run the automated tests with `dotnet test rewrite/OpenSkiTime.Rewrite.slnx`.
