# Changelog: OpenSkiTime desktop application

All notable changes to the Windows application are recorded here. Each section becomes the release notes of the matching `vX.Y.Z` GitHub release. The hosted live timing server is versioned separately; see [CHANGELOG-LIVE.md](CHANGELOG-LIVE.md).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/). While the version is `0.x`, the `.ost` series format is not yet stable between releases.

## [Unreleased]

This version uses live protocol 2.

### Added

- **Race radio (optional):** short Finnish AI commentary for Local and Cloud live timing viewers. Enable it per competition in the live timing control panel's *Race radio* card, choose the language model and speech provider, set a spending limit (a hard stop) and add event background and pronunciations if needed. Provider keys are saved in Settings → Live timing → *Race radio provider keys* (Windows Credential Manager). Numbers, times, places and names are spoken by OpenSkiTime's own Finnish rules, results are called unofficial, and timing capture and live publishing are never affected. *Simulation* providers rehearse it offline without cost. Every start is announced with the next racer in the start gate and the racer's club (and nationality in an international field); racers on course are featured during the run, and foreign names are respelled for the Finnish voice by nation. The bib is said with each racer's first mention, starts say "Radalla nyt", a DNF is announced at once and with feeling, and every event is said even when the model leaves it out. *Google Gemini 3.8 Flash TTS (Vertex AI)* and *ElevenLabs Eleven v4* are available as speech providers; Gemini needs a Google API key bound to a service account, saved as the *Google Gemini TTS (Vertex AI)* key, and ElevenLabs its own key. Optional background music: choose a folder of MP3 files (for example royalty-free music) in the *Race radio* card; viewers hear it softly under the commentary and can turn it off with *♪ Music*.

### Fixed

- **FIS calendar loads despite incomplete races:** one race without a codex (or another unusable row) no longer rejects the whole FIS calendar. Races without a codex are imported and the codex can be entered in Competitions later; a later calendar import fills it in for the same race. Only races that cannot form a competition are skipped, and the calendar status shows how many.

### Security

- **FIS live timing logs without racer data:** the local live timing diagnostic log no longer stores racer names, nations, clubs or FIS codes from FIS messages; it records only message structure, bibs and FIS errors. Full protocol tracing for troubleshooting is opt-in with the `OST_FIS_WIRE_TRACE=1` environment variable.

## [0.2.0-preview.2] - 2026-10-14

This version uses live protocol 1 and keeps the `.ost` series format of 0.2.0-preview.1; its series files open unchanged.

### Security

- **Signed releases:** the installer, its uninstaller and the OpenSkiTime programs are digitally signed and timestamped with the project's Certum code signing certificate issued to Teemu Niemi. Check the installer's *Digital Signatures* tab before running it. Windows SmartScreen may still warn about a new download until it has built a reputation.

## [0.2.0-preview.1] - 2026-10-14

This version uses live protocol 1 and changes the `.ost` series format (saved FIS points). Series files from earlier versions cannot be opened; create a new series.

### Added

- A competitor cannot be entered in a competition of the other gender. Ticking or pasting such an entry is refused with the reason, and saving checks it on every path. Existing wrong entries in older files do not block other changes and can always be removed.
- **Forerunners in Timing:** the **Forerunners** button in At start enters, edits or copies from Run 1 the run's forerunners during the race. They are the same forerunners as in Results, start before the competitors, are timed and audited like competitors but marked in their own colour. Timing shows their times (below the competitors in Ranking, without a rank) to check the timing; they are never ranked, never in the results and never published with a time in live timing.
- **Start lists:** before the first draw, every entered competitor is shown with the discipline points the draw will use, sorted by points; points that differ from the loaded FIS list are highlighted as errors. Bibs can start from any number and skip listed numbers (for example `13, 27, 40-42`), and can be reassigned as often as needed. A competitor can be moved to another start position with a reason, keeping the assigned bib. Every change is saved and audited as a new start-list revision.

### Changed

- **Browse FIS homologations** lists every homologated course in the competition's nation for the discipline, sorted by place and course, and preselects the course with the current homologation code or at the competition location (also for combined locations such as "Passo Stelvio | Bormio"). A moved race, a wrong location or a race without a FIS calendar entry can choose any course; nothing is applied until Apply homologation.
- **FIS draw with an older points list:** a points list that is not valid on the competition date no longer blocks the draw. Start lists shows which list the draw will use or used, with a clear NOT VALID warning, and the status after the draw repeats it.
- **FIS points are saved with the event series.** Update from FIS list stages the new list's points as highlighted changes and Save keeps them, together with the list's code and validity. The draw uses the saved points, so a series moved to another computer draws the same way. Points of competitors added from the FIS search are saved with them; use Update from FIS list and Save for imported or typed-in competitors.
- **Competitors:** the buttons are now **Undo** (was Review changes), **Save** (was Save changes) and **Update from FIS list** (was Stage updates for existing competitors).
- **Draw / Create start list** is always available. When a start list cannot be created, pressing it states the reason; for a FIS race with entries of the wrong gender the reason names those competitors.
- **Gender** is shown as Men / Women (Mixed for competitions) everywhere instead of the codes M / W / A.
- **Refreshed appearance:** a denser, higher-contrast look across the application and the Live timing control panel. Navy header, blue for actions and selection, a strong blue edge on selected rows, clearer keyboard focus, readable disabled controls, aligned figures in grids and compact rows and toolbars. The navy bar is now the window's title bar with the OpenSkiTime logo, the active competition and run, and **File** and **Settings** menus. New series, Open file, recent files, Backup / transfer and Close file are in **File**. The separate competition selector is removed; choose the competition and run in the Start lists, Timing, Results, Timing report and PDF Factory menus. Navigation items no longer show numbers.
- **Timing view:** device, B clock and live-timing status in one light strip under the navigation, with the clocks on the left (A with its START, intermediate and FINISH switches, then B) and live-timing channels and the device time on the right; the clock groups wrap instead of being cut off in a narrow window; pane headings show counts (At start, On course); DNS, DNF, DSQ, NPS, Clear status and Prepare Run 2 are below the On course list; running times are larger; the correction bar is above the classification editor; the starters/finished summary is in the status bar. Shortcuts, drag and drop and all timing functions are unchanged.

### Fixed

- **Browse FIS homologations** works again for competitions with a gender.
- **Release checksums:** `SHA256SUMS.txt` uses LF line endings throughout, so `sha256sum -c` verifies it directly. The release notes give a PowerShell command for checking downloads on Windows.

## [0.1.0-preview.2] - 2026-10-05

Second preview for rehearsals and evaluation. This version uses live protocol 1 and keeps `.ost` compatibility: series files from 0.1.0-preview.1 open unchanged. Do not open a series that contains manual timestamps with 0.1.0-preview.1, which does not understand them.

### Added

- **Manual timestamps:** if an impulse never arrives, double-click a START, INTERM or FINISH cell in the Timestamps view (or press F2) to enter the time of day by hand. The new row is marked as manually entered, the entry is recorded with operator and reason, and an assigned manual time is shown with a small "m". A result that uses a manual time asks for verification against backup timing.
- **Ranking:** the time column is named after the current run (Run 1, Run 2), each intermediate has its own column with the rank at that intermediate, and from Run 2 on the previous run's time is shown with its rank in parentheses. Equal times share a rank.
- **Simulator:** the simulation time follows the PC clock, so Start, Finish and intermediates can be pressed directly. Pause the clock to type a time of your own.
- **Simulator as B Clock:** for training, the simulator can be chosen as the B Clock source when primary timing is the simulator or a replay file. Test B start and Test B finish create backup impulses that are kept apart from A timing.

### Changed

- **Save Course & Homologation** now shares the course only with races of the same discipline, and the checkbox names the discipline (for example "all Giant Slalom races"). Saving the TD to all races is unchanged.
- **Cloud live timing** waits up to two minutes for a hosted server that is waking from idle and shows "Waking the live timing server" meanwhile, instead of failing. Stop takes effect immediately.

### Fixed

- Live timing no longer stops updating with "Live timing state could not update" when the number of intermediates is changed while live timing is running.
- Toolbar buttons (Open file, the competition picker) keep readable light text when hovered, pressed or open.

## [0.1.0-preview.1] - 2026-10-04

First public preview of the Windows application, for rehearsals and evaluation. Do not rely on it alone for an official race without independent backup timing.

### Added

- **Event series:** portable `.ost` series files with backups, transfer copies and recent files; optional import of an event and its competitions from the public FIS calendar.
- **Competitions:** race identity, schedule, FIS category, course, homologation and technical delegate editing, with shared course and TD saves across the series.
- **Competitors:** keyboard-friendly registration grid, Excel paste with highlighted review, category rules and an optional offline FIS points list.
- **Start lists:** FIS first-run draw for two-run SL/GS and single-run DH/SG with saved seeds and replay verification; Run 2 preparation from classified Run 1 results; TSV export and printable view.
- **Timing:** ALGE Timy 2/3 native USB, MT1 USB/serial and ALGE Results service adapters, plus simulator and ASCII replay. At start, Running, Timestamps and Ranking views, channel HOLD, impulse assignment and audited DNS/DNF/DSQ/NPS classification. Original device input and correction history are kept in the series file.
- **Results:** race information, jury and per-run course details, FIS penalty calculation, TD-approved immutable XML revisions and optional FIS submission in test mode.
- **Timing report:** automatic A timing, optional B and hand-clock evidence, local receipt OCR with operator review, and timing report XML (FIS submission in test mode only).
- **PDF Factory:** entry lists, start lists, referee forms, penalty calculations, approved results and timing reports, with optional A4 backgrounds and margins.
- **Live timing:** FIS TCP/HTTPS publishing, an offline local browser view and Cloud publishing to a server such as `live.openskiti.me`, controlled from a separate live timing panel. Cloud publishing requires a publisher key issued by the server operator. This version uses live protocol 1.
- **Settings → About:** software version and implemented FIS rule season (2026-27).
- Windows x64 per-user installer and portable ZIP, both including the .NET runtime.

### Known limitations

- The installer and executables are not code-signed; Windows may show an unknown-publisher warning.
- Series files from earlier development builds are rejected, and a later preview may not open files from this one: automatic schema upgrades are not available yet. Incompatible files are left unchanged. Keep backups.
- Local category draws, special Cup, youth, snow-seed and three-run formats are not supported. Automatic backup-time substitution and production FIS submission are pending.
- ALGE native USB needs the vendor driver and SDK, installed separately.

[Unreleased]: https://github.com/tnakeli/OpenSkiTime/compare/v0.2.0-preview.2...HEAD
[0.2.0-preview.2]: https://github.com/tnakeli/OpenSkiTime/compare/v0.2.0-preview.1...v0.2.0-preview.2
[0.2.0-preview.1]: https://github.com/tnakeli/OpenSkiTime/compare/v0.1.0-preview.2...v0.2.0-preview.1
[0.1.0-preview.2]: https://github.com/tnakeli/OpenSkiTime/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/tnakeli/OpenSkiTime/releases/tag/v0.1.0-preview.1
