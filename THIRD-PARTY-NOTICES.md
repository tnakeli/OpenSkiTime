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

## Twemoji Country Flags font

Project: country-flag-emoji-polyfill 0.1.10 by TalkJS (https://github.com/talkjs/country-flag-emoji-polyfill)\
Purpose: draws country flags on the live timing page where the system has no flag emoji (Windows)\
File: `src/OpenSkiTime.LiveTiming.Server/wwwroot/fonts/TwemojiCountryFlags.woff2`
(SHA-256 `9f04f14429bb6a9f415c7a4dd902a918d7e81a4f7526c415496fdb063954e3b8`)\
License: font code MIT (Copyright (c) 2022 TalkJS); flag graphics from Twemoji
(https://github.com/twitter/twemoji), Copyright Twitter, Inc. and other contributors, licensed under
CC-BY 4.0 (https://creativecommons.org/licenses/by/4.0/).

The font file is redistributed unchanged.

## Other dependencies

Additional third-party dependencies are managed through NuGet.
Their respective licenses remain applicable to those components.
