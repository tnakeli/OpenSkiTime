# OpenSkiTime start order draw and verification

**An explanation for competitors, coaches and race officials**

Reviewed on 2 October 2026. Algorithm version `SHA256-double-draw-v1`. The source code reference is commit `468fa52a8b2cf49efa2ba0eec3a632553015c186`.

OpenSkiTime's first-run draw combines a cryptographically generated random seed with rule-based grouping of competitors. Within a drawn group, the algorithm assigns no additional weight to an athlete's name, club or nation. Every competitor receives exactly one position. The seed, input data, rule version and resulting order are saved in the event series file, allowing the same draw to be reproduced and checked against the source code.

Fairness concerns **the order within the same draw group**. Grouping by FIS points deliberately affects the start order. Randomness also does not promise everyone a favourable bib in an individual race. These distinctions matter when assessing a claim that the software favours a particular competitor.

## What is drawn

This explanation covers the current profile based on FIS rules: two-run slalom and giant slalom, and single-run downhill and super-G. It does not describe every special rule for local, youth, Cup or Masters competitions. The selected profile and settings must be checked for the particular race.

For Run 1, the software uses competitors entered in the race and their discipline-specific points from the loaded FIS points list. The list must be valid on the race date. Competitor IDs and FIS codes must be unique; missing required athlete information prevents the draw.

| Competitor group | How the start order is formed |
| --- | --- |
| First group | The competitors with the best points are drawn among themselves. The default size is 15; the setting allows 1–15. Equal points at the boundary are included together, so the group can exceed the configured size. |
| Other competitors with points | Start in ascending points order. Competitors with equal points are drawn among themselves. |
| Competitors without available discipline points | Are drawn among themselves after the competitors with points. |

For example, outside the first group, a competitor with 20 points starts before one with 40 points. This follows from points ordering. If two competitors have equal points, their relative order is drawn.

Run 2 uses classified Run 1 results and the selected reversal of 15 or 30 competitors. Ties at the reversal boundary are included together. Equal times are ordered using bib numbers in the sequence defined by the code. **Run 2 does not use a new random draw.**

## Where the randomness comes from

For every Run 1 draw attempt, the user interface generates a new 16-byte, **128-bit** seed:

```csharp
Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
```

Microsoft describes `RandomNumberGenerator.GetBytes` as a source of cryptographically strong random bytes in the [.NET 10 documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator.getbytes?view=net-10.0). The [.NET 10 Windows implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/RandomNumberGeneratorImplementation.Windows.cs) calls Windows `BCryptGenRandom` with the system-preferred random number generator.

A 128-bit seed has 2^128 possible values, approximately 3.4 × 10^38. It is not derived from the clock, athlete information or a club name. The ordinary draw interface does not let the operator enter the seed. Open-source software can, of course, be modified; this explanation describes the referenced implementation.

The draw converts the seed into a reproducible sequence using SHA-256. Successive inputs are `seed:0`, `seed:1`, `seed:2` and so on. The first four bytes of each digest are interpreted as a little-endian, unsigned 32-bit integer. SHA-256 is defined in [NIST's Secure Hash Standard](https://csrc.nist.gov/pubs/fips/180-4/upd1/final). Using that standard does not mean OpenSkiTime is a certified or independently audited random number generator.

**Randomness and reproducibility are compatible.** The seed is generated randomly before the draw. Once the seed and input data are known, the resulting order is deliberately deterministic. Verification reuses the original seed rather than generating another one.

## How a competitor receives a position

The software performs a double draw within each group:

1. Randomly select one competitor who has not yet been assigned a position.
2. Randomly select one position that is still available.
3. Assign that competitor to that position and remove both from the remaining choices.
4. Continue until every competitor has exactly one position.

If five competitors and five positions remain, each selection is made from five choices. Neither a competitor nor a position is returned to the pool. A competitor therefore cannot receive two bibs, and two competitors cannot receive the same position.

Before drawing, the input is placed in a defined order using points and FIS codes. Consequently, competitor insertion order or database row order does not change the draw. A FIS code identifies a competitor when ordering the input; it is not a draw weight. Changing an identifier with a fixed seed can change a particular result, but no identifier receives additional weight under the random-seed model.

## Why integer conversion does not favour the first choice

A simple remainder operation, such as `randomValue % 15`, could introduce a small bias. The number of possible 32-bit values, 2^32, is not divisible by 15. The software removes this conversion bias through **rejection sampling**.

For `m` choices, the software calculates a rejection threshold of `2^32 mod m`. Values below that threshold are discarded and another value is generated. The number of accepted values is then exactly divisible by `m`, giving every remainder the same number of accepted input values. For 15 choices, the threshold is 1: value 0 is rejected, and the remaining 4,294,967,295 values divide equally among the 15 choices.

This is a mathematical justification for unbiased conversion **assuming uniformly distributed 32-bit input values**. It does not independently establish that the entire SHA-256-derived sequence is independent. The practical implementation relies on a strong seed source and a cryptographic hash.

## What fairness means numerically

Under the model of independent, uniformly distributed selections, all `m!` orders of a group of `m` competitors are equally likely. Each competitor then has a probability of `1/m` of receiving any particular position in that group. For 15 competitors, this is approximately **6.67%** per position.

The reasoning is straightforward: at the first step, every competitor and every position has the same selection probability. After assigning a competitor, the remaining problem has the same structure with one fewer competitor and position. The same symmetry holds at each step. The algorithm contains no athlete-specific weights or list of favourites.

This describes the algorithm's probability model. It does not claim a proven, perfectly uniform distribution over all orders generated by the finite set of 128-bit seeds. Random draws can produce repeated bibs across races, successive unfavourable bibs and clusters of athletes from the same club. The draw does not remember a competitor's previous bib or compensate for it in the next race. A single unfavourable bib therefore does not establish bias.

## How the implementation has been checked

The project's `StartListTests` check reproducibility independent of input order, points ties at the first-group boundary, the group without points, unique bibs, and replay after saving and reopening a file. Before saving a plan, the persistence layer recalculates it and rejects an order that does not match its recorded rules, seed and inputs.

A [runnable verification example](examples/draw-audit/Program.cs) was also created for this explanation. It compares the production algorithm against a separately written double draw. It checks that changing input order, names, clubs and nations leaves the verification order unchanged when identifiers, points and seed remain the same.

**Diagnostic run on 2 October 2026:** 20,000 draws with 15 fictional competitors in one first group, using a publicly defined, reproducible seed sequence. The production and separately written verification algorithms produced identical orders in every draw. All were valid permutations of 15 competitors. There were 225 athlete–position combinations; the expected count per combination was approximately 1,333.33, and observed counts ranged from **1,210 to 1,432**. No real personal data was used.

This is a verifiable observation about the implementation. Frequency variation is part of random behaviour. The diagnostic is not a mathematical proof of randomness, an official approval or evidence of what an operator did at a particular race. Its deterministic seed sequence also does not test the quality of the Windows random source.

## What is recorded for a race

The series `.ost` file stores competition details, athlete input snapshots and points, the points-list identifier and validity dates, settings, seed, rule version and resulting order. It also records the start-list revision, creation time, operator and reason. The operator field is the identity supplied to the application; it is not independently verified proof of identity.

Successfully saving a redraw creates a new start-list revision. The application blocks redraws once the run has been marked started or timing capture has begun for it. Run 1 also cannot be redrawn after Run 2 references its bibs.

**Verification limit:** redraws can be performed before the run begins. Unsaved attempts, failed attempts and experiments outside the application are not all recorded in start-list history. This implementation also has no external digital signature or public commitment to the seed. Replay establishes that the saved order matches the saved inputs; it does not alone establish that someone never searched for a favourable seed or modified the database outside the application.

At a race, confidence is supported by checking entries, points and settings before the draw, conducting the draw in front of witnesses, and publishing the accepted list immediately. Any redraw should be explained and its revision identified. This is a recommended officials' procedure, rather than a public oversight mechanism already implemented in the software.

## How to verify it yourself

### Inspect the source code

The GitHub links below are pinned to the reviewed commit. Their contents will not change with later software updates.

| Item to inspect | Source and method |
| --- | --- |
| Generation of a new seed | [MainViewModel.Draw.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/src/OpenSkiTime.Rewrite.Desktop/MainViewModel.Draw.cs#L303-L340), `PrepareDrawAsync` |
| Points grouping and group draws | [StartLists.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/src/OpenSkiTime.Rewrite.Domain/StartLists.cs#L30-L74), `FisStartOrder.FirstRun` |
| SHA-256, rejection sampling and double draw | [StartLists.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/src/OpenSkiTime.Rewrite.Domain/StartLists.cs#L115-L150), `DrawRandom.Next` and `DoubleDraw` |
| Run 2 order | [StartLists.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/src/OpenSkiTime.Rewrite.Domain/StartLists.cs#L76-L104), `SecondRun` |
| Save validation and revisions | [StartListStore.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/src/OpenSkiTime.Rewrite.Persistence/StartListStore.cs), `SaveStartListAsync` and `ValidateStartPlanAsync` |
| Reproducibility and persistence tests | [StartListTests.cs](https://github.com/tnakeli/OpenSkiTime/blob/468fa52a8b2cf49efa2ba0eec3a632553015c186/rewrite/tests/OpenSkiTime.Rewrite.Tests/StartListTests.cs) |

### Run the public verification example

With the .NET 10 SDK installed, run this command from the repository root:

```powershell
dotnet run --project docs/examples/draw-audit/DrawAudit.csproj -c Release -- 20000
```

The example uses the production domain code and a separately written verification algorithm. It does not open race files or modify race data. Seed `00112233445566778899AABBCCDDEEFF`, together with the example's 15 competitors, must produce these FIS codes for bibs 1–15:

```text
100001, 100009, 100002, 100006, 100005,
100013, 100007, 100011, 100014, 100004,
100010, 100012, 100008, 100015, 100003
```

### Replay a draw from an actual race

Obtain an organiser-provided backup and identify the competition, run and start-list revision to be verified. Race files contain personal data and should not be published alongside this verification guide.

Read that revision's `StartLists.PlanJson` and the associated `StartListEntries.EntryJson` rows in start order. `PlanJson` contains `Seed`, `RuleVersion`, `Competition`, `Gender`, `PointsList` and `Options`; the saved athlete draw inputs are in the `EntryJson` rows. The current competitor table or an ordinary TSV start-list export does not replace those original inputs.

With the saved plan and entries loaded, replay Run 1 using the same algorithm version:

```csharp
var replay = FisStartOrder.FirstRun(
    savedPlan.CompetitionId, savedPlan.Competition, savedPlan.Gender,
    savedEntries.Select(x => x.Entrant).ToArray(),
    savedPlan.PointsList, savedPlan.Options, savedPlan.Seed);
```

Compare each row's position, bib, competitor ID and group with the original list. A difference means that the input data, algorithm version or order differs from the recorded draw and needs investigation. The same seed and inputs under the same algorithm version produce the same order.

This explanation describes the reviewed software implementation. It is not an approval issued by FIS and does not replace checking the applicable competition rules and officials' procedures.
