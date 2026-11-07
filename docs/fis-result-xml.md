# FIS result XML conformance

OpenSkiTime writes the official result file of a FIS alpine race according to the [FIS Alpine Result XML Description, version 2.13 (2026-10-26)](https://assets.fis-ski.com/f/252177/x/c9387bbcb5/result-xml-alpine.md), sections 1–3 (individual competition). Team competitions (section 4) are not produced. The generator is `FisResultXml`; the Results view creates the file with **Save to XML** or **Send to FIS** (see [FIS XML and sending](results-race-information.md#fis-xml-and-sending)).

`FisResultXmlSpec` in the test project is the specification's element tables as an executable checklist: the elements allowed at each level in table order, required children, attribute domains, value formats and the section 1 rule that a used optional element holds its required children. It runs against generated files in the unit tests, including the 100-athlete two-run race with DNS, DNF and DSQ rows.

## How the specification is read

The specification is not consistent with itself. Section 1 says the examples "should only show the XML structure", so **the element tables are normative**. Where a table contradicts itself, the reading used by the rest of the document (and its example) is used. File name: `<NSA code><codex>.xml`, for example `FIN0592.xml`; UTF-8, no byte order mark, declaration `encoding="utf-8"`, LF line breaks.

| Item | Specification | Written | Why |
|---|---|---|---|
| Times (`Timerun1/2`, `Totaltime`, `Diff`) | tables: `MM:SS:ZZ`; example: `55.87`, `1:49.98` | `MM:SS:ZZ`, for example `00:48:01`, `01:35:62` | Tables are normative. **Open point to confirm with FIS** (below). |
| `Runinfo` attribute | table 2.3.1: `No`; example: `no` | `No` | Table. **Open point.** |
| Officials | Jury: technical delegate, chief of race, referees; Member: the others. `Member` is required (2.3.1) | Jury: `TechnicalDelegate`, `ChiefRace`, `Referee`; Member: `ChiefCourse`, `Startreferee`, `Finishreferee`. At least one Member is required; XML creation names the missing official. | Tables and the split shown in the example. |
| Technical delegate `Number` | not in table 2.3.2; present in the example | written | Only place that carries the TD number. |
| `Startelev` | table 2.3.5: `Startlelev`; 2.3.4 and example: `Startelev` | `Startelev` | The table spelling is a typo in a document that otherwise spells it `Startelev`. |
| Non-finisher `AL_result` | 3.11 requires `Racepoints` when present; example shows `Timerun1` only | omitted | A non-finisher has no race points; omission is valid (3.9: optional). Run 1 times stay in Results and the PDFs. |
| `AL_notranked` status | tables allow `DNS`, `DNS1`, `DNS2`, `DSQ1`, `DSQ2`, `DNF1`, `DNF2`, … | `DNF1`, `DNF2`, `DNS1`, `DSQ2`, … in two-run races, with `Run` | Listed in 3.8. |
| `Runinfo` children | 2.3.1 summary: `Course`, `Starttime`, `Weather`; 2.3.4 and example: `Course`, `Weather`, `Starttime` | `Course`, `Weather`, `Starttime` | Detailed table and example. |
| Race header `Gender`; competitor `Gender` | header `M`/`W`/`A`; competitor `M`/`F` | as specified | 2.1, 3.3. |
| `Racepoints`, `Appliedpenalty`, `Calculatedpenalty` | decimal | two decimals, decimal point | |
| `Season` | `2012` for June 2011 – May 2012 | the competition's FIS season | |

Written but optional: `Usedfislist`, `Appliedpenalty`, `Calculatedpenalty`, `Fvalue`, race header `Eventname`, `Place`, `Racedate`, `Tempunit`, `Longunit`, per run the course name and length, forerunners and weather, `Diff`, `Softwarecompany` (`OpenSkiTime project`), `Softwarename` and `Softwareversion` (the program version).

Not written: `Training`, `Speedcodex`, `Speedunit`, `Windunit`, `Endtime` (required only for OWG, WSC and WC), `Timingby`, `Dataprocessingby`, the `ChiefTiming` and `Assistantreferee` officials (the Results race information has no fields for them), jury e-mail and phone, and intermediate points (`Course/Intermediate`, `AL_resultdetail`). All are optional.

## Open points to confirm with FIS

The checks below follow the tables. If FIS reads the document differently, each is a one-line change in `FisResultXml`:

1. **Time format.** `MM:SS:ZZ` (tables) or `SS.hh` / `M:SS.hh` as in the example.
2. **`Runinfo` attribute case.** `No` (table) or `no` (example). The detailed `Run` elements in the example use `No`.
3. **Non-finisher `AL_result`.** Omitted; the example shows `Timerun1` only.

## Changes

2026-11-07: reviewed against version 2.13 (adds the `Run` attribute of `<Type>` for start lists and partial results, which an official result does not use). Added the required `Member` elements, moved `ChiefCourse`, `Startreferee` and `Finishreferee` from Jury to Member, put `Weather` before `Starttime`, added `Softwarecompany` and `Softwareversion`, removed the partial `AL_result` of non-finishers.
