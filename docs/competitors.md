# Competitor registration grid

The Competitors tab identifies the view; the content starts directly with registration controls, without a repeated section title. The recovered vertical space is used by the competitor grid.

Select several competitors with Ctrl/Shift or use **Select all** (Ctrl+A). Clicking a race participation checkbox on a selected row applies its new value to all selected competitors and keeps those rows selected for further race changes. The blank new-competitor row is excluded from Select all. Participation edits remain staged until **Save changes**, and can be reviewed individually in **Review changes**.

Drag the boundary between column headers to widen or narrow columns. Identity and points columns have compact initial widths; race columns display the full short name horizontally on two lines, with the race/gender above the date for standard calendar names. Hover over a race header to see its full short name and event name. Code and surname stay visible when scrolling horizontally through many races. Wider series use horizontal scrolling instead of squeezing every column into the window.

Sorting and filtering are controlled by two small icons in each column header. Click the sort arrow to toggle ascending/descending order; its direction shows the current sort. Click the funnel to open a small text filter, then choose **Apply** or press Enter. **Cancel** or Escape leaves the filter unchanged. No filter input row or separate filter toolbar is shown.

An active filter fills the funnel and italicizes the column heading. Click that same funnel again to remove the filter immediately. Filters ignore case and combine across columns: `FIN` under NAT and `2007` under YEAR find Finnish competitors born in 2007. Multiple words in one field must all match that column. GENDER matches `Men` and `Women` separately. Participation columns accept `Yes` or `No`; points columns match their displayed values. The blank row always remains available for new registrations. Filter editing uses normal text keyboard navigation and never invokes row deletion or cell-edit shortcuts.

Filtering/sorting commits the current cell into the staged draft, retains still-visible selected rows and preserves all unsaved changes; it does not save to the database or discard hidden drafts. Cell edits keep their current position until the next deliberate filter/sort operation. The former global search, gender dropdown, race-column search, FIS-points visibility switch and surname/category grouping dropdown and their separate behavior have been removed. All race and points columns remain available through horizontal scrolling; category ordering is selected by sorting CATEGORY.

The context menu contains Select all, Paste from Excel, Copy selected and Delete selected rows. The previous **Undo changes to selected row** action and Ctrl+R shortcut have been removed; **Review changes** still supports undoing individual staged changes and discarding all staged changes.

## FIS athlete category availability

Reviewed on 2026-10-02 against the [public FIS OpenAPI document](https://api.fis-ski.com/public-docs?public-api-docs.json), the current `/data-feeds/competitors` export header and four authenticated alpine athlete lookups. The general athlete response documents a nullable `categoryCode` alongside `classCode` and `className`; it does not document this as an alpine age-group classification. All four reviewed AL responses returned `categoryCode: null`. The alpine competitor export header has no category field, and the points-list competitor format used by the application has no category field either. No athlete payload, identifier or credential is retained in this documentation or test fixtures.

The registration grid's **Category** is currently resolved from the series' category rules using birth year and gender. It must not be populated from competition calendar categories such as FIS/NJR, athlete status, association, or a guessed interpretation of the API's nullable category field. No competitor data was changed by this availability review because the checked source data supplied no athlete category value.
