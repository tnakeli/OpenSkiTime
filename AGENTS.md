# Repository engineering rules

- Target .NET 10 LTS. Build desktop-first and Windows-first with an isolated path to Linux support. Local race operation must work offline.
- Separate UI, domain rules, timing calculations, persistence and device protocols. Domain/timing calculations have no UI, database, network or device I/O dependencies.
- Use explicit models and typed contracts. Prefer direct readable code and minimal dependencies; abstract real boundaries rather than hypothetical frameworks.
- Make race rules/calculations deterministic. Supply dates, clock context, rule versions and randomness explicitly. Never use binary floating point for authoritative timing values.
- Preserve original raw timing input unchanged, including malformed/duplicate input. Never report capture as saved before durable commit.
- Record manual timing changes with operator, reason, time, old/new values and source references. Commit correction/audit together; undo adds history rather than erasing it.
- Validate complete changes before mutation. Use explicit transactions, short-lived persistence contexts, database constraints and recoverable failures.
- Apply exactly the accepted import preview. Absent columns and blank cells preserve values by default; clearing and ambiguous matches require explicit review. Normalize surnames consistently.
- Protect existing race data. Test schema upgrades and backup/restore; never reset an operator database as an error-recovery shortcut.
- Store each event series in its own portable local database, including raw timing input and audit history. Keep user preferences separate; transferring a series must not depend on the original machine or user profile.
- Prioritize operator speed: dense consistent keyboard-friendly UI, preserved focus, clear status and actionable errors. Rendering and optional online integrations must not block capture.
- Test behavior through the boundary claimed, including critical failure/replay paths. Run relevant build/tests and report unverified checks honestly. Update affected documentation with behavior changes.
- Keep credentials and personal race data out of committed fixtures, configuration and diagnostic logs. Use synthetic/anonymized examples.
- Legacy Spec Kit files and legacy source code are historical reference material.
  Do not execute old Spec Kit workflows or treat completed tasks as proof of
  correct behavior. Use them only when relevant to the current task.
- The root README.md is public user-facing documentation for the OpenSkiTime
  open-source project. Keep developer planning, architecture notes and agent
  instructions under docs/ or other appropriate development files.
- Do not expose internal agent workflows, rewrite planning or historical Spec Kit
  process in user-facing documentation.
