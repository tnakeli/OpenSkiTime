# Integrated alpine timing report implementation plan

Prepared 2026-10-03. Status: implemented and verified locally following the operator clarification; external device/FIS acceptance remains unverified. FIS submission remains test-only; images are printed ALGE receipts or screen photographs, grouped as 1-N images per device. Hand start and hand finish are separate roles; B receipts may contain both.

## Outcome and scope

Add **7 Timing report** to the desktop application. Prepare, review, save and submit the FIS Timing and Data Technical Report using race data already available in OpenSkiTime. Optimize for minimal repeated entry, keyboard operation and visible unresolved work.

The proposed initial discipline scope matches current Results: DH, SG, GS and SL, with the run formats currently supported by the application. Parallel, combined and additional alpine formats require an explicit scope decision. Do not implement other sports, Timing Support Systems, PDF generation or EET calculation. Results Software is populated from OpenSkiTime's product name and build version.

B connection to the computer is optional. This does not mean that the physical backup timing required for the race is optional. A disconnected backup system can supply report evidence later through a device import, image import or manual entry.

## Sources reviewed

- User-supplied `C:/Users/tniem/Downloads/timing-report-xml.md`: version 1.17, updated 2026-09-10; sections 1, 2 and 4.1.
- User-supplied `C:/Users/tniem/Downloads/timingreport_user_manual.pdf`: version 13, 2026-06-05; sections 3.5-3.9, 4, 5 and alpine examples in 6.1-6.2. Relevant text and rendered forms were inspected.
- Existing [FIS timing review](fis-timing-review.md), [timing operation](timing.md), [Results operation](results-race-information.md), and active application source.
- [FIS public OpenAPI](https://api.fis-ski.com/public-docs?public-api-docs.json): timing-device homologation endpoint.
- [Tesseract documentation](https://tesseract-ocr.github.io/tessdoc/) and [handwriting limitation](https://tesseract-ocr.github.io/tessdoc/FAQ.html). OCR engine selection remains subject to representative input tests.

Source documents, downloaded catalogs, credentials and real race images must remain outside Git. Update `fis-timing-review.md` with the implemented rules and verification evidence during implementation, rather than presenting this proposal as compliance evidence.

## Operator workflow

1. Open **7 Timing report** for the shared active competition. Reuse the competition, date, category, codex, TD and run context; do not duplicate their editing forms.
2. Show a compact overview with readiness, missing required fields and the effective equipment/official defaults. Use report subtabs **Overview**, **B system** and **Hand timing**. The overview compares A/B/hand evidence per run; supporting evidence grids show the underlying observations.
3. Fill A evidence automatically from committed, assigned observations. Select the first and last competitors who completed the run in actual chronological order, plus the fastest eligible A result. Bib numbering does not determine first/last. Preserve source links and flag manual corrections requiring explanation.
4. Fill available B evidence automatically from the independent auxiliary capture. Allow temporary device import and multiple-image drag and drop in B/Hand subtabs. Offer a file picker for the same action.
5. Review source image crops/text beside proposed assignments and differences from A. Accept all unambiguous proposed matches together or correct individual cells. Missing or ambiguous matches remain blank; existing accepted values are preserved by default.
6. Enter or select explicit synchronization evidence, missing-A explanations and comments. Never infer synchronization from connection time or the first packet. Physical start/finish connections and voice communication describe the timing installation, not the USB connection to the PC.
7. Review the report and explicitly record certification. Generate an immutable XML revision, export its exact bytes or submit that revision through the shared FIS submission workflow. Show transport acceptance separately from final FIS processing outcome.

Allow incomplete drafts to save with actionable validation. Unverified OCR proposals cannot become certified evidence silently. Certification is an operator assertion, never an automatic validation result. A later edit or changed source marks the current working report as requiring review without altering prior approved revisions.

## Architecture and persistence

Keep .NET 10 and the current boundaries. Add explicit models for report drafts/revisions, equipment snapshots, synchronization evidence, auxiliary source roles, raw evidence, proposed/accepted associations and audit entries.

- **Domain/Timing:** deterministic association, report sample selection, timestamp comparisons and rule validation. Explicit clock/date/rule context; integer 100 ns ticks with original source precision. Net times use the existing FIS calculation/truncation policy. Never reduce A precision to match hand clocks.
- **Application:** report assembly, import preview/apply, auxiliary capture coordination, revision approval and typed OCR/store/transport contracts.
- **Devices/integration adapters:** reuse Timy USB, MT1 serial and ALGE Results sources and decoders. OCR is an isolated adapter; no OCR or network dependencies enter domain calculations.
- **Persistence:** raw packets, original images, OCR interpretations, reviewed associations, report drafts, exact approved XML and audit in the portable series database. Manual changes retain actor, reason, time, old/new values and source references; correction and audit commit together. Undo appends history.
- **Desktop:** focused views/view models, drag/drop, preview, keyboard editing, status and short-lived B display. Rendering and OCR cannot block capture.

Current `TimingWorkspace` couples capture to authoritative replay, assignment and race queues. Do not use it unchanged for B. `TimingReplay` currently reads all capture sessions, and `TimingStore` permits only one active capture session. These are explicit implementation changes, not configuration additions.

Create an auxiliary capture path reusing raw-journal/decoder mechanics, with explicit roles such as B, HandStart and HandFinish. Filter authoritative replay at its storage/query boundary so auxiliary observations never reach A assignment, results, start-list seeding or live publishing. B evidence may support a later EET feature through source references, but cannot substitute for A in this release.

Refactor capture ownership to support multiple registered sessions under one process-owned series lease. Preserve exclusive ownership against another process. Stopping one source must not release another source's lease. Drain all sessions before closing/switching the series. Keep queues independent, transactions short and bulk auxiliary work bounded so it cannot starve A durable commits. A genuine shared-disk failure still requires a visible recoverable error.

Create new development databases from the new current model. Reject incompatible old files without modification; no migration or automatic upgrade. Backups must carry original evidence and approved revisions and reopen without machine-local image paths or preferences.

## B monitor and device import

Add an optional B connection with independent status and explicit device selection. Prevent A and B from claiming the same physical input. Timy connection IDs are not reliable physical identities; dual-Timy SDK operation requires a hardware acceptance check.

In Timing, **Show B** displays B timestamps, elapsed/net times and A differences for 30 seconds. It must preserve focus. The button gains an amber warning badge, while a red message identifies an excessive difference or a missing expected signal. Unresolved warnings remain visible after the temporary details close. An intentionally unconfigured B source does not produce missing-signal alerts.

Compare both corresponding start/finish impulses and net times when available. Timestamp matching tolerances and warning thresholds are separate settings. Missing signals are detected against committed A events after a transport-aware grace period, not merely because no competitor has passed recently. ALGE Results polling/recovered history can arrive later than direct serial input. Thresholds are operational aids, not substitutes for FIS synchronization requirements.

Temporary import chooses run, B/hand role, start/finish mapping and history window. Reuse all supported device families, including ALGE Results; network sources still require their service connection. Separate hand-start and hand-finish devices are valid. Current adapters receive device output but do not prove automatic memory-download support; initially support operator-triggered device transmission, implementing automatic download only against a verified device protocol. Imported history must not trigger live missing-signal warnings.

## Image recognition and association

Process images locally/offline by default in a cancellable background worker. Preserve original files in the series; retain OCR engine/version, recognized text, bounding boxes and match decisions. Bound image size and work queues.

Choose the concrete OCR engine after testing representative printed clock tapes, display photographs and, if required, handwritten sheets. Tesseract is a possible printed-text implementation, not a promised solution for handwriting. Windows-only OCR must remain replaceable for Linux and must not introduce an unplanned packaging requirement.

Deterministic association considers role/channel, bib when available, run, chronological order, device date/clock identity and an explicit tolerance. Hand times use a wider tolerance than electronic B. Handle midnight, clock resets, overlapping images, repeated timestamps and partial rows. A source observation cannot silently fill multiple unrelated targets. A suspected clock offset is shown for explicit review rather than used to conceal disagreement.

The accepted preview is bound to the target series, run and source revision. Revalidate before applying; commit exactly accepted assignments and audit atomically. A changed target requires a refreshed preview. Blank input preserves values; clearing and replacement of existing evidence are explicit actions. An import without a reliable match leaves the report cell empty.

## Settings and shared FIS access

Extract the current inline Settings into a tabbed view:

| Tab | Contents |
| --- | --- |
| FIS | Existing top API key as the sole credential; connection/cache status and homologation refresh. |
| Timing devices | A connection, optional B connection and comparison preferences. Report equipment identities, serials and homologations belong to Timing report. |
| Timing report | Reusable report equipment, identities, serials, homologations, physical connection layout, Chief of Timing and Calculations and Timekeeper defaults. |
| Live timing | Existing live publishing settings moved without losing their values or behavior, where currently part of Settings. |

Keep reusable preferences separate from race data. Copy effective device/official values into the report, with explicit per-race overrides. Changing preferences must not rewrite existing report snapshots. Normalize surnames consistently. Do not require application restart to use new defaults for a new report.

Remove the lower Member Section token field and its duplicate save/read path. Results and Timing report read the existing top FIS API key. Retain endpoint-specific authentication headers: timing homologations use `X-Api-Key`; competition-file submission uses Bearer. Show permissions/authentication failures clearly without logging secrets.

Add a typed client for `GET /homologation/timing-devices`, with local cache, refresh status, manual entry and offline use. This is separate from existing course homologations. Filter appropriate device categories and preserve the selected homologation details with the report. Evaluate validity using available race-season evidence; do not infer historical validity solely from the API's current-valid flag.

## XML and submission

Implement the supplied v1.17 AL schema structure: UTF-8, no DTD, no empty optional elements; `Raceheader` with `Sector=AL` and `Type=TR`; `AL_race` jury/TD; `AL_timingreport` with contacts, relevant devices, software, connections, synchronization, run evidence, missed-A records and certification.

Validate mandatory fields by discipline/category/level using section 4.1, rather than implementing all-sport forms. First/last/best net values must agree with their authoritative result sources. A generic manual net correction does not prove that a result originated from A; collect replacement-system/reason provenance where needed. Keep unsupported or unknown provenance visible.

Reuse a shared competition-file submission client for exact approved payloads. Isolate responses by series, artifact kind and approval revision. Retain submission UUID and outcome in the series so status checks can resume after reopening. Do not automatically retry an uncertain POST or report a timeout as successful publication. XML exports of Results and Timing report must not silently overwrite one another even when FIS requires the same NSA/codex basename.

Resolve these interoperability points during implementation:

1. Current Results is explicitly test-only (`testMode=true`). Decide whether this task preserves test mode or adds real production sending for both artifacts. No real submission is made during implementation checks.
2. Supplied XML section 1.2 documents email transmission; the existing Member API exposes generic XML upload. Verify Timing report acceptance and permission behavior against its concrete contract/test validation before claiming integration success. Do not add unsolicited email-account configuration.
3. Section 2.4.15 and the example disagree on `Allresults` attribute spelling (`System` versus `SystemA`). Resolve using canonical FIS validation evidence and document the decision.
4. XML season description says July-June; existing `FisSeason.FromDate` rolls over in June. Prefer stored FIS calendar season and verify fallback semantics before reusing the helper.

## Delivery sequence and parallel work

1. Agree on the remaining product choices below, define source-role/report contracts and schema ownership, and run the small OCR feasibility check.
2. Parallel implementation streams after shared contracts stabilize:
   - Auxiliary capture, session ownership, persistence and replay isolation.
   - Report rules, immutable revisions, XML and shared FIS submission.
   - Tabbed Settings, single credential, equipment lookup and report UI.
   - Image import, deterministic matching and review UI when agent capacity permits.
3. Integrate through one owner for shared composition/navigation files. Review cross-module invariants, then run integration and desktop tests.
4. Update affected English documentation and report verified versus unverified device/FIS checks.

## Acceptance checks

- Simultaneous A/B capture, independent stop/failure, raw malformed/duplicate preservation, durable-save status, restart/replay and lease ownership.
- B/hand inputs, imports and failures cannot change authoritative A snapshots, results, start-list inputs or live output.
- Midnight, differing source precision, reordered bibs, nonfinishers, ties, manually corrected times and explicit synchronization selection.
- Synthetic image fixtures exercise actual OCR and drag/drop through accepted portable storage; fake OCR only tests orchestration. Ambiguous matches, overlapping pictures, blank-preservation, stale previews and audit/undo receive separate coverage.
- Valid/invalid XML fixtures, per-discipline required fields, automatic software identity, approval immutability and changed-source review.
- Shared credential use for Results/report; exact approved bytes, response isolation, redaction, rejected uploads, uncertain delivery and resumable polling.
- Keyboard navigation, compact layout at 1280x800 and 980x680, focus preservation, B overlay expiry and persistent warnings.
- Real SQLite backup/restore on a reopened file, original evidence portability and non-mutating rejection of incompatible files.
- Relevant .NET 10 build/tests, then the application solution suite after integration. Physical dual-device operation and external FIS acceptance remain explicitly unverified until actually exercised.

## Resolved operator choices

- Both XML workflows remain test-only; production submission is excluded.
- Images are printed ALGE receipts or photographs of computer/web screens, not handwritten sheets. One device may supply multiple overlapping images. Hand start and finish are separate roles; B may contain both channels.
- The booklet's 1 ms common-impulse synchronization check remains separate from configurable race monitoring: initial start/finish differences are 1/10 ms, respectively. Matching defaults are 1 second for B and 2 seconds for hand observations, with ambiguous matches left unresolved.

The implemented format scope is standard DH/SG/GS/SL already supported by Results. See [timing report operation](timing-report.md) for the current workflow and validation limits.
