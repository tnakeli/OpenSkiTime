# Repository engineering rules

- Write all project documentation in English.

- Target .NET 10 LTS. Build desktop-first and Windows-first with an isolated path to Linux support. Local race operation must work offline.
- Separate UI, domain rules, timing calculations, persistence and device protocols. Domain/timing calculations have no UI, database, network or device I/O dependencies.
- Use explicit models and typed contracts. Prefer direct readable code and minimal dependencies; abstract real boundaries rather than hypothetical frameworks.
- Make race rules/calculations deterministic. Supply dates, clock context, rule versions and randomness explicitly. Never use binary floating point for authoritative timing values.
- Base alpine timing calculations on the FIS Timing and Data booklets; record reviewed editions and rule references in docs/fis-timing-review.md. Preserve full source timestamp precision on one integer scale; apply truncation/rounding only at the stage required by the rule.
- Preserve original raw timing input unchanged, including malformed/duplicate input. Never report capture as saved before durable commit.
- Record manual timing changes with operator, reason, time, old/new values and source references. Commit correction/audit together; undo adds history rather than erasing it.
- Validate complete changes before mutation. Use explicit transactions, short-lived persistence contexts, database constraints and recoverable failures.
- Apply exactly the accepted import preview. Absent columns and blank cells preserve values by default; clearing and ambiguous matches require explicit review. Normalize surnames consistently.
- Protect existing race data. During pre-release development, create new series files directly from the current model; do not implement database migrations or automatic schema upgrades. Reject incompatible development files without modifying them. Introduce migrations only when releases begin. Test backup/restore; never reset an operator database as an error-recovery shortcut.
- Store each event series in its own portable local database, including raw timing input and audit history. Keep user preferences separate; transferring a series must not depend on the original machine or user profile.
- Prioritize operator speed: dense consistent keyboard-friendly UI, preserved focus, clear status and actionable errors. Rendering and optional online integrations must not block capture.
- Test behavior through the boundary claimed, including critical failure/replay paths. Run relevant build/tests and report unverified checks honestly. Update affected documentation with behavior changes.
- Keep credentials, downloaded FIS lists and personal race data out of Git, committed fixtures, configuration and diagnostic logs. Use synthetic/anonymized examples.
- The root README.md is public user-facing documentation for the OpenSkiTime
  open-source project. Keep developer planning, architecture notes and agent
  instructions under docs/ or other appropriate development files.
- Do not expose internal agent workflows or development planning in user-facing documentation.
