# M4 — Prepare runs

Implemented on `rewrite/codex`, 2026-09-27. Operator acceptance is pending; M5 timing capture has not started.

## Workflow and boundaries

- **Draw / Start lists** is a competition → run dropdown. No separate gender selector or large selection panel remains. The heading/title show competition code, run and current list state. Select an older saved version to inspect its snapshot. FIS draws include the whole competition; mixed entries are rejected explicitly rather than filtered. Local category draws remain a separate profile.
- **Mark run started** records operator/time on the latest approved list, locks its order and makes the next run available. Approval alone does not reveal Run 2. Existing later-run lists remain accessible on upgraded files. This progress marker does not start timing capture.
- Run 1 uses the cached FIS list effective on the competition date. Draw settings hold first bib, seed-group size and second-run reversal; operator/reason and a Jury confirmation are required. Repeated preparation saves a new draft; approval enables TSV/print-view export.
- Run 2 takes manually entered/pasted classified Run 1 times/statuses for every starter. Input is validated as a complete set and saved with the next start-list revision. Navigation cannot silently discard changed input. This temporary input boundary does not implement timing capture, elapsed-time calculation or official results.
- Each series file includes `Runs`, `StartLists` and `StartListEntries`. Transactions and optimistic revision checks protect writes. Stored snapshots retain inputs, seed, algorithm/rule version, operator/reason, timestamps and source-list reference. Men/Women cannot reserve overlapping bibs in one competition. Referenced competition edits/deletion and Run 1 redraw after Run 2 are blocked.
- Exports use the selected approved snapshot. TSV contains position and bib separately; the printable HTML is opened in a browser for Ctrl+P/PDF. The UI does not invoke a native printer or produce PDF directly.

## Rules implemented

Source: [FIS ICR July 2026, published September 2026](https://assets.fis-ski.com/f/252177/x/57a5e17fad/icr-04-09-2026.pdf), articles 617.3.3, 621.3, 621.9 and 621.11. Standard two-run SL/GS and one-run DH/SG only. First group defaults to 15; boundary ties expand it. Remaining athletes follow discipline points, with no-points athletes drawn last. Run 2 reverses the fastest 30 (or Jury-selected 15 fixed before Run 1), includes boundary ties, and retains bibs. Reversed ties use smaller bib first; outside reversal, equal times use larger bib first. Non-finish statuses do not start Run 2. Jury authorization is required for computer drawing.

The double-draw concept follows [FIS Alpine Data & Software Booklet, 15.2](https://assets.fis-ski.com/f/252177/8cc50a2939/20131210fis-alpine-data-software-booklet-28eng-29-v1-13.pdf). This implementation uses a versioned SHA-256 counter generator, rejection sampling and independent athlete/slot selections. Canonical input order makes replay independent of the grid's sort order. The UI supplies a fresh random seed; the domain itself performs no I/O.

Operator decisions still needed: validate examples against the intended race level; accept random ordering within tied-point groups after the first group; confirm draw-readiness fields (Code, both names, birth year, nation and field). No federation certification is claimed. Cup/youth/snow-seed rules, optional no-points subdivisions, three-run ENL and local category draws need separate profiles, not silent approximations.

## Verification

- Release build: zero warnings/errors. All 38 rewrite tests pass (29 domain/application/persistence, 9 desktop; none failed/skipped). Tests cover deterministic replay, seed-group/cutoff ties, reversal, statuses, invalid input, unchanged bibs, escaped exports, frozen revisions, stale/tampered plans and transferred-file reopening. New coverage exercises the actual dropdown commands, hidden/available later runs, persisted start/locking, male competition inference and rejection of partial mixed-field draws.
- Migration tests open actual M3 and initial M4 schemas through the production store and check pre-upgrade backups. The sixth migration adds nullable start time/operator; it preserves approved lists without inventing a start event.
- Rendered Avalonia workflow creates/approves both runs, pastes input, blocks unsafe navigation, writes actual TSV/HTML files and reopens persisted lists. Synthetic screens inspected at 1280×800 and 980×680; overlapping lower panels were replaced with Start list / Run 1 input tabs. A disappearing competition label was fixed and covered through the rendered control tree.
- Native Windows app launched using the local .NET host. Opened a synthetic saved series through the real file dialog and inspected Run 1/Run 2 lists, bib order and title context. No personal race file was opened. Headless rendering verifies the final label fix; a second native review of that fix remains for operator acceptance.
- Native printer/PDF output, full native keyboard rehearsal, Linux, high-DPI screens and use at a real race were not exercised. Browser automation was unavailable for print-view visual inspection. The earlier apphost-only launch requested an installed runtime; launching the DLL with the local .NET host worked.
- Dropdown/layout revision: inspected fresh rendered 1280×800 and 980×680 views. Removing the selector panel frees roughly three to four table rows; long file paths now trim within the available navigation width. This iteration was verified with Avalonia rendering and tests, not another native Windows session.
