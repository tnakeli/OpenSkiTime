# Feature Specification: Event Series Management

**Feature Branch**: `001-event-series-management`

**Created**: 2026-05-28

**Status**: Draft

**Input**: User description: "Create feature specification 001-event-series-management for Open Ski Time. Manage a race weekend/week as an Event Series containing multiple competitions, a shared competitor pool, and per-competition participation. Modern, compact, SaaS-style UI; English; offline-capable; FIS API only as a placeholder."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Set up an Event Series and its competitions (Priority: P1)

A race secretary opens Open Ski Time at the start of a race weekend. They create
a new Event Series for the weekend (e.g., "Levi Spring Cup, FIN, 2026"), enter
its basic data (location/resort, organizer, start date, end date, nation,
season), and add the individual competitions that will be run during the
weekend (e.g., SL on day 1, GS1 + GS2 on day 2). Each competition gets its own
basic data (date, discipline, race type, codes, course data, number of runs,
number of intermediate times). The user does this without consulting external
documentation.

**Why this priority**: Nothing else in the product can be used until an Event
Series and at least one competition exist. This is the entry point of every
race weekend.

**Independent Test**: Can be fully tested by creating an Event Series with
required fields, adding two competitions of different disciplines, saving, and
re-opening the application offline to confirm both are persisted and editable.
Delivers the value of "I can describe my race weekend in the system."

**Acceptance Scenarios**:

1. **Given** the user has Open Ski Time open with no Event Series, **When**
   they create a new Event Series with name, location, organizer, start date,
   end date, nation, and season, **Then** the Event Series is saved and shown
   in an Event Series overview.
2. **Given** an existing Event Series, **When** the user adds a competition
   with name, date, discipline, race type, number of runs, and number of
   intermediate times, **Then** the competition appears in the Event Series
   overview's competitions list and can be edited.
3. **Given** an Event Series with one competition, **When** the user closes
   and reopens the application without network access, **Then** the Event
   Series and its competition are still present and editable.
4. **Given** the user is creating a competition of race type "FIS", **When**
   they leave the FIS code blank and try to save, **Then** the system blocks
   the save and explains that a FIS code is required for FIS races.
5. **Given** the user is creating a competition of race type "Club" or
   "Training", **When** they leave the FIS code blank, **Then** the save
   succeeds.

---

### User Story 2 - Manage all competitors and their per-competition participation in one place (Priority: P1)

The race secretary needs to enter every competitor of the weekend once, not
once per race. They open the competitor management grid, paste competitors
from an Excel file (with separate "Last Name" and "First Name" columns plus
points and per-competition participation columns like `3.1 SL`, `4.1 GS1`),
review the import preview, and apply. They then sort and filter competitors
by category, nation, club, missing data, or by which competitions a given
competitor is participating in. They can also switch the grid into a grouped
view (by category, by club, or by nation) without changing the underlying
data.

**Why this priority**: A race weekend has one competitor pool. Forcing the
secretary to re-enter competitors per race is exactly the SkiAlp Pro pain
this product is meant to remove. Without this story, the product offers no
real-world advantage.

**Independent Test**: Can be fully tested by creating an Event Series with
three competitions, pasting a TSV containing 50 competitors and three
participation columns, confirming the preview, and verifying that filtering
by participation in one specific race shows only the expected competitors.

**Acceptance Scenarios**:

1. **Given** an Event Series with at least one competition, **When** the user
   pastes TSV data with headers `Code, Last Name, First Name, Year, Gender,
   Nation, Club, Slalom, Giant, 3.1 SL`, **Then** the import preview shows
   new competitors, updated competitors, unchanged rows, errors, and unknown
   columns separately, before any change is applied.
2. **Given** an import preview, **When** the user confirms the import,
   **Then** every imported last name is stored and displayed in UPPERCASE,
   regardless of the casing in the source data.
3. **Given** existing competitors with non-empty `Club` values, **When** the
   user imports a TSV that contains a `Club` column with empty cells,
   **Then** the existing `Club` values are not overwritten, unless the user
   explicitly selects an "overwrite with empty" import mode.
4. **Given** an import where a row's participation column `3.1 SL` contains
   `Yes`, `Kyllä`, `x`, `YES`, `kyllä`, or `X`, **When** the import is
   applied, **Then** the competitor is recorded as participating in the
   `3.1 SL` competition.
5. **Given** the competitor grid for an Event Series, **When** the user
   switches the view mode to "Group by club", **Then** competitors are
   visually grouped by club without any change to the stored data, and the
   user can switch back to a flat view without losing edits.
6. **Given** the competitor grid, **When** the user filters by "Participates
   in 11.4 SL", **Then** only competitors whose `11.4 SL` participation is
   true are shown.

---

### User Story 3 - Import competitors when only a single "Name" column is available (Priority: P2)

Some federations and clubs distribute lists with a single combined `Name`
column. The race secretary pastes such a list. The system parses each
combined name into last name + first name using a best-effort strategy and
shows every parsed row in the import preview marked as "uncertain name
parse" so the user can review and correct before applying.

**Why this priority**: This is a real-world fallback, not the preferred path.
The preferred separate-column flow (Story 2) must be encouraged in the UI;
single-column parsing must work but must never silently overwrite anything.

**Independent Test**: Can be fully tested by pasting a TSV with only `Name,
Year, Gender, Nation` columns, confirming every row is flagged as uncertain
in the preview, correcting one parse manually, then applying.

**Acceptance Scenarios**:

1. **Given** a paste containing a single `Name` column, **When** the system
   builds the import preview, **Then** every row is marked as "uncertain
   name parse" and shows the proposed last name (uppercase) and first name.
2. **Given** an uncertain-name row in the preview, **When** the user edits
   the proposed split, **Then** the user's edit is what gets applied.
3. **Given** an uncertain-name row, **When** the user applies the import
   without editing, **Then** the best-effort split is applied and the last
   name is stored uppercase.

---

### User Story 4 - Edit competition basic data with a placeholder for FIS update (Priority: P2)

The race secretary opens a competition and edits its basic data (course
name, altitudes, vertical drop, homologation number, number of runs, number
of intermediate times, gender/category, etc.). They see a clearly labeled
button "Update from FIS API" that is visually present but explicitly marked
as not yet available, so they understand the eventual workflow without
expecting it to function.

**Why this priority**: Race-day course/homologation edits are routine. The
FIS placeholder anchors a future workflow without misleading users today.

**Independent Test**: Open a saved competition, change its course name and
homologation number, save, reopen — values persist. Click the FIS update
button — the system clearly states the action is not yet implemented and
makes no network calls.

**Acceptance Scenarios**:

1. **Given** an existing competition, **When** the user edits any of its
   basic data fields and saves, **Then** the changes persist across
   application restarts.
2. **Given** the competition editor, **When** the user clicks "Update from
   FIS API", **Then** the system shows a visible "not yet available" message
   and performs no network requests.

---

### User Story 5 - Validate readiness of competitors and competitions (Priority: P3)

Before race day, the secretary wants a quick read on what is missing. The
Event Series overview shows a competitor count and a validation status
(e.g., "12 competitors with missing required data", "2 competitions missing
homologation number"). The competitor grid offers a filter "Missing required
data" that surfaces those rows.

**Why this priority**: Useful, but the secretary can survive the first
release by inspecting the grid manually. It is the smallest of the in-scope
stories.

**Independent Test**: Create an Event Series with one valid competitor and
one competitor missing `Year`. The overview shows "1 competitor with missing
required data"; filtering by "Missing required data" shows exactly that
competitor.

**Acceptance Scenarios**:

1. **Given** an Event Series with N competitors of which K have at least
   one missing required field, **When** the user opens the Event Series
   overview, **Then** the overview reports "K competitors with missing
   required data".
2. **Given** the competitor grid, **When** the user applies the "Missing
   required data" filter, **Then** only the K competitors above are shown.
3. **Given** a FIS race with no FIS code, **When** the user opens the Event
   Series overview, **Then** the competition is reported as having missing
   required data.

---

### Edge Cases

- **Empty paste**: Pasting an empty clipboard produces no preview and an
  explicit "nothing to import" message; nothing is changed.
- **Header-only paste**: A paste with header row only produces a preview
  showing 0 new / 0 updated and lists which headers were recognized vs.
  unknown.
- **Duplicate competitor codes within a paste**: Flagged as errors in the
  preview; the conflicting rows are not applied.
- **Existing competitor matched by code**: Treated as an update; only the
  columns present in the paste are considered.
- **Existing competitor matched by name + year + gender (no code)**: Treated
  as an update with a "matched without code" indicator in the preview.
- **Mixed-case last names in source**: All become uppercase on storage and
  display; original casing is not preserved in the first version.
- **Whitespace in pasted cells**: Trimmed on both sides before matching and
  storage.
- **Participation column header that does not match any competition in the
  Event Series**: Listed under "unknown columns" in the preview; ignored on
  apply.
- **Participation cell with a non-recognized truthy string** (e.g., `1`,
  `true`, `kyllä!`): Treated as not-true and shown in the preview as a
  warning so the user can decide.
- **Two competitions with the same date and discipline**: Allowed; the
  participation column header must include the column label as-is (e.g.,
  `4.1 GS1` and `6.1 GS2` are distinct).
- **Computer's local timezone changes between sessions** (e.g., user travels
  with laptop): Dates are stored as calendar dates without time component
  for the basic data fields specified, so this does not corrupt scheduling.
- **Reopening offline**: All Event Series, competitions, competitors, and
  participation are available without any network connection.
- **Year of birth outside plausible alpine racing range** (e.g., < 1900 or in
  the future): Flagged as an error; row not applied.
- **Gender value outside the configured set**: Flagged as an error; row not
  applied.
- **FIS code on a non-FIS race**: Allowed and stored, but ignored for
  validation.

## Requirements *(mandatory)*

### Functional Requirements

#### Event Series

- **FR-001**: The system MUST allow users to create, view, edit, and delete
  Event Series locally.
- **FR-002**: An Event Series MUST capture: name, location/resort,
  organizer, start date, end date, nation, season.
- **FR-003**: The system MUST NOT expose a timezone field on Event Series
  or competition basic data; it MUST use the computer's local timezone for
  any date display logic.
- **FR-004**: The system MUST allow users to add, edit, and remove
  competitions within an Event Series.

#### Competition basic data

- **FR-010**: A competition MUST capture: name, date, discipline, race type
  (FIS, National, Club, Training), FIS code, local race code,
  gender/category (when applicable), hill/course name, start altitude,
  finish altitude, vertical drop, homologation number, number of runs, and
  number of intermediate times.
- **FR-011**: The competition editor MUST display an "Update from FIS API"
  action as a clearly labeled placeholder that is not wired to any network
  call in this feature.
- **FR-012**: The system MUST require, to save a competition: name, date,
  discipline, race type, number of runs, number of intermediate times.
- **FR-013**: The system MUST require a non-empty FIS code to save a
  competition whose race type is FIS.
- **FR-014**: The system MUST allow homologation data to be missing in this
  feature, but MUST surface the absence in validation summaries.

#### Competitors

- **FR-020**: A competitor MUST capture: last name, first name, code,
  gender, year of birth, nation, club, association, downhill points,
  super-g points, giant slalom points, slalom points, super combined points.
- **FR-021**: The system MUST require last name, first name, code, gender,
  year of birth, and nation for a competitor to be considered usable for a
  race entry.
- **FR-022**: Club MAY be empty (e.g., FIS Masters style imports).
- **FR-023**: The system MUST normalize last name to uppercase on every
  import and on every manual edit, and MUST display last name in uppercase
  everywhere.
- **FR-024**: The system MAY discard the original mixed-case form of last
  name in the first version.
- **FR-025**: The system MUST derive a competitor's category automatically
  from year of birth and gender, using a configurable rule model. Exact FIS
  / national category rule details are out of scope for this feature.

#### Participation

- **FR-030**: A competitor MAY participate in zero, one, or many
  competitions of the Event Series.
- **FR-031**: The competitor management grid MUST present participation as
  one column per competition, using each competition's short label (e.g.,
  `3.1 SL`, `4.1 GS1`, `9.4 GS1`, `11.4 SL`).
- **FR-032**: The system MUST accept the following case-insensitive values
  as participation = true: `Yes`, `Kyllä`, `x`.
- **FR-033**: The system MUST treat empty participation cells as "not
  participating or unknown" and MUST NOT overwrite existing participation
  with empty values unless the user has explicitly chosen an
  overwrite-with-empty import mode.

#### Competitor management grid

- **FR-040**: The system MUST provide one shared competitor grid per Event
  Series listing every competitor of that Event Series.
- **FR-041**: The grid MUST support fast inline editing of competitor
  fields and participation cells.
- **FR-042**: The grid MUST support Excel/TSV copy-paste both into and out
  of the grid.
- **FR-043**: The grid MUST support sorting and filtering by category,
  nation, club, race participation, and "missing required data".
- **FR-044**: The grid MUST support grouped display by category, by club,
  and by nation as a view-mode only; grouping MUST NOT change underlying
  storage or model.

#### Import (Excel / TSV paste)

- **FR-050**: The system MUST accept pasted Excel or TSV data with a
  header row and use header-based column mapping.
- **FR-051**: The system MUST recognize at least these headers
  (case-insensitive, trimmed): `Code`, `Last Name`, `First Name`, `Name`,
  `Year`, `Gender`, `Nation`, `Club`, `Category`, `Slalom`, `Giant`,
  `Downhill`, `Super-G`, `Super Combined`, `Association`, plus per-
  competition participation labels (e.g., `3.1 SL`, `4.1 GS1`, `9.4 GS1`,
  `11.4 SL`).
- **FR-052**: The system MUST import only the columns present in the
  pasted data; absent columns MUST NOT overwrite existing values by
  default.
- **FR-053**: The system MUST offer an explicit "overwrite with empty"
  import mode that, when selected, allows empty cells in present columns to
  clear existing values.
- **FR-054**: When both `Last Name` and `First Name` columns are present,
  the system MUST use them directly.
- **FR-055**: When only a `Name` column is present, the system MUST parse
  it into last name and first name using a best-effort strategy and MUST
  flag every such row in the import preview as "uncertain name parse".
- **FR-056**: The system MUST always show an import preview before any
  change is applied, listing: new competitors, updated competitors,
  unchanged rows, rows with errors, rows with uncertain name parsing, and
  unknown columns.
- **FR-057**: The system MUST allow the user to cancel from the import
  preview without changing any stored data.
- **FR-058**: The UI MUST encourage the use of separate `Last Name` and
  `First Name` columns over a combined `Name` column (e.g., via inline
  guidance in the import dialog).

#### Persistence and offline

- **FR-060**: The system MUST persist Event Series, competitions,
  competitors, and participation locally on the user's machine.
- **FR-061**: All workflows in this feature MUST function with no network
  connection.
- **FR-062**: Cloud and FIS-API features MUST NOT be required by any
  workflow in this feature.

#### Validation summaries

- **FR-070**: The Event Series overview MUST display the total competitor
  count and a count of competitors with at least one missing required field.
- **FR-071**: The Event Series overview MUST list competitions that are
  missing required data (including missing FIS code on FIS races).
- **FR-072**: The competitor grid MUST offer a "Missing required data"
  filter.

#### UX constraints

- **FR-080**: The UI for this feature MUST be in English.
- **FR-081**: The UI MUST be operable for the primary flows (create Event
  Series, add competition, paste competitors, review import preview, edit
  participation) without requiring the user to read external documentation.
- **FR-082**: Errors MUST be explicit, recoverable, and explained in plain
  language; no silent failures.
- **FR-083**: The UI MUST NOT replicate the SkiAlp Pro user experience; it
  MUST follow a modern, compact, SaaS-style layout.

### Key Entities

- **Event Series**: A race weekend / week. Holds basic descriptive data
  (name, location, organizer, start/end dates, nation, season). Owns a set
  of competitions and a shared pool of competitors.
- **Competition**: A single race within an Event Series. Holds race-
  specific data (date, discipline, race type, codes, course/altitude data,
  homologation, runs, intermediate times). Knows nothing about competitors
  directly except via Participation.
- **Competitor**: A person registered in the Event Series. Holds personal
  data (uppercase last name, first name, code, gender, year of birth,
  nation, club, association) and per-discipline points (DH, SG, GS, SL,
  SC). Belongs to exactly one Event Series.
- **Participation**: Boolean-style relationship between a competitor and a
  competition in the same Event Series. Shown as one column per competition
  in the competitor grid.
- **Category** (derived): A label computed from a competitor's year of
  birth and gender via a configurable rule model. Not stored as primary
  data in the first version.
- **Import Preview** (transient): A staged view of pasted data showing new
  competitors, updated competitors, unchanged rows, error rows, uncertain
  name-parse rows, and unknown columns. Discarded when the user cancels;
  applied when the user confirms.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A race secretary can create a new Event Series, add three
  competitions, and paste 100 competitors with participation columns in
  under 10 minutes, without consulting external documentation.
- **SC-002**: After confirming an import of 100 competitors, the entire
  preview-to-apply step completes in under 3 seconds on a typical race-
  office laptop.
- **SC-003**: 100% of last names shown anywhere in the UI after import are
  uppercase, regardless of the casing in the source data.
- **SC-004**: 0% of imports with absent columns silently overwrite existing
  values; this MUST be verifiable from automated tests of the importer.
- **SC-005**: All primary workflows in this feature succeed end-to-end with
  the network disconnected.
- **SC-006**: When pasting a list with `Yes`, `Kyllä`, `x`, `YES`, `kyllä`,
  and `X` mixed across participation cells, 100% of those values are
  recognized as participating.
- **SC-007**: When the user clicks "Update from FIS API" in this feature,
  the system performs zero outgoing network requests.
- **SC-008**: A user filtering the grid by participation in one specific
  competition sees a result count equal to the count of competitors whose
  participation cell for that competition is true.
- **SC-009**: At least 95% of competitor edits are visible in the grid
  within 200 ms of the keypress that committed them, on a typical race-
  office laptop.

## Assumptions

- The primary user is a race secretary, timing team member, or organizer
  working in a race office; not the public.
- The user has a Windows laptop available; Linux portability is a longer-
  term architectural concern and is not exercised by this feature.
- Local persistence is provided by the application; the spec is agnostic
  about the storage engine, but the chosen engine must support fully
  offline operation. (See architecture decision records for the concrete
  technology choice.)
- Category rules in this feature are intentionally simple and configurable;
  exact FIS and national category models are deferred.
- Single-column `Name` parsing is best-effort and treated as secondary;
  users are expected to prefer separate `Last Name` / `First Name` columns.
- "Number of intermediate times" refers to per-run intermediate timing
  points and applies uniformly across runs of the same competition for
  this feature.
- Original mixed-case last name forms are not retained in this feature.

## Dependencies

- A local writable user data directory on the host operating system.
- The host's clock and timezone settings (used as-is; not configurable from
  within Open Ski Time).

## Out of Scope

The following are explicitly NOT delivered by this feature and MUST NOT be
implemented as part of it:

- Real timing capture (start/finish/intermediate signals).
- Alge Timy3 device integration.
- Alge MT1 device integration.
- FIS API implementation (the placeholder button is in scope; the call is
  not).
- FIS XML export / submission.
- FIS referee report generation.
- FIS penalty calculation.
- Start list draw rules.
- Second-run order rules.
- Live timing implementation (cloud or LAN).
- Localization beyond English.
- Multi-user / multi-machine synchronization.
- Authentication and user accounts.
