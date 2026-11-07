# Start orders for Club races

A Club (non-FIS) competition can hold several classes, men and women together, and any number of runs (1–99, for example a training race of 20 runs). FIS competitions are unchanged: they use the FIS draw described in the [draw fairness guide](fis-draw-fairness.md).

## Classes and blocks

- A **class** is a category label from the series' category rules (Competitors → category rules; a new series starts with the [default classes](competitors.md#default-classes) of its season), resolved from birth year and gender when the list is drawn, for example `N10`, `M10`, `W10`. The program never interprets the label. If a starter has no class or more than one, the draw is blocked and names the competitors. Without any category rules everybody is in one class, `All`.
- A **block** is one or more classes drawn and listed together. By default every class is its own block; **Merge with next** draws two blocks as one group and **Split** separates them again. Ranking and results stay per class.
- The start list of a Club race holds every class, men and women together. (Lists of one gender can be saved through the file format, but the application offers one list per run.)

## Setup grid in the Draw view

One row per block, in start order (**Up** / **Down** move the selected block). **Same for all** copies the options of the selected block to every block.

**First run**

| Option | Meaning |
| --- | --- |
| First run | **Draw all**: everyone in the block is drawn as one group. **By points**: the best *First group* competitors with points are drawn among themselves, equal points at the boundary join them; the others follow in points order, equal points drawn among themselves; competitors without points are drawn last. Without any points the block is simply drawn. |
| Better points | **Lower** (FIS style) or **Higher**. |
| First group | Size of the best group (1–1000). |
| 0 = no points | Treat a points value of 0 as no points. |
| First bib / Bibs | The **Bibs** column shows the range of every block, worked out from the order, the number of starters, the global **First bib** and **Skipped bibs** of the draw settings. **First bib** shows the calculated start as a hint; type a number (for example 11 after a block of 1–7) to start that block there. The blocks after it follow, and a number that overlaps the block before is marked. |

**Later runs** (every run after the first, from the run before it)

| Option | Meaning |
| --- | --- |
| Order by | **Reverse start order**: the previous run's final order reversed; no results are needed, so everyone including DNF/DSQ starts at the place of their number. **Reverse results**: the best *Reverse first* classified competitors start in reverse result order, the rest in result order (equal times at the boundary join the reversed group); needs results from Timing or typed in the Run input. **Same order**: repeats the previous order. |
| Reverse first | A number (default 30) or `All`. |
| DNF / DSQ | With *Reverse results*: **Start last** (default) or **Do not start**. |

A competitor who did not start the previous run (DNS, NPS) is left out whenever results are known; without results everyone stays on the list and DNS is handled in Timing.

**Draw block again** draws only the selected block with a new seed; the other blocks keep their order. Moves and bib assignment work as before and are recorded as revisions with operator and reason; redrawing a block clears them.

Seeds, the setup, every starter's class and the points list used are saved with the list, so a start list can be replayed and verified like a FIS draw. Every block has its own seed (128 random bits) and uses the same double draw as the FIS draw. Saving recalculates the list from the recorded inputs and rejects any difference.

## Points

The competitor grid has one **POINTS** column that applies to every discipline (see [competitors](competitors.md#points)). A competitor with a Points value is ordered by it; otherwise the FIS points of the race discipline are used. Points are an input to the draw only; OpenSkiTime calculates no points or standings.

## Output

Start list TSV, the print view and the PDF show each starter's class; the PDF has one heading per block. The totals column in Timing is shown for two-run races only; in a race with more runs each run is ranked on its own.

## Limits

- A Club race that already holds lists drawn with the FIS draw keeps the FIS layout; its flexible lists start from a new competition.
- A competition with a flexible list cannot be changed to a FIS competition.
- The Results tab, penalty and FIS XML are FIS only; class results are not implemented.

## Timing and live results

In Timing, the ranking lists classes under headings with the **active class first**: the class of the latest finisher, else of the competitor on course. The other classes follow in their category order. The live page lists its results the same way, with ranks per class (see [live timing](live-timing.md#classes)).
