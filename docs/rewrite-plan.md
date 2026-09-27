# OpenSkiTime rewrite plan

Work on `rewrite/codex`. The M1 implementation lives in the separate `rewrite/` solution; see [M1 evidence](m1-evidence.md) for verification and remaining desktop review. Recovered master is fixed at `5505ceb` by `legacy-recovered-pre-codex`; original `legacy-pre-codex` (`8259809`) and the initial M0 snapshot (`6744c1a`) remain available. Historical Feature 001/002 specs and ADRs are evidence, not execution plans. See [architecture.md](architecture.md) and [M0 evidence](m0-evidence.md).

## Incremental migration

1. Preserve the legacy tag/history. Build/run the reference separately; initially introduce the rewrite in a separate solution/source area, without wholesale copies of old layers. New code must not depend on legacy internals.
2. Use a distinct application identity and new database files, one per event series. Never let rewrite startup migrate the legacy database. Avoid dual writes or alternating binaries against one writable database.
3. Add a read-only legacy importer when the event/competitor model exists. Read a consistent SQLite backup and convert each selected series into its own new file; map IDs, dates, metadata, bibs, participation and category rules. Retain source backup/conversion provenance. Flag orphan entries, duplicate identifiers/bibs and unsupported data instead of silently repairing them. Verify fields/relationships and series isolation, not only counts. Unimplemented legacy fields have no data to migrate.
4. Compare approved scenarios with the reference after each milestone. Preserve correct behavior and record intentional fixes separately. Timing needs approved fixtures/hardware traces: legacy has no timing results to compare.
5. Cut over an event at a milestone boundary after operator acceptance. Rollback reopens the unchanged legacy backup; preserve/export new-only data rather than assuming the old schema can read it. Retain a conversion report with source identity and destination schema/version.

## Vertical milestones

Each milestone produces an executable workflow or meaningful acceptance test, including its persistence, failure handling and UI where relevant. Dependencies run top to bottom; optional integrations never gate offline operation.

| Milestone | Runnable/testable outcome | Acceptance evidence |
|---|---|---|
| M0 — Establish reference | Reproducible recovered-legacy build/test run and synthetic characterization fixtures. | Build/tests, migrations, use cases and headless Avalonia screens/commands verified; native Windows review remains open. Compare Feature 001/002 with actual save/undo/paste/copy behavior and reopened storage. Record defects separately; see evidence document. |
| M1 — Open a weekend | New desktop app creates/opens/closes one file per series, edits competitions, reopens offline and creates portable backup/transfer copies. | Implemented and covered by SQLite and headless UI tests. Native launch is blocked on the current machine by Smart App Control for unsigned local DLLs; second-machine transfer and native theme/DPI/operator review remain open. See [M1 evidence](m1-evidence.md). |
| M2 — Competitor desk | Keyboard-editable competitor/participation grid, category suggestions, filtering/grouping and legacy conversion reference tests. | Implemented; the desktop surface was revised with M3 after operator feedback. Native keyboard review remains open. See [M2 evidence](m2-evidence.md). |
| M3 — Import/exchange entries | Excel paste and manual grid edits share one reviewable change list; yellow cells, per-change Undo, confirmed Discard all, one atomic Save changes action and selected-row TSV clipboard copy. | Accepted after operator reported the Windows/Excel checklist passed; SQLite atomicity and headless UI workflows also pass. See [M3 evidence](m3-evidence.md). |
| M4 — Prepare runs | Create runs, review/freeze/revise start lists and print/export approved ordering. | Approved eligibility/draw fixtures; reproducible seed/version; bib versus position separation; no duplicate entry/position; invalid lists blocked. Start with explicit manual ordering if competitive draw rules are undecided. |
| M5 — Simulated timing | Simulator/replay feeds durable raw capture; operator resolves observations, views run/combined results, corrects and undoes with audit. | Golden fixtures; duplicate/out-of-order/split/malformed input, midnight/reset, ties/statuses, crash/restart/full replay, correction+audit atomicity, disk-full/backlog alarm and blocked unsafe publication. Closing/switching series drains capture safely without cross-file writes. |
| M6 — ALGE timing | One approved device end-to-end, then the second; Timy3/MT1 order follows available hardware. | Record firmware/interface/protocol; captured trace and real-device disconnect/reconnect/replay tests. Compare device evidence to calculated times. Unknown packets retained; stalled UI cannot stop capture. |
| M7 — Publish results | Review/finalize an immutable result revision, export local results/reports and reproduce that export. | Agreed layouts/rounding/statuses, file-write recovery and publication provenance. FIS XML is a separately accepted increment using official schemas/examples. The earlier FIS points-list competitor lookup is described in [FIS integration](fis-integration.md); any further FIS API and separate LAN/cloud live consumers follow only if needed. Outages never block timing. |
| M8 — Rehearse/cut over | Packaged Windows build completes offline rehearsal, transfers a full series file to another machine and converts a selected legacy series. | Measured workflow targets, capture soak/fault tests, restore/conversion rehearsal and operator acceptance of corrections/results. Transferred raw input, audit and result revisions reproduce identical results. Retain legacy executable/backup/history. |

Use prior targets provisionally: series/three races/import 100 competitors in under ten minutes; apply 100 rows in under three seconds; 95% of committed edits visible within 200 ms. Measure on an agreed laptop with up to 500 competitors/20 competitions and larger stress fixtures. Establish capture throughput, durable-commit latency and recovery targets from device rates/operator needs before M5; legacy documentation does not establish them.

In future Draw and timing workspaces, show the active competition code in the window title alongside the open event-series file.

## Human decisions

| Decision | Recommended starting point | Needed before |
|---|---|---|
| First race scope/rules | Name disciplines, rule edition, aggregation/rounding/ties, statuses, reruns, second-run order and penalties. Begin with one complete approved format. | M4/M5 |
| Identity, bibs, readiness | Decided: new bib references are competition-specific and normally blank; Draw later assigns actual bibs under FIS/local rules. Preserve legacy series bibs as conversion references. Draft competitors are allowed; M2 readiness is advisory. Decide entry-blocking fields before Draw. | Draw/M4 |
| Categories/points | Decided: birth year and gender determine category; M2 shows an unpersisted suggestion and flags overlaps. Confirm season/age boundary, gender eligibility, overlap policy and point lists before automatic assignment/draw. | M4; points before draw |
| Hardware | Confirm Timy3/MT1 models, firmware, transport, channel mapping, clock setup, backup timing procedure and protocol/trace access. | M5/M6 |
| Desktop/support | Validate compact-grid prototype with an operator; agree Windows/laptop/display targets and themes. One portable database per event series is decided. | M1/M2 |
| Save/undo/clipboard behavior | Decided for the competitor desk: manual edits, paste, participation and deletion are staged together; Save changes writes atomically, Undo reverses one draft change, and confirmed Discard all resets the draft. Bib is hidden here and belongs to Draw. Confirm final clipboard column order with operators. | Operator review |
| Recovery/publication | Agree operator attribution, correction permissions, retention/backup destination and acceptable recovery time; prioritize local reports versus FIS/live. | M5/M7 |

## Completion evidence

Exercise the production boundary claimed: migrations rather than `EnsureCreated`, importer rather than substitute transactions, reachable UI rather than unbound view models. Keep deterministic domain/timing fixtures fast; add focused SQLite and headless workflow tests, then real Windows/device rehearsals. Test names and task checkmarks are not evidence.

At implementation start, reconcile stale README/ADRs/Spec Kit pointers, metadata and missing license file, add CI, pin the .NET 10 SDK/compatible packages and review dependency warnings. Those files remain unchanged in this planning change. Keep accepted rule examples and milestone evidence near relevant tests; avoid another large methodology document.
