# Third-Party Notices

OpenSkiTime uses the following third-party software components.

## QuestPDF

Project: QuestPDF\
Purpose: PDF document generation\
License: QuestPDF Community License\
Website: https://www.questpdf.com/

OpenSkiTime is an MIT-licensed open-source project and uses QuestPDF
under the terms of the QuestPDF Community License.

## mikradio

Project: mikradio (AI motorsport radio)\
Purpose: design reference for the optional race radio\
Source: https://github.com/lassiniemisto/mikradio (reviewed at commit 7c0ed0e)\
Author: GitHub user lassiniemisto

The race radio (`src/OpenSkiTime.LiveTiming.Radio`) is a C# reimplementation inspired by mikradio's pipeline: priority event
batching, multi-host dialogue as JSON lines with interruptible markers, a style-only curation pass, a producer agent with a
phase gate, and topic/style memories and an event diary. The dialogue, curation and producer prompts in `RadioPrompts.cs`
adapt the structure and several rules of mikradio's prompts. The repository has no license file; its author permitted the
OpenSkiTime project to use its ideas and code. No voice IDs, team data, articles or names were copied.

## Other dependencies

Additional third-party dependencies are managed through NuGet.
Their respective licenses remain applicable to those components.
