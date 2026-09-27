# Timing setup and operation

## Open the race

1. Prepare the competition's starting order in **Draw / Start lists**.
2. Choose **Timing → competition short name → Run**. The short name, run and codex remain visible, including in the window title.
3. Expand **Device connection**. Set the source, device date, start/finish channels and operator. Connecting locks this run's starting order. Default channels are C0 start and C1 finish.
4. Select a starter or type a bib, then **Arm start (F5)**. Arm the expected finisher with **F6**. These actions associate the next corresponding impulse; they do not create a time or send a command to the device. Multiple competitors can be on course.
5. Unmatched input appears in **Observations**. Select it and **Assign** the correct bib. A Timy/MT1 sequential record number is not assumed to be a bib. Only an explicit device bib can be assigned automatically.
6. Enter a reason before changing an assignment, ignoring an observation, correcting an elapsed time, or setting DNS/DNF/DSQ/NPS. **Correction history → Undo selected change** records a reversal. Clearing the corrected-time field restores calculation from impulses; **Clear status** removes the classification override.
7. **Disconnect** drains received input to disk. Once every starter is classified and observations resolved, **Prepare Run 2** opens the reversal/start-list workflow. A changed Run 1 invalidates an unstarted Run 2 order; recreate it. Bibs remain unchanged.

Timing changes save immediately, independently of competitor-grid Save changes. The right-hand grid uses **S** for start and **F** for finish. Show all includes assigned, ignored and duplicate observations; the screen shows the latest 500 observations/history entries, while the file retains all input/history. Sort result columns to inspect times, ranks or bibs.

## ALGE Timy 2 / Timy 3 native USB — Windows x64

- Install the manufacturer's [Timy USB driver](https://alge-timing.com/alge/download/driver/TimyUSB_Driver_Setup_2.80.0.exe) once, using an administrator account when Windows requests it. Native Timy USB is not a virtual COM port. Check Device Manager if the device remains unknown or reports a missing driver.
- In OpenSkiTime, select **Timy 2/3 · USB → Set up USB library** once while online. This downloads the manufacturer's [USB library package](https://alge-timing.com/alge/download/software/AlgeTimyUsbDLLExample.zip) into the current user's local application data, outside event files and the repository. Afterwards USB timing works offline.
- Use **PC Timer** on the device. Enter its firmware version in the connection settings for the session record. Leave USB ID empty for the first connected Timy, or supply the required ID when several are connected. Other Timys are not merged into that stream.
- Set/check the device clock and the date in OpenSkiTime before connecting. Use the device's maximum time-of-day precision, consistent with its printed tape; do not set the device output to hundredths merely because race results use hundredths. The application does not reset the clock, clear device memory, change firmware or send result data to the timing tape. Check the device's handshake configuration if USB times stop arriving; the PC Timer documentation describes the RTS/CTS setting.
- The main application remains .NET 10. ALGE's current mixed-mode library requires a small, isolated .NET Framework 4.8 Windows helper, included in the build. It forwards original byte events to the application. No PowerShell execution-policy changes are needed. The helper currently requires x64 Windows.
- Driver and vendor libraries are not redistributed in the repository/installer. Bundling them requires redistribution terms to be confirmed. Do not disable application control or antivirus to make them run; resolve a blocked component with a trusted signed vendor package/deployment.

Native USB helper startup has been verified. Physical impulses, firmware variants and cable reconnection still need a hardware rehearsal; the development computer currently reports a missing Timy USB driver.

## ALGE MT1

**USB / serial:** select the MT1's virtual COM port, refresh ports if necessary, and match the device's serial settings. The adapter defaults to 38400 baud, 8 data bits, no parity, 1 stop bit and no handshake. It retains fragmented ALGE ASCII input and retries an interrupted connection. Different connections are separate clock contexts: an elapsed time spanning a reconnect requires review.

**ALGE Results:** select **MT1 · ALGE Results**, enter start/finish device IDs and channels, and sign in with an ALGE Results account having the **Timekeeper** role. The two endpoints may use different devices. Synchronize the physical devices before use. The receive-from field uses UTC, for example `2026-09-27 10:00:00`; empty starts at connect time. A reopened capture restores its previous receive-from bound for recovery.

This integration polls every two seconds, uses count changes to fetch paged history, and periodically rechecks the interval to detect edits. A failed connection backs off and fetches missing history after recovery; limits and authentication failures are shown. Downloaded responses are preserved before interpretation, duplicate impulses do not time twice, and server changes require review. It is a read-only consumer and sends no device/trigger modifications. Cloud polling is not a substitute for local USB/independent backup timing.

Passwords remain in memory unless **Remember password** is selected. On Windows, remembered passwords use the current user's Credential Manager, separately per account; uncheck it when connecting to remove that saved password. Tokens, passwords and login responses are never stored in event files or timing traces. Live MT1/cloud operation remains unverified without the physical device/account.

## Results and recovery

- Calculation uses one integer 100 ns scale for every source, subtracts finish minus start at full received precision, then truncates each run's elapsed time to hundredths. Observation times show seven decimal places; trailing zeroes do not imply better device accuracy. At least millisecond source precision is required for automatic electronic timing; homologated timers must provide 0.0001 s or better under Timing Booklet 16.1. Equal times share rank (1, 1, 3). Combined results sum the truncated run times. Receive time is never used as race time.
- Hand/backup times require the FIS EET correction, whose average is rounded before calculating the replacement time-of-day. This calculation/report is not automated yet. **Correct time** records a verified net time and its reason; it does not perform EET. Do not substitute a raw hand/B finish for an A finish. Device-keyboard impulses are flagged but provisional calculations are not proof of a valid replacement time.
- Missing starts/finishes, extra assigned impulses, clock resets and unrecognized/device-correction input require review. Times spanning different ASCII connection clocks, less than 0.01 s or over two hours are not silently accepted. Device keyboard impulses are marked for backup verification. Unsupported penalties/reruns are not inferred.
- Original bytes, protocol version, receive sequence/time, device/date/channel settings and operator are kept in the `.ost` file. Corrections contain operator, reason, time, old/new values and raw references. SQLite rejects updates/deletes to raw input and audit rows.
- The status strip distinguishes saved packets from waiting packets. A storage failure keeps pending input in memory, shows **CAPTURE NOT SAVED**, and blocks close/file switching. Restore writable storage and use **Retry storage**, then disconnect. The bounded queue applies backpressure; hardware buffers can still fill during a prolonged fault.
- An unexpected application exit is detected on reopening and shown as an unresolved observation. Committed input and corrections replay; data still in a device/OS buffer or not durably committed may need recovery from device memory/independent timing. This is not a zero-loss guarantee for power failure or a failed disk.
- Disconnect before transferring the series. Use **Backup / transfer** for a consistent SQLite copy, including raw input and audit. Keep one authoritative event copy; no merging of independently timed copies is provided.

## Practice without hardware

Use a separate test event file. Choose **Simulator**, connect, arm a bib, enter a device time such as `12:00:00.0000`, and press **Test start**. Arm its finish, enter `12:01:02.3456`, then **Test finish**. The result is `1:02.34`. **Replay file** reads raw ALGE ASCII bytes through the same journal/decoder pipeline. Training/replay and real capture cannot be mixed in the same run.

## Protocol and rule references

- [Timy PC Timer manual](https://alge-timing.com/downloads/userGuides/Timy2-PC-Timer-BE.pdf), [Timy3 general manual](https://alge-timing.com/downloads/userGuides/Timy3-Allgemein-BE.pdf).
- [MT1 manual](https://alge-timing.com/downloads/userGuides/MT1-BE.pdf), [ALGE Results API](https://alge-results.com/api-doc/).
- [FIS Timing Booklet Alpine, Para Alpine 2.67, ICR 611 and section 14](https://assets.fis-ski.com/f/252177/x/7e6bba2b44/timing_booklet_alpine_para_alpine.pdf), [Alpine Data Booklet 1.15, sections 7–9](https://assets.fis-ski.com/f/252177/x/beca03a2ae/alpine-data-software-booklet-v1-15-with-appendix.pdf). This application is not a replacement for required independent timing systems/tapes or technical acceptance.
