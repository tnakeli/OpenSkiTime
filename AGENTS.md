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
## Agent workflow

- Before making substantial changes, inspect the relevant existing code, tests, and documentation.
- For non-trivial work, create an implementation plan and break it into concrete tasks before editing code.
- Prefer extending existing architecture and patterns over introducing parallel implementations.
- Work through the tasks systematically and keep the plan updated if discoveries require changes.
- Do not stop after implementation: build the affected projects, run relevant tests, and fix failures caused by the change.
- Review the final diff for correctness, unnecessary changes, accidental regressions, secrets, personal data, and generated artifacts.
- Update relevant developer documentation when architecture, behavior, protocols, file formats, or operational procedures change.
- At completion, summarize what changed, what was tested, and any remaining risks or unverified areas.

## Code review priorities

When reviewing changes, prioritize:

- correctness of timing calculations and race rules
- preservation of full timestamp precision
- deterministic behavior
- concurrency, race conditions and process isolation
- durable persistence before acknowledging saved data
- preservation of original raw timing input
- auditability of manual timing corrections
- backwards compatibility of `.ost` files
- security issues, secrets and unsafe external input handling
- adequate automated tests for changed behavior

Treat timing, persistence and data-integrity regressions as high-severity findings.