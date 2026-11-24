# Update from Ski-Alp-Pro (test use only)

**Settings > About > Advanced > Update from Ski-Alp-Pro** opens an event saved by Vola Ski-Alp-Pro, shows its statistics and imports it into the open event series. It is a test tool: use it to bring old races into OpenSkiTime, or to test OpenSkiTime beside Ski-Alp-Pro by drawing there and importing the Run 1 order and bibs. The button is shown in advanced mode only and needs an open event series without connected timing or unsaved changes. Vola and Ski-Alp-Pro are trademarks of their owner; OpenSkiTime is not affiliated with it.

## What it imports

1. Choose `EventNNN.scdb`. Start lists and times are in `EventNNNEx.scdb`; keep both files in the same folder. Without it only competitors and bibs are known.
2. The dialog shows the codex, race, date, discipline, competitors and, per run, starters, finished, DNS, DNF, DSQ, other, without result, and impulses. Missing data counts as zero.
3. **Data** offers what the file holds: competitors only, Run 1 starters, Run 1 with times, Run 2 starters, Run 2 with times. Each choice contains the ones before it.
4. **To competition** is the competition with the same codex. When the series has none, the dialog asks: the default is a new competition with the file's codex; another competition or another codex can be chosen.
5. Importing into an existing competition **replaces all its data** (entries, start lists, timing input and changes, approved results and timing reports) and needs a confirmation that names the amounts. Competition settings and race information stay; only missing intermediates are added. A verified copy of the complete series is saved first as `<name>.before-import-<yyyyMMdd-HHmmss>.ost` next to the file.

Competitors are matched by code, otherwise by name and birth year. Values of the file replace saved ones, blank values keep them. FIS points of the file are saved with its list code. When neither the series nor this computer knows that list, it is valid on the imported race dates only and has no penalty rules, so a penalty needs the list itself.

## Rules

- **Start lists** stay verifiable. A list is a normal draw of the same entries with one recorded change, `ImportOrder`, that holds the file's order and bibs; the rules that check a drawn list reproduce it exactly. Run 2 is calculated from the imported Run 1 with the file's order on top. A Run 1 finisher missing from the file's Run 2 list is placed last without a result; a Run 2 starter without a Run 1 time refuses the Run 2 import.
- **Times** come from the file's impulses. Each row is journalled unchanged as a raw packet (`ski-alp-pro/v1`, JSON) of a capture session marked **Simulation**, decoded on the integer tick scale and truncated like device input. Rows Ski-Alp-Pro does not count are information only; impulses without a starter are ignored by a recorded decision. When the impulses do not give the file's own net time, that time is recorded as a correction. DNS, DNF and DSQ (with gate) are recorded classifications. Every decision carries the operator, time and the SHA-256 of the source files.
- **Simulation** timing cannot be mixed with device timing in a run, certified in a timing report or sent to FIS as official results.
- **Safety.** Source files are opened read-only and never written. The import is built and validated completely, then saved in one transaction; a failure changes nothing. It is refused while timing capture is active. This is the only operation that removes protected history rows: the delete triggers are taken out and put back unchanged inside the transaction, and the resulting schema must equal the schema before.

## File format

The format was derived from events saved by Ski-Alp-Pro 14.1 (two-run slalom). Both files are SQLite databases; text is in the Windows-1252 code page.

| Table | Used columns |
| --- | --- |
| `TPARAMETERS` | `SOTWARE` (contains SKIALP), `CODEX` (`FIN0592.718`: nation, codex, TD number), `DATE` (days since 1899-12-30), `DISCIPLINE` (0 DH, 1 SG, 2 GS, 3 SL, 4 combined; only 3 is confirmed by a real event), `HEAT_NUMBER`, `INTER_NUMBER`, `TIME_ACCURACY`, `IMPORT_LIST_NUMBER`, `SEPARATE_FILES`, `TITLE1`, `LOCATION`, `FIS_CATEGORY`, `HEADER_FIS_*` |
| `TCOMPETITORS` | `C_NUM` bib, `C_LAST_NAME`, `C_FIRST_NAME`, `C_CODE`, `C_SEX` (1 men, 2 women), `C_YEAR`, `C_NATION`, `C_CLUB`, `C_I22` points list, `C_I29`–`C_I33` DH/SG/GS/SL/AC points × 100 (100000: none) |
| `TSTARTLIST_HEATn` | `C_LINE` order, `C_NUM` bib |
| `TTIMEINFOS_HEATn` | `C_NUM`, `C_STATUS` (0 time or none yet, 1 DNS, 2 DNF, 3 DSQ), `C_TIME` net milliseconds, `C_PENALTY`, `C_DATA1` gate, `C_REASON` |
| `TTIMERECORDS_HEATn_START` / `_FINISH` / `_INTERn` | `C_LINE`, `C_NUM`, `C_STATUS` (0: counted), `C_HOUR2` time of day in microseconds |

## Verification

Tests use synthetic files: reading, statistics, missing times file, foreign files, order/bibs/times/statuses, precision, replacement with backup and unchanged schema, failed import, capture in progress, corrected times, Run 2 differences, Club races, and the desktop workflow. On 2026-11-24 four real two-run slaloms (Ruka, March 2026) were imported outside the repository: every time, status and start order matched the file without corrections, also when importing again over the first import, and the Run 2 order OpenSkiTime calculates equalled the file's. Other disciplines, single-run events, events with several codices and events saved as one file are not verified with real files. Only Run 1 and Run 2 are imported.
