# FIS timing precision review

Reviewed 2026-09-27 against the supplied PDFs and the [FIS Timing & Data library](https://www.fis-ski.com/inside-fis/document-library/timing-data). This checks the implemented alpine timing path, not complete FIS race readiness or Para classification.

## Sources

- [Timing Booklet Alpine, Para Alpine 2.67, 2026-06-16](https://assets.fis-ski.com/f/252177/x/7e6bba2b44/timing_booklet_alpine_para_alpine.pdf): sections 3, 13 (ICR 611), 14 and 16.1; especially printed pages 29–35 and 37. Supersedes the previously referenced 2.63.
- [Alpine Data Booklet 1.15, 2025-10-31, with Appendix A](https://assets.fis-ski.com/f/252177/x/beca03a2ae/alpine-data-software-booklet-v1-15-with-appendix.pdf): sections 7–10, 13–15. The library labels this download **1.16**, but the linked PDF's cover, footers and change log, like the supplied attachment, say **1.15**. Use the document's actual version, not the page label.

The attached PDF SHA-256 values, in the order above: `1a6f5bacef70e98cf3b02d128753106ebef7c5f63176c7e81e5943428ff6c57f`, `2ac0971f9f6291f6e945622c61e524acadcef4517e8c228b3d88954e9d346078`. PDFs and extracted material remain outside Git. Requirements below are paraphrased; page numbers identify the authoritative text/examples.

## Numeric contract

| Requirement | Implementation / finding |
|---|---|
| Use the complete device time-of-day precision (ICR 611.3.5; Data 7.3/8.1). | One integer 100 ns scale for every source. ASCII accepts 1–7 fractional digits; MT1 integer timestamps are retained exactly. Original bytes and source precision remain available. No floating-point race arithmetic or operator precision setting. |
| Calculate finish minus start before reducing precision (ICR 611.2.1). | Verified. `12:01:01.0099998 − 12:00:00.9999999 = 60.0099999 s → 1:00.00`. Truncating either input first can change the result. |
| Express each run's net time in hundredths by truncation. | Verified, including values one 100 ns tick below a hundredth boundary. Combined results sum already truncated run times. Extra digits cannot break a hundredth-level tie. |
| Preserve data through capture, display and replay. | Fixed simulator output at four decimals and observation/cloud-detail display at five. They now preserve/show seven. SQLite backup/reopen tests also exercise seven-digit input. No schema or persisted-data conversion. |
| Lower-resolution clocks use the same representation. | `10:48:31.86` is stored exactly as `10:48:31.8600000`; its source precision remains two digits. Zero-padding adds no measurement accuracy and does not make a hand time an electronic A time. |
| Device resolution differs from software representation. | Data 7.3 specifies at least 0.001 s; Timing 16.1 requires homologated timers to measure 0.0001 s or better and expose consistent precision on interfaces/tape. Keep the complete maximum precision delivered by the device. The software's existing three-digit minimum is an input guard, not proof of device homologation. Verify the Timy/MT1 configuration and tape during hardware acceptance. |

## EET is a separate calculation

Using one exact numeric scale does **not** remove the EET rounding step. Timing section 14 uses ten eligible paired time-of-day observations, normally preceding the missing time and completed with following observations if needed. Differences are backup minus A; their average is rounded at the applicable source precision, at least milliseconds. The replacement time-of-day is backup minus that correction. System A retains its full precision even when the hand clock has only hundredths. Do not round A to match the hand clock or calculate the average from truncated net times.

The printed examples provide future acceptance values: section 14.1 gives `13:04:12.240`, section 14.2 gives `10:07:51.6972`, and section 14.3 (hundredth hand clock) gives `10:07:51.7007`. An unrounded higher-precision mean would change these values, so it must not silently replace the prescribed rounding. The embedded ICR 611.3.2.1 still illustrates hundredth rounding; section 14 explicitly requires at least millisecond precision and supplies the detailed examples. Pin the implemented EET policy to these examples when that workflow is added.

EET calculation, selection of reference observations, A/B/hand system roles and the EET report are **not implemented**. A device-keyboard impulse is currently flagged in the result detail but can still yield a provisional time; that flag is not a verified EET. An operator net-time correction is audited but does not prove that the ten-pair calculation was done. These remain gaps before FIS race acceptance, not compliant behavior established by tests. Never mix an A start with an uncorrected B/hand finish merely because their timestamps use the same units or UTC.

Timy keyboard follow-up (2026-09-27): USB input with `C0M`/`C1M` delivers manual impulses with two fractional digits. The previous unconditional three-digit guard incorrectly hid their calculated time. Explicitly manual impulses now accept hundredth precision and display the net time immediately, preserving their manual marker and actual precision. The same rule applies to keyboard intermediates. Non-manual gate impulses retain the three-digit minimum; clock continuity, duplicate assignment and elapsed-time checks remain. This display calculation is not EET verification or evidence of official FIS timing acceptance. Synthetic SDK-envelope capture/reopening and mixed-precision regression tests cover the correction; no personal timing trace is committed.

Other open requirements: device-supplied net-time records need their own explicit input type (Data 8.2); official missed-time/asterisk presentation and Jury-reviewed correction reports (Data 13/14.3), result XML/TDTR, and Para-specific classification remain outside this implementation. The current decoder accepts time-of-day impulses, not arbitrary device net times. Original raw preservation, immediate durable writes, audited edits and no result writes back to the device tape already follow Data 7.2/8.3/9; physical timing tapes, synchronisation and independent backup still require rehearsal.

## Verification

`FisTimingPrecisionTests` adds 13 cases covering 1–7 digits, exact zero extension, sub-hundredth boundaries, mixed source resolutions, per-run truncation before totals and rejection of uncorrected hundredth-only input as electronic main timing. Existing MT1, SQLite transfer/replay and rendered desktop tests now verify all seven digits. Rewrite Release build: zero warnings/errors; **66 tests passed**, zero failed/skipped. Rendered timing reviewed at 1280×800 and 980×680. No physical-device precision or EET workflow acceptance is claimed.
