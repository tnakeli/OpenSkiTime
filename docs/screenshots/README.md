# README screenshots

All eight screenshots were regenerated and visually reviewed on 2026-10-03 against the current desktop source. Every simulated impulse is durably assigned before the timing captures are rendered.

These images are headless Avalonia captures of a generated demo event. Athlete identities, codes, competition details and FIS-style points data are fictional; timing comes from the simulator. No personal event file or downloaded points list is checked in.

To regenerate the eight PNGs from the repository root, set `OPENSKITIME_README_SCREENSHOTS` to the absolute path of this directory and run:

```powershell
$env:OPENSKITIME_README_SCREENSHOTS = Join-Path (Get-Location) 'docs/screenshots'
dotnet test rewrite/tests/OpenSkiTime.Rewrite.Desktop.Tests/OpenSkiTime.Rewrite.Desktop.Tests.csproj --filter FullyQualifiedName~GenerateReadmeScreenshotsFromSyntheticEvent
```

The fixture creates its `.ost` file and points-list ZIP under the system temporary directory and removes them after capture.

The captures cover the event series, competition editor, competitor grid, first-run start list, timing workspace, overview, saved DSQ classification and automatically saved race-information form. The race-information capture is a draft before Run 2; it does not represent approved results or an accepted FIS submission. Synthetic competition codices 9991/9992 and the demo homologation and TD details are illustrative. The fixture does not call FIS or weather services.
