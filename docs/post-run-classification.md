# Post-run classification review

Reviewed 2026-10-02. Select a competitor in any Timing grid and open **Edit classification**. DSQ, DNF, DNS, NPS and clearing a classification remain available after finishing a run or disconnecting capture. DSQ gate, reason/ICR reference and reporting judge are optional. The operator identity is required; an empty correction note uses an explicit default classification note. Quick status buttons and keyboard actions remain available.

Classification and its details are committed together in the append-only timing audit. Replay/reopening restores them; clearing or undoing the classification restores the underlying recorded/corrected time without modifying raw input. A subsequent DSQ quick action preserves existing DSQ details; other statuses clear them. An audited Run 1 finisher reclassification can update final results after Run 2 without rewriting its saved start order. Changed finisher times or newly eligible starters continue to require review of the Run 2 source results.

FIS result XML exports optional DSQ `Gate` and `Reason`. The reporting judge remains in the portable series and correction history: the reviewed protocol has no per-disqualification judge element. No database migration or new table is introduced.

Weather may retain an air temperature whose measurement place is unspecified. This is editable separately from measured start/finish temperatures; XML omits `Place` for that record. All entered temperatures are decimal Celsius values.

Synthetic times are restricted to separate operator-authorized test series and explicitly labelled in their audit history. They must not be interpreted as measured race times. Supporting historical penalty rule editions and reverse engineering third-party databases are separate work.

Verification covers post-finish DSQ/DNF, optional details, invalid gate rejection without mutation, atomic persistence, reopening, undo, final ranks after a late Run 1 DSQ, DSQ XML fields, weather without an invented location, and the actual desktop view command/selection workflow. External FIS XML acceptance and native Windows interaction are not established by these automated tests.
