# OpenSkiTime domain

Reference: `legacy-recovered-pre-codex` (`5505ceb`), replacing the incomplete `8259809` baseline. Sources: domain/application code, historical [Feature 001](../specs/001-event-series-management/spec.md), [Feature 002](../specs/002-competitor-grid-ux/spec.md), and [M0 runtime evidence](m0-evidence.md). Existing behavior, specification intent and proposed rules are distinguished below.

## Existing concepts

| Concept | Implemented behavior |
|---|---|
| Event series | Race weekend/week, not a season points championship. Name, location, organizer, dates, nation and season; shared competitors and competitions. Required strings are trimmed; end date cannot precede start. |
| Competition | Race with date, short label, discipline/type, course/altitude/homologation metadata, run count and intermediate count. Runs ≥1, intermediates ≥0; FIS race requires a race code. No actual Run entity. |
| Competitor | Series-local registration with GUID, uppercase surname, first name, birth year, optional FIS athlete code/nation/club/gender and series-wide bib. Duplicate name/year rejected on add; FIS code uniqueness is not enforced. |
| Participation | Competitor/competition pair, boolean and optional positive start order. Zero/many entries allowed. Missing record and explicit false differ in storage but are mostly treated alike by imports. |
| Category rule | Inclusive birth-year range, optional gender, label and display order. Individual matching works; no production resolver/editor connects it to the UI. |
| Import preview | Header-based TSV; FIS code match before case-insensitive surname/first-name/year match. New rows, bib/participation/scalar changes and warnings. Name/year changes require a FIS match. Preview and the two apply paths are not equivalent. |
| Desktop edit history | Manual field edits autosave and can be restored during the session; deletes are staged until Commit Changes. Log entries are capped at 200 and cleared by refresh/commit. This is transient undo, not timing audit. |

Disciplines: SL, GS, SG, DH, AC, KOMBI, OTHER. Race types: FIS, National, Club, Training. Gender: Male, Female, Other, often nullable. These are stored vocabulary, not a complete rulebook.

## Invariants to retain or resolve

- The rewrite stores exactly one event series per portable database file, including all its race data, raw timing input and audit history. Ownership and references stay within that series; moving the file preserves identity and history.
- Retain stable identity, same-series ownership, calendar dates for scheduling, uppercase surnames with diacritics, positive assigned bibs, unambiguous competition labels and one entry per competitor/competition. Enforce these on edits and in storage too.
- Bib uniqueness currently spans the series. Preserve this interpretation on legacy conversion; decide whether new races need competition-specific bibs. Bib, competitor ID and start position are distinct.
- Birth-year checks currently use `DateTime.UtcNow.Year`; pass an explicit reference date/year instead. Rules must not change with the workstation clock or locale.
- Legacy readiness requires first name and plausible year (surname is required at creation). FR-021 also requires code, gender and nation. Factories reject incomplete names/year, conflicting with specified missing-data workflows. Recommend storable drafts with explicit competition readiness; human judgment must define blocking fields versus warnings.
- Specification `Code` is conflated with FIS athlete code in implementation. Separate internal identity, federation identifiers and competition FIS code. Same name/year is a possible match, not proof of identity; conflicting/ambiguous identifiers need review.
- Nation validation checks length only; FIS-code validation checks length, not the documented alphanumeric alphabet. These are not federation validation. Approve identifier/code lists and enum validation explicitly.
- Dates outside series dates, altitude consistency, category overlaps and official-publication readiness have no established rule. Do not invent federation requirements from field names.

## Imports and reusable knowledge

Retain the conceptual pipeline: tokenize → map headers → project → match → preview → apply. Useful examples are in `TsvTokenizerTests`, `HeaderMapperTests`, `UpperCaseNameTests`, `ParticipationValueMatcherTests`, and bib/category tests. Keep valid examples while replacing assertions that mirror incomplete behavior.

Keep absent-column and blank-cell preservation by default. Legacy positive participation aliases are case-insensitive trimmed `Yes`, `Kyllä`, `Kylla`, `x`, `joo`, `k`. Model **absent**, **blank**, **explicit value** and **invalid** separately. Unknown values remain visible and unresolved until reviewed; a typo must not silently withdraw an entry. Explicit empty overwrite affects present columns only and must satisfy required-field rules.

Preview identifies source row/header, match reason, before/after values, errors, unknown columns, duplicates and uncertain names. Apply exactly the accepted preview, with target-series ID, source/options identity and database revision, in one transaction. Relevant changes invalidate preview. Retry must not duplicate a completed import.

The name heuristic supports comma format and single uppercase tokens, but `VAN DER POEL Jeroen` splits incorrectly despite research promising multi-token surnames. Combined names need editable review. Tokenization handles quoted tabs/escaped quotes but not multiline quoted cells or planned comma-delimited fallback. Gender exists in storage and scalar preview, but grid options `Men`/`Women` do not map to `Male`/`Female`, and new-row imports omit it. Association and discipline points remain absent. These are gaps, not desired rules.

Preserve the compact grid, editable scalar preview, manual participation toggle and selected-row TSV exchange conceptually. Do not preserve the reproduced export of false participation as `x`, missing participation/bib/gender in grid import, or persistence of discarded edits. Define autosave versus staged review explicitly; general edit undo must remain separate from durable timing corrections.

## Proposed race and timing model

These are new requirements/design, not recovered timing behavior:

| Model | Meaning |
|---|---|
| EventSeries / CompetitorRegistration | Weekend and athlete data, referenced without loading one giant aggregate. Points carry discipline, list/season and provenance when supported. |
| Competition / RaceEntry | Race configuration and entries, eligibility/category and effective bib under the approved policy. Participation is independent of run outcome. Optional imported bib references are competition-specific; Draw later allocates actual bibs. |
| Run / StartListRevision | Explicit numbered run and ordered entries. Freeze/revise with rule version and inputs; retain draw seed/algorithm version. Never infer later-run order from a mutable grid. |
| DeviceSession / RawTimingInput | Device/session, exact bytes/chunks, monotonic receive sequence, UTC receipt metadata and transport diagnostics. Append-only; survives parse failure. |
| TimingObservation | Parsed device time/channel/precision/sequence with raw references. Device details stay outside race rules. Unknown athlete/run association remains unresolved. |
| TimingAssignment / Correction | Explicit association or superseding decision: target revision, operator, time, reason, old/new values and source references. Local operator identity needs no cloud account. |
| RunResult / ResultRevision | Reproducible elapsed time, status/ranking from accepted assignments, corrections and versioned rules. Publication captures an immutable revision. |

Separate device time-of-day, receipt time and elapsed duration. Use integral units (for example .NET ticks) with explicit precision, not binary floating point. Resolve midnight rollover, synchronization/offsets, reset, source priority and ambiguous ordering explicitly. Retain source time and applied normalization. PC clock changes cannot alter captured elapsed times.

DNS, DNF, DSQ, missing finish, rerun and penalty are explicit states/decisions, never magic times such as zero. Missing data cannot produce a ranked finish. Preserve duplicate deliveries while preventing duplicate effect; use device/session/protocol evidence rather than timestamp equality alone. Replay the same observation, correction, start-list and rule revisions to obtain identical results.

Never edit/delete raw timing input to correct results. Correction and audit commit together; reversal adds history. Protect referenced entries/runs from destructive deletion once timing exists. Auditable history is not a claim of tamper-proof certification.

No existing algorithm defines timing precision/rounding, tie ranking, multi-run aggregation, second-run reversal, penalties or FIS eligibility. Obtain approved examples and applicable rule editions before implementation. FIS XML/API, ALGE protocols and official reports require authoritative samples/validation; legacy labels are insufficient evidence.
