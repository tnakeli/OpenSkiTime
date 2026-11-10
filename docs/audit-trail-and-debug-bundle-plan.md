# Audit trail implementation plan

Accepted scope: advanced-mode inspection and local support bundles using existing history and logs. Updated 2026-11-10. **No additional logging**: no new sinks, instrumentation, verbose mode, retention changes or diagnostic project. Operational behavior and limits are documented in [Audit trail and support bundles](audit-trail.md).

## Tasks

1. Isolate implementation on `feature/audit-trail`, transfer only the feature's planning documents, and exclude the unrelated security-audit skill and `skills-lock.json`.
2. Add typed application inspection contracts and a dedicated read-only SQLite adapter. Cover all existing history/current-state tables, page without the former 500-record cap, preserve exact timestamps/raw input, and load selected details/source links on demand.
3. Persist advanced mode outside series files. Put Audit trail immediately after PDF Factory, retain existing navigation guards, and provide Events, Raw timing, Diagnostics and Series & environment sections with filters and keyboard/copy support.
4. Inspect existing publisher/radio logs. Show missing desktop trace honestly. Keep original local log viewing separate from allowlisted support-export projections; never include wire XML or arbitrary external error text by default.
5. Add a reviewable local ZIP export: minimized diagnostics by default, optional issue text and explicit complete-series inclusion. Use a verified SQLite backup while capture is disconnected; preserve existing output until the entire archive is written, flushed and verified. Include hashes and collection limits/omissions.
6. Verify real read-only/persistence/export/UI boundaries with synthetic data, run affected builds/analyzers and unit/desktop/live tests, inspect layout and review the complete diff. Update developer documentation with actual behavior.

## Existing source inventory

| Source | Available information | Historical limits |
|---|---|---|
| `TimingAudit` | Operator/reason/time, before/after decisions, original observation keys, undo reference, manual timestamp/EET evidence. | Operator is a recorded string, not necessarily an authenticated identity. |
| `RawTimingPackets`, `AuxiliaryRawPackets` | Exact original bytes, session/sequence, receive timestamp, protocol/source/stream, including malformed/duplicate input. | Decoding is derived. Pending uncommitted input is not saved evidence. |
| `TimingCaptures`, `AuxiliaryCaptures` | Start/stop/clean-stop metadata, options/routes/clock group, calculation version and device information as recorded. | No complete transport retry history. A session with no recorded stop may simply be active. |
| `Runs`, `StartLists`, `StartListEntries` | Saved revisions/order/athlete snapshots, seed, rule version, points-list context, edits, approval/start metadata. | Saved snapshots must not be replaced with current registration when explaining historical state. |
| `RaceInformation` | Every saved revision, timestamp and portable race/jury/course/forerunner context. | No recorded operator/reason. |
| `ApprovedResults` | Immutable approved XML and source fingerprint/list references, penalties, approver/time. | Result-upload responses are session-only, not persisted in a result-submission table. |
| `TimingReports`, `ApprovedTimingReports`, `TimingReportSubmissions` | Every saved draft, approval and persisted submission-status row, including operator/reason/time and exact approved XML. | Persisted status is a historical response, not a newly checked remote status. |
| `TimingReportImages` | Image/OCR/engine/context evidence if rows exist. | The current receipt dialog does not persist its images/OCR here. |
| `ImportReceipts` | Source hash, applied revision, created/updated counts. | No accepted preview, original file, operator or timestamp. |
| Series, competitions, registration, participation, category and saved FIS points tables | Current state and saved point-list context. | No complete before/after edit history. |
| `PdfFactory` | Current profile and latest generation receipt per report, source version and hash. | Upserted receipts do not preserve older generations or failures. |
| Existing worker logs | Publisher/radio lifecycle, safe technical protocol events and locally available text. | Rotation/retention can remove older evidence. Opt-in FIS wire trace may include personal race data. |
| Desktop trace | Avalonia/PDF trace output while attached to a trace listener. | No general persistent desktop log file exists; it cannot be recovered retrospectively. |

## Required boundaries

- Inspection has no write DDL/pragmas, migration, correction, undo or repair action. Keep `.ost` format compatibility and permanent race history unchanged.
- Use exact integer 100 ns device ticks and stored precision. Receive timestamps never become race times. Keep current state, saved evidence and replay-derived values distinguishable.
- Do not hold race persistence locks while paging, displaying logs or writing a ZIP. A complete-series snapshot takes the existing cross-process capture lease, so active A/B/hand capture rejects that attachment. Diagnostics-only export remains available.
- Race data is included only by explicit complete-series selection. The snapshot is original evidence; it is not an anonymized database. Credentials and downloaded FIS caches are never collected from external stores. Arbitrary secrets typed into original evidence cannot be reliably recognized automatically; the original series must be reviewed and shared privately.
- Allowlist diagnostic output rather than copying log directories or redacting unknown free text. No automatic upload, email, issue creation or telemetry.
- Preserve the selected source identity, cancel stale reads when switching series, and report cancellation/omissions/failure honestly. Never silently drop a selected complete-series attachment.
- Test more than 500 events, equal timestamps, malformed/raw input, read-only files, capture leases, actual backup reopen/replay, sensitive legacy logs, cancellation and existing destination preservation. UI tests cover local preference persistence/failure, file switching and both supported window sizes.

## Later work requiring separate scope

A full durable history of registration/competition/category edits, accepted import previews, result-upload statuses and overwritten PDF generations would require additional transactional persistence. It is not reconstructed from current values. An incompatible future development schema follows [the database policy](development-database-policy.md); this implementation adds no schema changes or migrations.
