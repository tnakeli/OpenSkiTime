# PDF Factory

Save or open an event series, choose **8 PDF Factory ▾**, select the competition from its menu, then **Generate** and **Open**. The navigation menu and the top active-competition button share the competition selection used by the race desks, with the current competition highlighted. PDF Factory lists both FIS and club competitions and shows all configured runs for the selected competition; entering the view preserves the active timing run. Open launches the actual generated file beside the `.ost` in the operating system's default PDF reader, without copying it to a temporary directory. Hover over the output filename or Open button to see its absolute path. Generation is local and works offline, without Office, an external PDF printer or a cloud service.

Available reports are Event Series Entries and Competition Entries (ungrouped, by nation and by category); Start List and Referee Report for each configured run; Penalty Calculation; Official Results; and Timing Report. Competition entries include only participating competitors. Categories and start order come from existing domain functionality. Event Series Entries add one column per competition of the series, in race-date order and headed by its short label, with `X` for each competition the competitor participates in. Entry lists omit the Bib column when no listed competitor has a bib and the Category column when nobody has a resolved category (all `Unclassified`). Every general report footer shows the software version after the program name, for example `OpenSkiTime 0.1.0`.

Each row displays its filename and status:

- **Not generated:** the physical PDF is missing.
- **Generated:** the PDF hash and saved source version match current data.
- **Outdated:** source data, branding, renderer version or file contents changed. **Open old** keeps the previous PDF accessible; **Regenerate** replaces it after successful rendering.

Hover over a disabled Generate button for its prerequisite. The view reads current committed data whenever opened, when selecting a competition and after generation or print-profile changes. Reopen PDF Factory to check changes made on another desk. Commit competitor grid edits before leaving that desk.

**Generate All** processes available reports in catalog order, skips unavailable reports and continues after individual failures. Its summary names failed reports and gives success, skip and failure counts. Rendering never changes capture, timing decisions or results.

## Availability

Entry lists support empty data. Referee forms can be generated before timing with handwriting areas. Start lists require a saved list. Timing Report requires saved timing-report metadata or a capture session. Its PDF is the FIS "Timing & Data Technical Report Alpine" form, filled from the saved timing-report draft; without a saved draft only the race identity is filled. The XML remains the document transmitted to FIS.

Official Results requires complete current timing matching a saved result approval for every drawn starter group. The approval must also match the current race information, category and calculated/applied penalty. Changes to those values require a new approval; approvals without a saved race-information snapshot cannot generate official PDFs. Official Results prints the saved approved penalty and approval revision/time. Penalty Calculation requires the existing FIS result and penalty engines and reviewed rule tables embedded in the drawn points list. Missing or unreviewed profiles stay unavailable. Existing engines support standard one-run and two-run FIS races. Club official results and final results for more than two runs remain unavailable. Referee forms are still exposed for all configured runs, up to nine. Reporting does not invent unsupported rules.

## Files and print profile

PDFs are stored beside the open `.ost`. Names use `<CompetitionShortName>_YYYY-MM-DD_<ReportName>.pdf`, for example `Levi_FIS_2026-12-12_StartList_Run1.pdf`. Series reports use the series name and start date. Whitespace and Windows-invalid characters become underscores. If two competitions on the same date sanitize to the same name, stable competition IDs disambiguate their output names.

Rendering finishes in a unique temporary file beside the destination. The generator checks the PDF signature and flushes it to disk before replacing the final file. Rendering failure preserves the previous PDF. Same-report generation is serialized, including an exclusive filesystem lease across processes. Copy the race directory to transfer both PDFs and race data. Database backup includes branding and generation metadata but does not copy external PDFs.

Expand **PDF settings** to set the event series' A4 margins and choose an optional background. Defaults are 15 mm top/bottom and 12 mm left/right. Use a one-page, unencrypted A4 PDF of at most 20 MB, leaving space for generated headers, tables and footers. The first template page repeats as an underlay on output pages except Referee Report and Timing Report. Timing Report uses fixed 10 mm top/bottom and 17 mm left/right margins and the FIS form layout without background. Referee Report uses fixed 15 mm top/bottom and 12 mm left/right margins and its own FIS form layout, independent of PDF settings and background header/footer text. Save the profile and regenerate other reports to apply changes.

**Preview** opens a one-page A4 PDF in the default PDF reader with the selected background and a red box at the current margin boundaries. The box includes the area for report headers and footers. Each side displays its current margin in millimetres: `TOP=15` at the top centre, `BOTTOM=15` at the bottom centre, `LEFT=12` at the left midpoint and `RIGHT=12` at the right midpoint for the default profile. Preview uses the values currently entered, including unsaved margin edits, and validates them with the same rules as saving. It does not save the profile, generate reports or change report statuses. Preview files use unique names in the operating system's temporary folder; closing the application attempts to remove them, while files locked by a PDF reader may remain there.

The imported PDF bytes and original filename are embedded in `.ost`, like other portable event assets. No machine-specific path is stored, and deleting the original imported file does not affect the event. If a profile names a template without embedded bytes, generation uses default styling. A corrupt embedded PDF causes a generation failure and preserves the previous output; remove or replace that background to recover. Invalid margins are rejected before persistence.

The referee report follows the supplied preview of the 2022 English Report by the Referee form, with the current official FIS logo and documented blue/yellow colours. Its default form fits one A4 page, with reference competition fields, a DSQ table with nine writing rows, individual DNS/NPS/DNF bib grids and the publication/referee row. The selected run's DSQ details and status bibs are filled from saved timing. All status tables add rows dynamically as required and continue across pages without dropping bibs; large DSQ lists and long notes can also continue. Publication time, deadline and publication date remain blank for manual entry. The saved referee name is printed when available, with space for a manual signature. See [FIS report branding](fis-report-branding.md) for sources, field mapping and verification. This is an OpenSkiTime-generated form, not digital signing or a claim of FIS certification.

## Architecture

`OpenSkiTime.Application/PdfFactory.cs` owns typed descriptors, `ReportCatalog`, deterministic filenames, `EntryReportBuilder` and `PdfReportSourceBuilder`. The source builder consumes `CategoryResolver`, saved start lists, `TimingReplay`, `FisRaceResults` and `FisPenalty`. Typed `RunReportData` and `FinalReportData` carry calculated values to rendering. The builder retries if the series revision changes during loading. Each run's timing read uses the existing SQLite read transaction. Live capture can advance after a snapshot; reopening the view checks output freshness against the new snapshot.

`OpenSkiTime.Reporting` contains QuestPDF document classes, shared header/table/footer components, `QuestPdfRenderer`, `ReportGenerationService`, `ReportStatusService` and the OS-reader boundary. QuestPDF 2026.9.1 is confined to this infrastructure project. Its Community License is explicitly configured for this MIT open-source project. It was chosen for local layout, repeating table headers and native [document operations](https://www.questpdf.com/concepts/document-operations.html), including repeating underlays. Domain and timing projects do not reference QuestPDF.

`PdfFactoryStore` stores the embedded profile and generation receipts. Supported `/4` files lacking this optional extension read defaults without modification. Only an explicit profile save or successful generation creates the extension transactionally. There is no race-schema version change, EF migration or automatic upgrade; incompatible development files are still rejected by the existing store. Receipts contain stable report ID, filename, supplied timestamp, source version and PDF hash. Receipt writes never increment race revision. If replacement succeeds but receipt persistence fails, the PDF remains readable. Output without a matching receipt is shown as Outdated; retry generation to recover.

Source versions reuse the existing monotonic series revision. Competition versions also hash timing fingerprints, report metadata, approval state and branding. Raw capture invalidates competition PDFs even when it does not advance the series revision. Series reports depend on series revision and branding only; selecting another competition does not make them stale. Invalidation is deliberately broad within each scope. `VersionFor` allows more precise dependencies later. Renderer changes must bump the version constants.

The desktop view model obtains rows exclusively from the catalog and follows existing MVVM, navigation and styling. Opening PDF Factory saves pending race-information and timing-report edits first, and stays on the current desk while a timing-report operation is busy. Snapshot loading, rendering, file hashing and receipt persistence run off the UI thread. Rendering holds neither the workspace gate nor timing locks. Logging uses desktop trace output with report IDs and technical stack traces rather than competitor records.

## Adding a report

1. Add a `PdfReportType` and catalog descriptor with scope, filename and prerequisites.
2. Build typed read-only report data using existing application/domain projections.
3. Implement a QuestPDF document in Reporting and register renderer dispatch.
4. Add catalog, prerequisite, generation and data-boundary tests.
5. Document operator behavior. No report-specific PDF Factory UI change is necessary.

Tests are in `PdfFactoryTests` and `PdfFactoryWorkflowTests`; see the [verification record](pdf-factory-verification.md) for tested boundaries and remaining limits. Set `OST_PDF_QA_DIRECTORY` to a scratch directory when running focused tests to retain synthetic PDFs, a portable sample database and a desktop screenshot for visual review. Downloaded FIS lists and real competitors must not become fixtures.
