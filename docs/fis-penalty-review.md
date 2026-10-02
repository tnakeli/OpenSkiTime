# FIS penalty review

Results calculates the penalty automatically from the saved starter points and the corresponding FIS points-list rule tables. The TD sees the rule source and read-only parameters, the best ten finishers (including ties at rank 10), the five selected finishers, the best five starters, A/B/C sums, `(A + B - C) / 10`, correction Z, category adder, bounds and applied penalty. Rows identify rank, bib, FIS code, name, birth year and nation. Starter rows include classification; DNF, DSQ and DNS in run 2 remain eligible when they started run 1. DNS/NPS in run 1 are excluded.

The finishers table separates actual race points from the capped race points contributing to C. Missing or excessive list points use the discipline cap; notes identify substituted caps. The double-cap minimum is displayed when required. Arithmetic uses decimal values and integer hundredths, with rounding at the existing FIS calculation stages. The applied penalty subtracts Z, adds the category adder and applies the effective bounds.

## Rule data and portable operation

The points-list ZIP supplies `ALxxxxcat.csv` (category minimum/maximum), `ALxxxxdis.csv` (discipline/gender F value, points cap, Z and adders 0–4), and `Fiscategory.txt` (alpine category race level). The reader validates headers, identities, duplicates and numeric limits. It selects the adder by the category's race level, never by a guessed default or the checkbox identifying a FIS competition. Missing category mappings are reported explicitly.

New draws store these typed rule tables in the existing start-list JSON snapshot. They travel with the series file and its backup; no schema migration or network request is needed when reviewing those results on another computer. Editing competition category selects the corresponding category from the saved tables on Results refresh. The reviewed calculation edition is 2026/27; other editions need explicit review before approval is enabled.

Older draws without rule tables can use the locally cached list only when its code and both validity dates match the drawn snapshot exactly. Without that matching list, Results explains what is missing and blocks penalty approval. Missing metadata, unsupported mappings and a list outside race-day validity also block approval. Athlete points are always taken from the draw snapshot, not silently updated from a newer cache.

## Review and limits

Reviewed on 2026-10-02 against [FIS Alpine Points Rules 2026/27, June 2026](https://assets.fis-ski.com/f/252177/x/ddef5ebef2/fis_points_rules_01-07-2026.pdf), §§4.1, 4.4.1–4.4.8, 4.5 and 4.9. The PDF defines the calculation; list-specific published tables supply the numerical profile. The implementation currently supports SL, GS, DH and SG. The TD review is available in the UI; printing/PDF layout remains a future task.

Synthetic tests verify list/category/gender joins, non-default F/cap/adder values, category bounds, wrong-list rejection, rule JSON round-trip and automatic desktop review after backup/reopening without a cached FIS list. Missing rule tables prevent approval. Existing penalty tests cover starter eligibility, substituted caps, rounding and tied selections. No downloaded athlete data is committed.
