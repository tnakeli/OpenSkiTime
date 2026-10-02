using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenSkiTime.Rewrite.Domain;

// Synthetic public inputs only. This example never opens an event database.
var runs = args.Length == 0 ? 20_000 : int.Parse(args[0], CultureInfo.InvariantCulture);
if (runs is < 1 or > 100_000) { throw new ArgumentOutOfRangeException(nameof(args), "Use 1–100000 draws."); }
const string vectorSeed = "00112233445566778899AABBCCDDEEFF";
var race = new CompetitionValues("Synthetic draw", "TEST SL", new(2026, 10, 2), Discipline.Slalom, RaceType.Fis, 2, 0, "0034");
var competitionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
var points = new PointsListSource("SYNTHETIC", new(2026, 10, 1), new(2026, 10, 31));
var entrants = Enumerable.Range(1, 15).Select(i => new DrawEntrant(new Guid(i, 0, 0, new byte[8]),
    new CompetitorValues($"TEST{i:00}", "Athlete", 2000, (100000 + i).ToString(CultureInfo.InvariantCulture), "FIN", "Synthetic", Gender.Female), i)).ToArray();
StartListPlan Draw(string seed, DrawEntrant[]? input = null) => FisStartOrder.FirstRun(competitionId, race,
    Gender.Female, input ?? entrants, points, new(), seed);
int[] Codes(StartListPlan plan) => plan.Entries.Select(x => int.Parse(x.Entrant.Athlete.FederationCode!, CultureInfo.InvariantCulture)).ToArray();

var vector = Codes(Draw(vectorSeed));
var replay = IndependentFirstGroup(entrants.Select(x => int.Parse(x.Athlete.FederationCode!, CultureInfo.InvariantCulture)).ToArray(), vectorSeed);
Require(vector.SequenceEqual(replay), "The independent double draw disagrees with production.");
Require(vector.SequenceEqual(Codes(Draw(vectorSeed, entrants.Reverse().ToArray()))), "Input order changed the draw.");
var renamed = entrants.Select(x => x with { Athlete = x.Athlete with { Surname = "ANOTHER", FirstName = "Name", Nation = "SWE", Club = "Other club" } }).ToArray();
Require(vector.SequenceEqual(Codes(Draw(vectorSeed, renamed))), "Name, nation or club changed the order.");
Console.WriteLine($"Rule version: {FisStartOrder.RuleVersion}");
Console.WriteLine($"Vector seed: {vectorSeed}");
Console.WriteLine("Vector codes by bib: " + string.Join(", ", vector));
Console.WriteLine("Independent replay, input order and identity-field checks: PASS");

var counts = new int[15, 15];
for (var i = 0; i < runs; i++)
{
    // A published deterministic seed series makes this diagnostic repeatable.
    // It does not replace the application's RandomNumberGenerator seed source.
    var seed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("OpenSkiTime draw audit v1:" + i.ToString(CultureInfo.InvariantCulture))))[..32];
    var order = Codes(Draw(seed));
    Require(order.Distinct().Count() == 15 && order.All(x => x is >= 100001 and <= 100015), "Invalid permutation.");
    Require(order.SequenceEqual(IndependentFirstGroup(Enumerable.Range(100001, 15).ToArray(), seed)), "Independent replay disagrees.");
    for (var position = 0; position < 15; position++) { counts[order[position] - 100001, position]++; }
}
var expected = runs / 15.0;
Console.WriteLine($"Synthetic draws: {runs}; expected count per athlete/position: {expected.ToString("F2", CultureInfo.InvariantCulture)}");
Console.WriteLine($"Minimum/maximum over all 225 athlete/position cells: {counts.Cast<int>().Min()}/{counts.Cast<int>().Max()}");
Console.WriteLine("Code       First-bib count    Min/max across 15 positions");
for (var athlete = 0; athlete < 15; athlete++)
{
    var row = Enumerable.Range(0, 15).Select(position => counts[athlete, position]).ToArray();
    Console.WriteLine($"{100001 + athlete}     {row[0],6}             {row.Min()}/{row.Max()}");
}
Console.WriteLine("Frequency variation is descriptive evidence, not a proof of randomness or an accreditation.");

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

// Independent expression of the published selection procedure. It does not call
// the production DrawRandom and uses a 64-bit expression for the rejection cut.
static int[] IndependentFirstGroup(int[] codes, string seed)
{
    var remainingAthletes = codes.ToList();
    var remainingPositions = Enumerable.Range(0, codes.Length).ToList();
    var output = new int[codes.Length];
    uint counter = 0;
    int Pick(int size)
    {
        var cut = (1UL << 32) % (uint)size;
        while (true)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + counter.ToString(CultureInfo.InvariantCulture)));
            counter++;
            var word = BinaryPrimitives.ReadUInt32LittleEndian(digest);
            if (word >= cut) { return (int)(word % (uint)size); }
        }
    }
    while (remainingAthletes.Count > 0)
    {
        var athlete = Pick(remainingAthletes.Count);
        var position = Pick(remainingPositions.Count);
        output[remainingPositions[position]] = remainingAthletes[athlete];
        remainingAthletes.RemoveAt(athlete);
        remainingPositions.RemoveAt(position);
    }
    return output;
}
