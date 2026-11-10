# Audit trail and support bundles

Enable **Settings > About > Advanced > Enable advanced mode** to show **Audit trail** immediately after **PDF Factory**. The setting is stored in `ui-preferences.json` under `LocalDataDirectory.Path`, outside series files; `OPENSKITIME_LOCAL_DATA` redirects it. It defaults to off. A failed preference write keeps the previous setting and reports an error. Turning it off returns an active Audit trail workspace to Settings > About.

This feature uses existing saved history and logs. It adds no logging sinks, instrumentation, verbose mode, retention changes, database tables or file-format upgrades. Ordinary timing capture and audit persistence operate independently of the advanced-mode setting.

## Inspecting a series

The workspace has four sections:

- **Events:** saved timing decisions/corrections/undo, start-list revisions, primary/auxiliary capture metadata, every saved race-information and timing-report revision, approvals, persisted timing-report submission responses and stored evidence metadata.
- **Raw timing:** original primary and auxiliary packets with session, sequence, receive time, protocol/source/stream and byte count. Detail shows original bytes in base64/hex and escaped UTF-8, plus decoding derived from replay through the selected packet. Integer 100 ns ticks and source precision remain in decoded details. A later packet may complete an incomplete device message.
- **Diagnostics:** safe projections of available publisher/radio log events and application/runtime/OS information. Choose **View original local log** to inspect one original worker file locally; that text may contain personal data and is excluded from bundles.
- **Series & environment:** current series/competition/registration/participation/category state, saved FIS point context, runs, saved start-list entries, import receipts and latest PDF profile/generation receipts. Refresh also shows a technical environment and table-count summary in the detail pane.

Use source, competition, run, bib, operator, time-range and summary-text filters, then **Refresh**. Empty filters cover the whole series; **Clear filters** resets them. Enter timestamps with a UTC offset, for example `2026-11-10T12:30:00+02:00`. Ctrl+F focuses search; Enter applies it. **Previous**/**Next** use 100-record pages, with no 500-record history cap. Search covers record summaries, source names, competition labels and displayed person names; it is not a full-text search over every JSON/blob. In Diagnostics, search matches safe event/component text and the time filter applies to retained log records.

Select a record for all stored columns, including original decision JSON, reasons and old/new values. Available source links open saved lists, capture sessions, reversed decisions, approvals and original packets. Approved XML is shown exactly as stored. JSON is formatted for reading; malformed JSON remains visible as original text. Blobs over 1 MiB are identified but omitted from the text preview; the complete-series bundle preserves them. Text/XML is displayed as text, never executed as markup.

Record provenance distinguishes **Stored**, **Current state**, **Stored snapshot** and other source-specific limits. Absent timestamps are not invented. Event ordering uses UTC ticks with stable source/row tie-breakers while detail preserves the recorded offset. Device time is not inferred from receive time. Cross-device/process timestamps are not proof of causal order. Start-list approval/start and capture-stop milestones are fields on their saved record, rather than separately reconstructed events.

Inspection uses dedicated read-only SQLite connections and no race write gate. It never applies corrections, undo, repair, schema changes or upgrades. All row data is paged; selected raw decoding streams preceding session packets on demand. Changing/closing the series invalidates pending inspection results. A refreshed view preserves a selected record when it remains on the page.

Current registration is not a complete edit history. Import receipts do not include operator/time/original preview. PDF receipts retain only the latest generation per report. Result-upload responses exist only in desktop memory, unlike persisted timing-report submissions. Current receipt images/OCR are not persisted by the receipt dialog. The viewer cannot recover facts that were never saved.

## Existing log coverage

Worker logs remain in `%LOCALAPPDATA%/OpenSkiTime/LiveTimingLogs`, which currently ignores `OPENSKITIME_LOCAL_DATA`. Existing publisher/radio processes rotate at approximately 4 MiB plus one previous file per PID. This feature does not change that behavior. Desktop Avalonia/PDF trace output has no general persistent file sink, and Diagnostics reports that limitation.

Collection considers only non-recursive `publisher-<pid>.log`, `radio-<pid>.log` and their `.previous` files. Linked directories/files are excluded. Limits are 100 files, 8 MiB per file, 32 MiB per collection and 10,000 projected events. Active log reads use a fixed byte boundary and shared access; unavailable/rotated sources and unsupported/excess lines are reported as omissions.

Exported events use fixed, recognized lifecycle descriptions or a generic FIS protocol-event label. Free text, external error bodies, wire XML, names and credentials are not forwarded. `OST_FIS_WIRE_TRACE=1` can make original worker files sensitive; local viewing and exported projections therefore have separate boundaries. A PID is not a unique lifetime ID and events are not presented as a fully correlated process history.

## Creating a debug bundle

**Create debug bundle…** opens a review dialog. Choose the existing-log window (last hour, 24 hours, 7 days or 31 days), review the file list, then choose **Save ZIP**. The default is `debug-bundle.zip`. Export works offline, without a series and while timing is connected when no complete-series attachment is requested. It does not send or upload anything.

By default, the archive contains:

```text
README.txt
environment.json
configuration-summary.json      # source type, role/channel and shared-connection flag
series-summary.json             # format, revision and counts if available
diagnostics/worker-events.json  # safe events and collection omissions
manifest.json                   # scope, limits and SHA-256 entry hashes
```

All log-window boundaries use one clock reading, including the full 31-day option. The dialog keeps export status and the saved path above its action bar, independently of the scrollable description and content selection. Cancelling the file picker explicitly reports that no ZIP was created; a picker failure stays in the dialog with an actionable error. The native picker is owned by the support dialog.

Regression tests exercise the Save ZIP button through the actual Avalonia dialog with a substituted file picker, including destination selection, 24-hour/31-day ranges, cancellation and picker failure. Native Windows picker interaction still requires manual verification.

**Include my issue description** adds the operator's previewed text as `attachments/issue-description.txt`. Leave it off to exclude free text. **Include complete event series (personal race data)** adds `series/event-series.ost`, including all saved raw input, malformed/duplicate packets, audit history, reports, registration and embedded evidence/template bytes. This attachment is a complete original database, not an anonymized extract. Share it privately; ZIP provides no encryption. Secrets accidentally entered into original evidence/free text cannot be reliably detected automatically. Review that evidence before sharing.

No credential-store data, arbitrary preference/profile files, downloaded FIS caches, unrelated series, external PDFs/images/templates or original worker logs are collected. An embedded blob already stored in a selected complete series remains part of that original evidence.

The configuration summary uses enum/numeric role, source and channel values from the desktop selection, including the shared-connection flag. It excludes device/account identifiers, endpoints, replay paths and free-text firmware fields. These are current UI settings, which may differ from a historical capture's options in a complete series.

Disconnect and drain all A, B and hand capture before including the complete series. Snapshot creation acquires the same cross-process capture lease as race operation and rejects an active capture in any instance. It uses SQLite backup and verifies the copied format/integrity, including committed WAL content; it does not copy a live `.ost` file directly or checkpoint the source. The export never flushes pending forms or records uncommitted input as saved evidence. The source selected at review is retained, and the series identity is checked before attachment publication.

The archive is written to a task-owned temporary file beside the destination, closed/flushed, reopened and hash-verified before publication. The temporary full-series copy is removed before publishing success. Cancellation/failure preserves an existing destination and never reports a saved bundle prematurely. A failure of a selected full-series attachment fails the export; it does not silently downgrade the package. Missing optional logs/summary sources are listed as omissions. **Open folder** opens the published destination's folder.

If temporary-data cleanup fails, export reports the local paths that need operator attention instead of hiding a potentially sensitive retained copy. These paths are shown locally, not added to logs or a diagnostics-only bundle.

## Verification

Synthetic automated tests cover history beyond 500 rows at identical timestamps, filters, read-only byte preservation, incompatible formats, exact raw bytes, capture exclusion, verified backup reopen, preference persistence/failure, navigation placement/window sizes, file-switch invalidation, privacy of diagnostic ZIP contents, manifest hashes and cancellation preserving existing output. These checks do not replace physical-device or race-day load verification.

Validated on Windows on 2026-11-10: full solution build with zero warnings/errors; 595 domain/application/persistence tests, 199 desktop tests and 188 live timing tests passed. Headless renders were inspected at 980 x 680 and 1280 x 800, along with the support-export dialog. Focused checks also verify historical bib/name lookup, current-state row identity reuse, publication failure, retained sensitive temporary data and modal export choices.
