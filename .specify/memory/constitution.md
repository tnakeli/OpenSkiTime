<!--
SYNC IMPACT REPORT
==================
Version change: (initial) → 1.0.0
Rationale: First ratification of the Open Ski Time constitution. MAJOR bump from
template placeholders to a concrete governing document.

Modified principles (template → final):
  - PRINCIPLE_1_NAME → I. Race-Office Practicality (NON-NEGOTIABLE)
  - PRINCIPLE_2_NAME → II. Modern, Compact UX (No SkiAlp Pro Carryover)
  - PRINCIPLE_3_NAME → III. Modular Architecture & Separation of Concerns
  - PRINCIPLE_4_NAME → IV. Offline-First, Cloud-Optional
  - PRINCIPLE_5_NAME → V. Tested, Documented, Reviewable Change
  - (added)         → VI. Secrets & Credentials Hygiene
  - (added)         → VII. Data Import Fidelity

Added sections:
  - Technology & Platform Constraints (replaces SECTION_2)
  - Development Workflow & Quality Gates (replaces SECTION_3)

Removed sections: none (template placeholders replaced).

Templates requiring updates:
  - ✅ .specify/memory/constitution.md (this file)
  - ✅ .specify/templates/plan-template.md — Constitution Check aligns with these
       principles; no edit required at v1.0.0 (generic gate language still valid).
  - ✅ .specify/templates/spec-template.md — generic; aligns.
  - ✅ .specify/templates/tasks-template.md — generic; aligns.
  - ⚠ README.md — pending creation; see deferred items.

Deferred / Follow-up TODOs:
  - TODO(README): Create root README.md with project overview, screenshots /
    screenshot placeholders, and links to .specify/memory/constitution.md.
  - TODO(ADR): Establish docs/adr/ directory for architecture decision records
    (e.g., Avalonia UI, SQLite, live-timing transport).
-->

# Open Ski Time Constitution

Open Ski Time (technical name: `OpenSkiTime`) is open-source alpine skiing race
timing and race management software, distributed under the MIT License. This
constitution defines the non-negotiable principles that govern its design,
implementation, and evolution. It supersedes any conflicting convention,
preference, or precedent elsewhere in the repository.

## Core Principles

### I. Race-Office Practicality (NON-NEGOTIABLE)

Open Ski Time MUST be usable by real race offices, timing teams, Technical
Delegates, referees, and organizers under live event pressure. Every feature
MUST be evaluated against a concrete race-office workflow before it is
considered complete. If a feature cannot be demonstrated to help (or at
minimum not hinder) a real race scenario, it MUST NOT ship.

Rationale: Timing and race management software fails when it is built for
demos rather than for the chronograph cabin. Practical viability is the
product's reason to exist.

### II. Modern, Compact UX (No SkiAlp Pro Carryover)

The product MUST NOT replicate the SkiAlp Pro user experience. The UI MUST be
modern, clean, compact, and SaaS-style, while remaining fast and information-
dense enough for stressful race office and timing situations. Users MUST be
able to perform core workflows without reading large documentation. Error
states MUST be explicit, recoverable, and understandable. Automation is
encouraged, but the user MUST always remain in control of the race situation.

The initial UI language is English. The architecture MUST support
localization as a later, non-breaking addition.

Rationale: The market expectation has moved past 1990s/2000s desktop UX, but
race-office workflows still demand density and speed; both constraints must
hold simultaneously.

### III. Modular Architecture & Separation of Concerns

The codebase MUST keep the following concerns in separate, independently
testable modules / projects:

- UI (desktop shell)
- Core domain model (events, competitions, competitors, participation, runs)
- Timing master (authoritative timing state)
- Live timing (separate small app/service; see below)
- Device integrations (timing hardware abstractions)
- Persistence (local store and migrations)
- Import / export (Excel/TSV, FIS data, etc.)
- FIS workflows (API/XML)

Timing device integrations MUST be implemented behind abstractions so that
Alge Timy3 and Alge MT1 can be the first concrete drivers, and other devices
can be added later without changes to the timing master or UI.

Live timing MUST be delivered as a small separate application or service,
and MUST support both:

- a cloud-hosted mode for public/live consumption, and
- a local LAN mode for announcers and race-office browser clients.

FIS API/XML workflows MUST be isolated from local race operations so that
local timing and race management remain fully functional when FIS systems
are unavailable.

Rationale: Race-day reliability requires that failures in one concern (e.g.,
a FIS endpoint, a serial device) cannot break unrelated concerns (e.g.,
timing capture or competitor management).

### IV. Offline-First, Cloud-Optional

The application MUST work fully offline for local race management and timing
operations. Cloud components (including hosted live timing, FIS sync, and
any future SaaS features) MUST be optional and MUST NOT be required for
local race-day operation.

Rationale: Ski venues frequently have unreliable connectivity. Loss of
connectivity MUST NOT stop a race.

### V. Tested, Documented, Reviewable Change

Every feature MUST include automated tests covering its core behavior, and
MUST update the documentation it affects. Changes that add behavior without
matching unit tests (or, where tests are genuinely impractical, an explicit
documented justification) MUST NOT be accepted. Architecture documentation
MUST be kept current with every major architectural change.

Rationale: Race-timing software is safety- and credibility-critical; silent
behavior changes are unacceptable.

### VI. Secrets & Credentials Hygiene

Secrets, API keys, FIS member credentials, and tokens MUST NOT be embedded
in the repository, in committed configuration, or in build artifacts. End
users MUST supply credentials via environment variables, local untracked
configuration files, or secure OS-level storage. The repository MUST contain
no sample value that resembles a real credential.

Rationale: FIS and timing-platform credentials belong to individuals and
organizations, not to this project.

### VII. Data Import Fidelity

Import workflows MUST follow these rules:

- Excel / TSV copy-paste import is a first-class workflow and MUST be
  supported from the earliest releases.
- Imports MUST use header-based column mapping.
- Only columns present in the pasted/imported data MUST be imported or
  updated; absent columns MUST NOT overwrite existing values.
- The preferred competitor format uses separate `Last Name` and `First Name`
  columns. `Last Name` MUST always be stored and displayed in UPPERCASE.
- If only a combined `Name` column is provided, best-effort parsing MAY be
  performed, but it MUST be treated as a secondary behavior and MUST be
  shown to the user for review before changes are applied.
- Participation "true" values MUST include English `Yes`, Finnish `Kyllä`,
  and FIS-style `x` (case-insensitive).

Rationale: Race offices live in spreadsheets. Lossy or surprising imports
destroy trust in the software faster than any other defect class.

## Technology & Platform Constraints

- Runtime: .NET 10 LTS.
- First supported desktop platform: Windows.
- The architecture MUST allow Linux portability as a later step; Windows-only
  APIs MUST be isolated behind abstractions.
- Desktop UI framework: Avalonia UI, unless a later, explicitly recorded
  architecture decision supersedes this choice.
- Local persistence: SQLite, unless a later, explicitly recorded architecture
  decision supersedes this choice.
- Live timing is delivered as a separate small application/service supporting
  both cloud-hosted and local-LAN modes (see Principle III).
- The application MUST function without any cloud dependency for local race
  operations (see Principle IV).

Replacement of the Avalonia UI or SQLite defaults MUST be documented as an
architecture decision record (see Governance) before code changes land.

## Development Workflow & Quality Gates

- Each feature ships with: (a) unit tests covering its core behavior,
  (b) updated user/architecture documentation where relevant, and
  (c) explicit error handling for foreseeable race-office failure modes.
- README.md MUST clearly explain what Open Ski Time is and MUST include
  screenshots or labeled screenshot placeholders from the project's earliest
  releases.
- The initial product scope is: Event Series management and competition basic
  data. An Event Series represents a race weekend or race week containing
  multiple competitions and a shared competitor pool, with per-competition
  participation. Users MUST be able to create and edit competition basic data
  and manage all competitors and their per-competition participation from a
  single view.
- The FIS federation / API update action MUST appear in the first feature as
  a clearly labeled UI placeholder; it MUST NOT be wired to any live FIS
  endpoint in the first feature.
- Out of scope for the first feature, and therefore MUST NOT be implemented
  in it: actual timing capture, Alge device integration, start list draw
  rules, second-run ordering rules, FIS XML submission, FIS API
  implementation, referee report generation, penalty calculation, and
  timing report automation. These remain governed by this constitution when
  they are later implemented.

## Governance

This constitution supersedes other conventions in the repository. All pull
requests and reviews MUST verify compliance with the principles above;
non-compliance MUST be either fixed before merge or recorded as an explicit,
time-bounded exception in the PR description.

Amendments to this constitution require:

1. A proposed diff against `.specify/memory/constitution.md`.
2. A Sync Impact Report (see header of this file) describing version delta,
   modified/added/removed principles, and templates or docs requiring
   follow-up.
3. Maintainer approval and, where the change is backward-incompatible, a
   migration note for downstream features already specified or planned.

Versioning policy for this constitution follows semantic versioning:

- MAJOR: Backward-incompatible governance or principle removals / redefinitions.
- MINOR: New principle or section added, or materially expanded guidance.
- PATCH: Clarifications, wording, typo fixes, non-semantic refinements.

Architecture decisions that override defaults named here (e.g., choosing a
different UI framework or persistence engine) MUST be recorded as an
Architecture Decision Record under `docs/adr/` and referenced from this
constitution at the next amendment.

**Version**: 1.0.0 | **Ratified**: 2026-05-28 | **Last Amended**: 2026-05-28
