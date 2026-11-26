# Tutorial videos

Short, narrated use-case videos for YouTube, in Finnish and English. Each shows the real application being used, with the
pointer clicking when the word is spoken. The whole production is repeatable: after a user interface or wording change the
same commands make the video again.

The other recordings (`scripts/demo-video`, [social media](social-media.md)) have captions and no narration.

## How it works

| Step | Tool | Result |
|---|---|---|
| 1. Script | `scripts/tutorial-video/tutorials/<name>.json` | One text per step and language, with cue words |
| 2. Narration | `narrate.py` (ElevenLabs Eleven v4, with timestamps) | One MP3 per step and `timing.json`: the moment of every cue word |
| 3. Recording | `tests/OpenSkiTime.Desktop.E2E/<Name>TutorialTests.cs` with `TutorialRecorder` | `raw.mkv` and `recording.json` (pointer track, clicks, camera, areas to cover) |
| 4. Composition | `compose.py` (ffmpeg, numpy) | MP4 (1920 × 1080, 30 fps) and SRT |
| 5. Checks | `check.py` (Windows OCR) | Privacy, sync and sound; must pass |
| 6. Review | Gemini 4 Neon in T3 Code, then the maintainer | Findings to fix in the script or driver |
| 7. Publishing | `scripts/social/youtube.py`, YouTube Studio | Private first, public after approval |

**Speech first.** The narration exists before the recording. The driver reads `timing.json`, moves the real pointer and
clicks at the start of the cue word. When the application is slower than the speech, the composer shortens still pictures
and fits the time between two clicks, so every click is shown at its word and no speech is cut.

**No text on the picture** except the title card and the closing card with the links. Subtitles are in the SRT file.

**The pointer** is left out of the screen recording and drawn larger from its recorded track, with a halo and click rings.

**Music** is one piece for the whole series: full in the title and closing cards, about 20 dB under the speech.

## Privacy

A tutorial may use a real event. Then names, FIS codes and technical delegates are covered, never blurred: each cell is
filled with its own background colour and a bar of fixed shape, so not even the length of a name is left.

- `TutorialRecorder.Cover` sets the cells to cover; `TutorialRecorder.Reveal` wraps every action that shows or moves names.
  The composer keeps the picture before the action until the new cells are known, so no frame shows an uncovered name.
- `check.py` reads the finished video with OCR and compares it with the names in the recorded series file. As a control the
  same names must be found in the raw recording. A finding fails the check.
- Nothing but a composed and checked video leaves the computer. Raw recordings, the series file and failure pictures stay
  in the work folder and the demonstration folder; **delete them after publishing**.
- API keys are read from Windows Credential Manager by the application and never shown: Settings shows dots only.
- Narration sends the script text to ElevenLabs; the review sends the covered video to Google.

## Requirements

- Windows with a 1920 × 1080 screen at 100 %, .NET 10 SDK, Python 3 with numpy, ffmpeg.
- ElevenLabs Creator plan or higher. The key is saved in OpenSkiTime (Settings → Live timing → Race radio provider keys →
  ElevenLabs) and needs *Text to Speech*. The script's `voice` is a voice ID (any voice of the ElevenLabs library) or the
  name of a voice of the account, which also needs *Voices: read*.
- Keys the tutorial itself uses, for `fis-event` the FIS API key and the regs.fissi.fi key (Settings → API keys).
- A music file outside the repository. Eleven Music output is licensed for online use on the Creator plan, except film,
  TV, radio and studio games ([terms](https://elevenlabs.io/music-terms), read 2026-11-25). Keep its prompt, date and plan
  beside it, do not put it in Git, and do not use it in the race radio.

## Making a video

Tooltips appear after the pointer rests on a control, and one right after another opens at once. Near the bottom of the
screen a tooltip can open under the pointer and take the next click; let the pointer rest on an empty place before clicking
there (see `RecordTraining`).

The work folder (`C:\OpenSkiTimeVideos`) and the demonstration folder (`C:\OpenSkiTimeDemo`) are outside the repository and
the user profile, so no account name is on screen.

```powershell
$work = 'C:\OpenSkiTimeVideos'
# 1. Narration; unchanged steps are reused. --dry-run shows what would be spoken and how many characters it takes.
python scripts/tutorial-video/narrate.py fis-event --language fi --work $work

# 2. Recording: takes the mouse, keyboard and screen for about five minutes. Close other windows, turn on Do not disturb.
#    The test method is the script's "driver": RecordBasics, RecordErrors or RecordTraining (TimingTutorialTests), and
#    RecordStartLists, RecordSettings, RecordTimingReport or RecordRaceReports (OfficeTutorialTests).
dotnet build src/OpenSkiTime.Desktop -c Release
dotnet build tests/OpenSkiTime.Desktop.E2E -c Release
$env:OPENSKITIME_DESKTOP_E2E = '1'
$env:OPENSKITIME_TUTORIAL = "$work\fis-event\fi"
$env:OPENSKITIME_TUTORIAL_REHEARSAL = '1'   # go on after a failed step and list them in rehearsal.log; leave out for strict runs
tests/OpenSkiTime.Desktop.E2E/bin/Release/net10.0-windows/OpenSkiTime.Desktop.E2E.exe -method "*RecordFisEvent*"

# 3. Composition and checks (about four minutes)
$recording = (Get-ChildItem "$work\fis-event\fi" -Directory -Filter 'recording-*' | Sort-Object Name | Select-Object -Last 1).FullName
python -X utf8 scripts/tutorial-video/compose.py fis-event --language fi --recording $recording --music $work\assets\C-rytmikas.mp3 --output $work\fis-event\fi\output
python -X utf8 scripts/tutorial-video/check.py --video $work\fis-event\fi\output\OpenSkiTime-fis-event-fi.mp4 --recording $recording --every 0.5
```

Repeat with `en`. A take is used only when `rehearsal.log` has no failed step and `check.py` passes. `check.py` takes the
click times from the recorder's log, so watch the video once for what only an eye sees.

### Review

Put a small copy (720p) of the checked video in the ignored `artifacts/` folder, let Gemini 4 Neon review it through T3 Code
(Antigravity), and delete the copy. Neon is good at comparing narration and picture, missing steps, pacing and
pronunciation. It is **not** reliable for names, numbers and small text (tested 2026-11-25), so it never replaces
`check.py`. Findings are suggestions; the maintainer listens to the Finnish and decides.

### Publishing

`publish.py` uploads a composed video as private with the script's `youtube` entry (title, description with what the viewer
learns, tags), the language, the altered or synthetic content label (AI voice and music) and the SRT as subtitles. It saves
the video ID in `<video>.youtube.json` next to the video, so running it again only adds what is missing. The sign-in of
`scripts/social/youtube.py` is used (docs/social-media.md). Descriptions use no emojis or dashes as punctuation. Make a
video public only after the maintainer has watched it.

```powershell
python -X utf8 scripts/tutorial-video/publish.py fis-event --language fi --output $work\fis-event\fi\output        # preview
python -X utf8 scripts/tutorial-video/publish.py fis-event --language fi --output $work\fis-event\fi\output --yes  # upload
```

## Writing a script

- One step is one or two sentences and one action. Aim for three minutes or less in total.
- `{cue}` before a word marks the moment of an action: `Press {save}Save.` The driver uses `video.Click(button, "save")`.
  A cue at the very first word cannot anchor the picture; start the sentence with another word.
- `[shown|spoken]` gives a pronunciation that differs from the subtitle: `[API|aa pee ii]`. Use it for abbreviations and
  addresses in Finnish. `say` at the top of the script gives the pronunciation of a word everywhere (`"FISin": "Fissin"`).
- Say the control's English name as it is on screen, also in Finnish.
- Both languages need the same steps and cues, because one driver records both.
- `"fictional": true` marks a script whose event is invented (the synthetic race); `check.py` then has no names to look for.

## Adding a tutorial

1. Write `tutorials/<name>.json` and narrate it (check with `--dry-run` first).
2. Add `<Name>TutorialTests.cs`, following `FisEventTutorialTests`: one `video.Step` per step id. Prepare anything the viewer
   should not see before `video.Start()`.
3. The recorder offers `Click`, `ClickAt`, `Hover`, `Move`, `Drag` (press at one cue, drop at another), `DragTo`,
   `DoubleClickAt`, `Keys` (a shortcut at a cue), `Fire` (something without the pointer, such as a simulated impulse) and
   `Camera`/`Overview`. Look controls up before waiting for a cue. `Drag` and `DragTo` set the camera themselves to frame
   both the source and the target: a zoom on the target alone hides what is being dragged. Reading the whole control tree takes seconds when a large grid is filled;
   keep elements, query small scopes, and find the status text once.
4. Wrap every action that shows or moves people's names in `Reveal` and return the cells to cover. A view that the
   application opens by itself counts too. Use fictional data when the tutorial does not need a real event.
5. Rehearse, read `rehearsal.log`, compose, check and watch. Zoom (`Camera`) where text is small; return with `Overview`.

## Tutorials

| Name | Content |
|---|---|
| `fis-event` | Event from the FIS calendar (FIS races in Levi, 28 November – 1 December 2026), FIS API key, course from the FIS homologations for all slaloms, FIS points list, entries from regs.fissi.fi, paste from Excel, yellow cells, Undo and Save, update from the FIS list |
| `timing-basics` | Opening a run, HOLD and the inputs, the panes, a forerunner, automatic assignment of start, intermediate and finish, start order by dragging, DNS by dragging onto its button, DNF with its button and the right-click menu, result and intermediate rank |
| `timing-errors` | A false finish fixed by dragging the racer onto the right timestamp, a missing finish entered by hand, a racer passing another (the same drag), HOLD of the finish, a rerun |
| `timing-training` | The simulator as source, test impulses on the PC clock, a typed time for a tie, practising an error with a drag, and setting training times aside when a real device is chosen |
| `start-lists` | The FIS standard draw, draw settings (first group, first bib), the draw, moving a racer with a reason, Print view and Export TSV |
| `settings` | API keys, timing devices (operator, source, channel, intermediates, B Clock), timing report defaults and live timing keys |
| `timing-report` | The FIS timing report of a timed race: equipment and officials, runs, replacement times, synchronization, system B and hand times, certification, XML, sending to FIS and test mode |
| `race-reports` | Results, penalty, race information (jury, gates, start time), FIS XML, and PDF Factory with its background and Generate All |

The tutorials are for every operator, so they show the mouse: drag and drop and buttons. Keyboard shortcuts are for
experienced operators: a tutorial may mention them (the right-click menu lists them), but never demonstrates them.

The timing and race office tutorials use the synthetic race (`SyntheticRace`, ten racers). `TimingTutorial.Record` prepares
it to one of three stages before the recording: `Entries` (racers entered, nothing drawn), `Timing` (the first run drawn and
open in Timing with the simulator, on today's date) or `Timed` (both runs timed with the simulator, on the race date, when
its FIS points list is valid and the penalty can be calculated). In Timing, impulses come in real time on the computer's
clock, so the running times on screen are real. `OfficeTutorialProbe` (opt-in with `OPENSKITIME_TUTORIAL_PROBE=<folder>`)
saves a screenshot and the UI Automation tree of each race office view of a timed race, to write a new driver against. A run with training times can only be timed again after
**Set training times aside**, which `timing-training` shows.

`fis-event` names its event in constants at the top of `FisEventTutorialTests`. The entries and points lists change, so after
the event choose another one there (event name, start date, list date and number of races) and in the script.
