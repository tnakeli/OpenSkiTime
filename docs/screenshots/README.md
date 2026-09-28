# README screenshots

These images are headless Avalonia captures of a generated demo event. Athlete identities, codes, competition details and FIS-style points data are fictional; timing comes from the simulator. No personal event file or downloaded points list is checked in.

To regenerate the six PNGs from the repository root, set `OPENSKITIME_README_SCREENSHOTS` to the absolute path of this directory and run:

```powershell
dotnet test rewrite/OpenSkiTime.Rewrite.slnx --filter FullyQualifiedName~GenerateReadmeScreenshotsFromSyntheticEvent
```

The fixture creates its `.ost` file and points-list ZIP under the system temporary directory and removes them after capture.
