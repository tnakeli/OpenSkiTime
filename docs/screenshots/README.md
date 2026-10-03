# README screenshots

The desktop screenshots are generated against the current source. Every simulated impulse is durably assigned before the timing captures are rendered. The receipt screenshot uses the existing synthetic OCR fixture and the actual recognition and matching workflow.

These images are headless Avalonia captures of a generated demo event. Athlete identities, codes, competition details and FIS-style points data are fictional; timing comes from the simulator. No personal event file or downloaded points list is checked in.

To regenerate the ten desktop PNGs from the repository root, set `OPENSKITIME_README_SCREENSHOTS` to the absolute path of this directory and run:

```powershell
$env:OPENSKITIME_README_SCREENSHOTS = Join-Path (Get-Location) 'docs/screenshots'
dotnet test tests/OpenSkiTime.Desktop.Tests/OpenSkiTime.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~ScreenshotsFromSyntheticEvent
```

The fixture creates its `.ost` file and points-list ZIP under the system temporary directory and removes them after capture.

The captures cover the event series, competition editor, competitor grid, first-run start list, timing workspace, overview, saved DSQ classification, automatically saved race-information form, timing-report Times tab and receipt review dialog. The race-information capture is a draft before Run 2; it does not represent approved results or an accepted FIS submission. Synthetic competition codices 9991/9992/9993 and the demo homologation and TD details are illustrative. All preferences and cached lists used by these fixtures live in their temporary folders; they do not read or write the operator's settings. The fixture does not call FIS or weather services.

`10-live-timing.png` shows the actual live server's browser interface, including a connected viewer, intermediate times, two racers on course, ranked finishers and waiting starters. `live-demo.json` contains its fictional snapshot; no publisher credential is stored. To regenerate it in an isolated development browser:

```sh
dotnet build src/OpenSkiTime.LiveTiming.Server -c Release
python -m pip install playwright==1.58.0
python -m playwright install chromium
python scripts/Capture-PublicScreenshots.py --website
```

The Visual previews GitHub workflow runs this browser capture in a Linux CI environment. Its `public-screenshots` artifact includes the live image and desktop/mobile previews of the homepage and guide. The browser is restricted to the loopback demo servers; it cannot reach a real race service. Preview processes are stopped after capture. The public website links gallery images at full resolution and labels all examples as synthetic.
