# Settings

Settings has five tabs: **FIS**, **Timing devices**, **Timing report**, **Live timing** and **About**.

## FIS

Save one FIS API key in the first tab. The same Windows Credential Manager entry is used for FIS data, equipment homologations, result XML and timing-report XML. Public JSON requests use `X-Api-Key`; the competition-file upload/status service uses bearer authentication. Competition-file access requires `competition.files.write` and `competition.files.read`. The former second token field is no longer read or displayed. Existing Windows credentials are not copied into event files or logs.

Both XML send paths remain **test only**. They upload the selected approved revision's exact bytes and filename with `testMode=true`. The server must confirm test mode. No production switch or automatic POST retry is provided. Timing report and Results share the submission client and polling limit. See [Results race information](results-race-information.md#xml-test-submission).

**Refresh homologations** downloads the typed catalogue from the [FIS public API](https://api.fis-ski.com/public-docs?public-api-docs.json), reviewed 2026-10-03: `GET /homologation/timing-devices?includeExpired=true`. Retaining expired entries supports historical reports; the catalogue exposes current validity and `validUntilSeason`, not a complete history of approval intervals. The operator checks the selected equipment against the race season. Lookup failures preserve the previous cache and manual equipment values. Downloaded catalogues stay in local application data; chosen equipment details are copied into each event report.

## Timing devices

Timing is configured per **timing role**: each role chooses a source, its connection and a channel. Device date and operator apply to the capture session, not to a role.

- **PRIMARY TIMING** lists **Start**, **Finish** and **Intermediate 1…N**. Primary sources are Timy 2/3 · USB, MT1 · USB / serial, MT1 · ALGE Results, Simulator and Replay file. **Add intermediate** appends the next intermediate on the next free channel; **Remove last intermediate** removes the highest one. A race uses as many configured intermediates as its competition defines; extra configured intermediates are ignored for that race.
- **B CLOCK · OPTIONAL** lists **B Clock Start** and **B Clock Finish** after **Add B Clock**. B sources are the same without the simulator. **Clear B Clock** removes both roles. The section also holds the B Clock warning thresholds (start warning ms, finish warning ms, missing signal wait s).

Each row shows only the connection fields of its source: USB ID, firmware and **Set up USB library** for Timy USB; COM port, **Refresh ports** and baud rate for MT1 serial; the ALGE Results username and MT1 device for ALGE Results; and the file path for Replay file. Rows other than Start and B Clock Start default to **Same as Start** / **Same as B Clock Start**, so a shared device is entered once and each further role adds only its channel and, for ALGE Results, its device.

**Any device can serve any role.** Roles on one device use different channels; different devices, source types and ALGE Results accounts may be combined, for example Timy USB at start and finish with intermediates on MT1 devices of two clubs, each with its own ALGE Results account. All device clocks are set to the same time, so elapsed times may span devices. OpenSkiTime opens one capture session per physical connection; the sessions of one capture share a clock group and each keeps its own raw journal. Several Timy USB devices need explicit USB IDs, a serial port serves one connection, and an ALGE Results device channel serves one role. Training sources (Simulator, Replay file) cannot be mixed with real devices. B must be independent of primary timing: Timy USB clocks need separate explicit USB IDs, serial sources separate ports, and A and B cannot share an ALGE Results device ID.

**ALGE RESULTS ACCOUNTS** lists each username used by a role once, with its password, **Remember password** and **Fetch devices**. Fetching signs in once and lists the account's MT1 devices (`GET /mt1/api/user/{userId}/devices`); the role rows of that account then offer the devices in a list instead of typing IDs. A configured ID that the account no longer lists stays visible and is marked. Passwords are kept in memory while timing is connected (so B Clock retries work) and stored only with **Remember password**, per account in Windows Credential Manager.

ALGE Results times are the device clock time set for the device in ALGE Results (`timestamp + timeOffset`, as documented by the API), the same wall-clock time every other device shows. OpenSkiTime has no time zone or UTC offset settings.

**Signal monitor:** while timing is connected, each role row shows on the right the latest impulse per channel of its device, for example `C0 20:46:44.07 · ●C1 20:47:01.33`; ● marks the row's own channel. Press the device buttons or cells to check wiring and channel settings. Channels that no role uses are shown too: for ALGE Results they are journalled as information and never timed, for Timy/MT1 they remain unassigned review input as before.
**Signal monitor:** while timing is connected, each role row shows on the right the latest impulse per channel of its device, for example `C0 20:46:44.07 · ●C1 20:47:01.33`; ● marks the row's own channel. Press the device buttons or cells to check wiring and channel settings. Channels no role uses are shown too: for ALGE Results they are journalled as information and never timed; for Timy/MT1 they remain unassigned review input as before.

**Device date** dates Timy and MT1 serial times, which carry only the time of day. ALGE Results reports the full date, and a live trigger from another day than the device date is shown for review instead of being timed. Connecting warns when the device date is not today's date.

**Save timing settings** validates the complete configuration and stores it on this computer in `timing-roles.json` in the user's local application data. Passwords are never stored there. Settings cannot be changed while timing is connected; disconnect first. While connected, the Settings tab shows the live B Clock status with **Retry B Clock** and **Disconnect B Clock**; neither interrupts primary timing.

Start/finish difference warnings and missing-signal wait are helper thresholds, initially 1 ms, 10 ms and 5 s. ALGE Results monitoring waits at least 10 s for missing B signals. These settings do not alter timing or FIS rules.

**Migration of former settings:** when no role file exists, the former primary settings (`timing-settings.json`) and the former optional B / hand-clock connection (`auxiliary-settings.json`) are read once. The primary source becomes Start, Finish and the listed intermediate channels; the former backup connection is proposed as B Clock together with its thresholds. A notice in Settings lists what needs checking (for example ALGE Results usernames and device IDs, or whether the former backup device really is the permanent B Clock) until the settings are saved. The former files are left unchanged on disk. Temporary B and hand device imports are no longer part of Settings; device evidence for a timing report is read from the timing report itself.

## Timing report

The **Timing report** tab contains report equipment, people and connection defaults together. The **Timing devices** tab contains the timing role assignments and capture settings.

Equipment defaults contain A/B timers, the start gate, start clock, A/B finish cells and optional separate start timers. Select a row, choose a cached matching homologation, apply it and enter the individual serial number. Equipment can also be entered manually offline. **Save report defaults** at the end of this tab saves equipment, people and report connections together for timing reports.

Selecting a cached homologation snapshots its expiry season and catalogue retrieval time with the equipment. These values survive event transfer and are checked against the report season even offline. Changing only the serial number preserves the evidence; changing the brand, model or homologation code clears it and shows **Manual / unverified** until a catalogue entry is applied again. Manual equipment still needs operator verification. The start clock is entered manually because the public catalogue has no start-clock category; the supplied XML description requires its brand, model, serial and homologation when a start clock is declared.

Save the chief of timing and calculations, timekeeper and connection defaults once. Surnames and nation codes normalize to uppercase. These are user preferences outside the series database. Reports use these saved values when opened. Saving preferences updates an open report; saved report revisions retain portable snapshots and approved XML remains unchanged. OpenSkiTime supplies the result-software name and version automatically.

Verification uses synthetic equipment and contacts. Tests cover endpoint authentication, malformed/error responses, shared credentials through actual Results and report submission helpers, Settings navigation, offline cache/default reopening, preserved report snapshots and rejected invalid replacements. Physical devices, live authenticated FIS responses and real FIS report acceptance need separate operational verification.

## Live timing

Enter the Cloud server address and save its **publisher key**. A Cloud live timing server creates race sessions only for publishers holding a key issued by its operator; Local publishing needs no key. The key is saved per server origin in this Windows user's Credential Manager, never in the event file or preferences, and the status line shows whether a key is saved for the current address. Restart Cloud publishing after saving a new key. To publish on `live.openskiti.me`, contact the OpenSkiTime maintainer for a key; self-hosted servers issue their own. See [publisher keys](live-timing-cloud-deployment.md#publisher-keys).

## About

Shows the software version (Semantic Versioning `MAJOR.MINOR.PATCH`) and the FIS rule season the application implements (currently 2026-27), with links to the source code, MIT license, third-party notices, issue reporting, private vulnerability reporting and the privacy statement. The window title shows the same version and rule season.
