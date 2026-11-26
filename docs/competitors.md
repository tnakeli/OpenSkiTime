# Competitor registration grid

The Competitors tab identifies the view; the content starts directly with registration controls, without a repeated section title. The recovered vertical space is used by the competitor grid.

The view fills the window like Timing and is not part of a scrolling page: the grid takes all the height the other parts leave, so its header and horizontal scroll bar always stay in view and the mouse wheel at the end of the rows does not scroll anything else. The grid's scroll bars are always visible, and Shift with the mouse wheel scrolls it sideways. **Update from FIS**, **Update from regs.fissi.fi** and **Category rules** sit below the grid; one of them is open at a time and together they take at most about half of the height, scrolling on their own. The warnings of the last paste or update are folded into one **Warnings (n)** line above the grid. A row with something to check shows ⚠ next to its change marker; the tooltip and the note below the grid tell what.

Every nation column of the desktop views (competitors, draw, start lists, timing, results, race information, forerunners) shows the country's flag before the three-letter FIS code, as the live timing page does. The flag pictures are embedded (`NationFlags`, Twemoji, see THIRD-PARTY-NOTICES.md), so no download is needed; a code without a flag shows the code alone. The nation entry fields stay plain text.

Select several competitors with Ctrl/Shift or use **Select all** (Ctrl+A). Clicking a race participation checkbox on a selected row applies its new value to all selected competitors and keeps those rows selected for further race changes. The blank new-competitor row is excluded from Select all. Participation edits remain staged until **Save**, and can be reviewed and undone individually in **Undo**.

Drag the boundary between column headers to widen or narrow columns. Identity and points columns have compact initial widths; race columns display the full short name horizontally on two lines, with the race/gender above the date for standard calendar names. Hover over a race header to see its full short name and event name. Code and surname stay visible when scrolling horizontally through many races. Wider series use horizontal scrolling instead of squeezing every column into the window. FIS points columns are shown only for disciplines the series has a competition in (all of them while it has none); the hidden points stay with the competitors.

Sorting and filtering are controlled by two small icons in each column header. Click the sort arrow to toggle ascending/descending order; its direction shows the current sort. Click the funnel to open a small text filter, then choose **Apply** or press Enter. **Cancel** or Escape leaves the filter unchanged. No filter input row or separate filter toolbar is shown.

An active filter fills the funnel and italicizes the column heading. Click that same funnel again to remove the filter immediately. Filters ignore case and combine across columns: `FIN` under NAT and `2007` under YEAR find Finnish competitors born in 2007. Multiple words in one field must all match that column. GENDER matches `Men` and `Women` separately. Participation columns accept `Yes` or `No`; points columns match their displayed values. The blank row always remains available for new registrations. Filter editing uses normal text keyboard navigation and never invokes row deletion or cell-edit shortcuts.

## Default classes

A new event series starts with the age classes of its season, one for girls (N) and one for boys (M): 10, 12, 14, 16, 18 and 21, and the general classes NY and MY. The birth years follow the season's end year and move on by one every season; for 2026/27 U10 is 2017–2019, U12 2015–2016, U14 2013–2014, U16 2011–2012, U18 2009–2010, U21 2006–2008 and the general classes 2005 and earlier. The classes do not overlap, so every competitor has exactly one class. **Default classes (replace)** in the category rules loads them again for the series season; the rules can be edited like any other rule.

## Points

The **POINTS** column holds one points value for every discipline. Type it in the grid, or paste a `Points` column from Excel (blank cells keep the saved value, `~` clears it; the import preview marks changed points). A competitor with a Points value is ordered by it in every discipline; without one the draw uses the FIS points of the race discipline. Update from FIS list keeps typed Points. Points are saved with the competitor in the series file, so every race of a series uses the same values.

## FIS list updates and points

**Update from FIS list** (in **Update from FIS**) updates the name, birth year, gender, nation, club and FIS points of the competitors from the loaded FIS points list by matching FIS Code. Unsaved rows are updated too, for example competitors just read from regs.fissi.fi or pasted, so they can be completed before the first **Save**; a value typed in an unsaved row is replaced by the list's value. Changed cells, points included, are highlighted and kept with **Save**; **Undo** lists them and can undo each one. A competitor whose Code is not on the list gets empty points from that list. Duplicate Codes are skipped.

FIS points are saved with the event series together with the list they came from (code, validity and penalty rules), so the series does not depend on the computer's downloaded list. Downloading a newer list changes nothing until **Update from FIS list** and **Save**. Changing a competitor's Code takes that athlete's points from the loaded list as a staged change; without a loaded list (or when the Code is cleared) the previous points are cleared instead. The event file enforces the same rule on every save path, imports included: a Code change saved without new points clears the saved points, so they never stay with another athlete. Adding competitors from the FIS search saves their points with them. The Run 1 draw uses the saved points; it needs one saved list for all entered competitors and states which list it uses, with a warning when it is not valid on the competition date.

## Update from regs.fissi.fi

**Update from regs.fissi.fi** (the section below **Update from FIS**) reads the registered athletes of every competition of the open series from the public API of [regs.fissi.fi](https://regs.fissi.fi) and stages the differences in the grid. It needs the API key saved in **Settings → API keys**. Nothing is saved before **Save**; **Undo** lists every staged change and can undo each one. Pending edits are saved or discarded first, because the registrations are compared with the saved competitors.

A competition is found by its codex. The season of the registrations must be the season of the competition (FIS uses the same codexes every season), and gender and discipline must agree where both sides tell them. The API is asked for the race of the competition date first and, when it knows none, for the newest race of the codex, so a race moved to another day is found too. The section lists each competition with the date on the site, the registered athletes, the new competitors, the entries added and removed and a status:

| Status | Meaning |
|---|---|
| Review and save / Up to date | The registrations were read; with or without entry changes. Changed details are highlighted in the grid |
| Date differs from the competition, check the race | Read, but the date on the site is another day of the same season |
| None registered | The race has no registrations (for example not open yet). No entry is removed |
| No codex / Not found | The competition has no numeric codex, or the site has no such race in this season |
| Gender or discipline differs | The codex belongs to another kind of race; correct the competition |
| Several matches | More than one race, or more than one competition, has the codex |
| Start list saved, not updated | A start list exists; see below |

Athletes are matched by FIS code, otherwise by name and birth year.

- A new athlete is added with the code, name, birth year and nation of the site, the gender of the race and the entries of all read competitions.
- An existing competitor's name, birth year and nation are replaced by the site's values; values the site does not have stay. The API has no club: saved clubs stay, and new competitors get theirs with **Update from FIS list** after **Save**. The same name in other capitals or order is kept. The gender is filled in only when it is missing.
- An entry of a competitor who is not registered any more is removed. This includes competitors added by hand, so review the removals before **Save**. Nobody whose code or name is on the registration list of the race is removed; an unclear one is named in a warning.
- A match by name whose Code differs needs **Match checked** before **Save**. When **Save** stops at such a row, the row is selected and brought into view (a column filter that hides it is cleared), and the status bar names the competitor, the reason and what to do; **Match checked** is in the note below the grid. A row marked for deletion needs no confirmation.
- A new or changed name written entirely in capitals (the surname cannot be told apart, unless the loaded FIS list has the athlete) gets a note to check the name. The note does not block **Save**: download the FIS list and use **Update from FIS list**, before or after **Save**, to take the name from the list.
- An uncertain match (several competitors, registered twice, men's and women's races, the same name with a birth year missing, neither a code nor a birth year) and invalid values are left out with a warning, and nobody involved loses an entry. An entry of the other gender is not added.
- The site's points are not used. New competitors get their points from the loaded FIS list; use **Update from FIS list** and **Save** for the others.

A competition with a saved start list is not updated and competitors on any saved start list keep their details, because a later run requires the entries and details of Run 1.

All races are read and checked before anything is staged: a response that is not understood completely stops the update without changes, because a missing athlete would otherwise be proposed for removal. If the competitors or competitions change or another view is opened during the download, nothing is staged. Reading registrations is optional and does not affect timing.

### API

`GET https://regs.fissi.fi/public/api/race_athletes.php?codex=0507&date=2026-11-28` with the key in the `X-API-Key` header, never in the address. Redirects are not followed, so the key is sent to regs.fissi.fi only. The response has `race` (codex, name, date, location, discipline, category, gender), `points_list`, `count` and `athletes` (`fis_code`, `name`, `birth_year`, `nation`, `points`); codex, date, discipline and gender of the race and the athletes' fields except points are used. Every used field must be present and `count` must be the number of athletes; a null or blank value means not known. 401 and 403 (key missing or rejected), 429 (request limit, with the time of `Retry-After`) and other errors stop the update; a 404 with the API's error code `race_not_found` is an unknown race. Nothing is retried automatically. Hidden races are not returned by the API.

Contract: the operator's instructions of 2026-11-24. Checked with a key on that day: five races (264 registrations in all; one race empty, one with another date) gave exactly the athletes of the public registration pages (code, name, birth year and nation), and a race of the other gender, an unknown codex and a wrong date were recognized. The service states a limit of 600 requests (`X-RateLimit-Limit`).

## Gender of competitors and competitions

Gender appears as **Men** and **Women** for competitors and as **Men**, **Women** or **Mixed** for competitions everywhere in the application. The series file and FIS messages keep the FIS calendar codes M, W and A.

A competitor cannot be entered in a competition of the other gender: a men's competition (calendar gender M) takes only men and a women's competition (W) only women. Ticking such an entry is refused at once and the status bar names the competitor and competition. A pasted entry of the other gender is not staged; the import warnings name it. Saving checks the same rule on every write path (grid, paste/import and single-row saves) before anything is written, and nothing is saved when it fails. Only entries added in that save and competitors whose gender changes are checked, so an existing wrong entry in an older file never blocks unrelated work; removing it is always allowed. Mixed competitions, competitions without a gender and competitors whose gender is not known yet are not restricted.

Filtering/sorting commits the current cell into the staged draft, retains still-visible selected rows and preserves all unsaved changes; it does not save to the database or discard hidden drafts. Cell edits keep their current position until the next deliberate filter/sort operation. The former global search, gender dropdown, race-column search, FIS-points visibility switch and surname/category grouping dropdown and their separate behavior have been removed. All race columns and the points columns of the series' disciplines remain available through horizontal scrolling; category ordering is selected by sorting CATEGORY.

The context menu contains Select all, Paste from Excel, Copy selected and Delete selected rows. The previous **Undo changes to selected row** action and Ctrl+R shortcut have been removed; **Undo** still supports undoing individual staged changes and discarding all staged changes.

## FIS athlete category availability

Reviewed on 2026-10-02 against the [public FIS OpenAPI document](https://api.fis-ski.com/public-docs?public-api-docs.json), the current `/data-feeds/competitors` export header and four authenticated alpine athlete lookups. The general athlete response documents a nullable `categoryCode` alongside `classCode` and `className`; it does not document this as an alpine age-group classification. All four reviewed AL responses returned `categoryCode: null`. The alpine competitor export header has no category field, and the points-list competitor format used by the application has no category field either. No athlete payload, identifier or credential is retained in this documentation or test fixtures.

The registration grid's **Category** is currently resolved from the series' category rules using birth year and gender. It must not be populated from competition calendar categories such as FIS/NJR, athlete status, association, or a guessed interpretation of the API's nullable category field. No competitor data was changed by this availability review because the checked source data supplied no athlete category value.
