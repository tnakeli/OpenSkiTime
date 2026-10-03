# Settings

Settings has three tabs: **FIS**, **Timing devices** and **Timing report**.

## FIS

Save one FIS API key in the first tab. The same Windows Credential Manager entry is used for FIS data, equipment homologations, result XML and timing-report XML. Public JSON requests use `X-Api-Key`; the competition-file upload/status service uses bearer authentication. Competition-file access requires `competition.files.write` and `competition.files.read`. The former second token field is no longer read or displayed. Existing Windows credentials are not copied into event files or logs.

Both XML send paths remain **test only**. They upload the selected approved revision's exact bytes and filename with `testMode=true`. The server must confirm test mode. No production switch or automatic POST retry is provided. Timing report and Results share the submission client and polling limit. See [Results race information](results-race-information.md#xml-test-submission).

**Refresh homologations** downloads the typed catalogue from the [FIS public API](https://api.fis-ski.com/public-docs?public-api-docs.json), reviewed 2026-10-03: `GET /homologation/timing-devices?includeExpired=true`. Retaining expired entries supports historical reports; the catalogue exposes current validity and `validUntilSeason`, not a complete history of approval intervals. The operator checks the selected equipment against the race season. Lookup failures preserve the previous cache and manual equipment values. Downloaded catalogues stay in local application data; chosen equipment details are copied into each event report.

## Timing devices

The primary A source retains its existing controls. The optional auxiliary connection accepts Timy USB, MT1 serial, ALGE Results or replayed raw ALGE ASCII. Select **B**, **HandStart** or **HandFinish**. Live B follows the active A run. For a temporarily attached device, clear **Live B**, refresh saved runs and select the destination run before connecting. Hand roles use the **Start / hand channel** and, for ALGE Results, the start-device ID. Enter an explicit device date and check the destination before receiving history. Auxiliary packets never assign authoritative race times.

Two Timy USB clocks need separate explicit IDs. Serial sources need separate ports. A and B cannot share an ALGE Results device ID. Saved backup connection settings omit passwords; the optional ALGE password checkbox uses Windows Credential Manager. Temporary import destinations and receive-from timestamps are not saved as reusable defaults. Start/finish difference warnings and missing-signal grace are editable helper thresholds, initially 1 ms, 10 ms and 5 s. ALGE Results monitoring waits at least 10 s for its polled data. These helper settings do not alter timing or FIS rules.

When combining an ALGE Results UTC clock with a locally dated USB/serial clock, explicitly enter the local clock's UTC offset in minutes for that race date. For example, `180` means local UTC+03:00. Leave the setting blank when both sources share a time basis. Mixed clocks with no explicit offset cannot be reliably matched; entering it affects comparisons and report matching, never the original captured timestamps.

## Timing report

The **Timing report** tab contains report equipment, people and connection defaults together. The **Timing devices** tab contains the actual device connections and capture settings.

Equipment defaults contain A/B timers, the start gate, start clock, A/B finish cells and optional separate start timers. Select a row, choose a cached matching homologation, apply it and enter the individual serial number. Equipment can also be entered manually offline. **Save report defaults** at the end of this tab saves equipment, people and report connections together for timing reports.

Selecting a cached homologation snapshots its expiry season and catalogue retrieval time with the equipment. These values survive event transfer and are checked against the report season even offline. Changing only the serial number preserves the evidence; changing the brand, model or homologation code clears it and shows **Manual / unverified** until a catalogue entry is applied again. Manual equipment still needs operator verification. The start clock is entered manually because the public catalogue has no start-clock category; the supplied XML description requires its brand, model, serial and homologation when a start clock is declared.

Save the chief of timing and calculations, timekeeper and connection defaults once. Surnames and nation codes normalize to uppercase. These are user preferences outside the series database. Reports use these saved values when opened. Saving preferences updates an open report; saved report revisions retain portable snapshots and approved XML remains unchanged. OpenSkiTime supplies the result-software name and version automatically.

Verification uses synthetic equipment and contacts. Tests cover endpoint authentication, malformed/error responses, shared credentials through actual Results and report submission helpers, Settings navigation, offline cache/default reopening, preserved report snapshots and rejected invalid replacements. Physical devices, live authenticated FIS responses and real FIS report acceptance need separate operational verification.
