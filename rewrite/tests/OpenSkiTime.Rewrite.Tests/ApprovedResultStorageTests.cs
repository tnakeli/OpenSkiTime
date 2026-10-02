using Microsoft.Data.Sqlite;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class ApprovedResultStorageTests
{
    [Fact]
    public async Task ApprovedXmlSurvivesReopenAndCannotBeOverwritten()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-m7-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "race.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 9, 28);
            var series = await workspace.CreateAsync(path, new("Test", "Ruka", "Club", date, date, "FIN", "2026-27"));
            var values = new CompetitionValues("Downhill", "DH", date, Discipline.Downhill, RaceType.Fis,
                1, 0, "1234", CourseName: "Slope", StartAltitudeMeters: 1200, FinishAltitudeMeters: 800,
                HomologationNumber: "12345/01/26");
            series = await workspace.SaveCompetitionAsync(null, values, series.Revision);
            var competition = series.Competitions[0];
            var entrants = new List<DrawEntrant>();
            for (var i = 1; i <= 5; i++)
            {
                var athlete = new CompetitorValues("RACER" + i, "Test", 2000,
                    (900000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Male);
                var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, series.Revision);
                series = series with { Revision = saved.Revision };
                entrants.Add(new(saved.Value.Id, athlete, i * 10));
            }
            var plan = FisStartOrder.FirstRun(competition.Id, values, Gender.Male, entrants,
                new("1327", date, date), new(), "m7-test");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, series.Revision,
                "Operator", "Test draw", DateTimeOffset.UtcNow))).Revisions);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            var simulator = new SimulatorTimingSource();
            await timing.StartAsync(simulator, new("Test", "Synthetic", date, Simulation: true), "Operator");
            foreach (var entry in list.Plan.Entries)
            {
                var start = TimeSpan.FromHours(12).Ticks + entry.Position * TimeSpan.FromMinutes(2).Ticks;
                await timing.ArmAsync(entry.Bib, null);
                await simulator.PulseAsync(0, start);
                await UntilAsync(() => timing.Snapshot!.Results.Any(x => x.Bib == entry.Bib && x.Status == TimingStatus.OnCourse));
                await timing.ArmAsync(null, entry.Bib);
                await simulator.PulseAsync(1, start + TimeSpan.FromSeconds(50 + entry.Position).Ticks);
                await UntilAsync(() => timing.Snapshot!.Results.Any(x => x.Bib == entry.Bib && x.Status == TimingStatus.Finished));
            }
            await timing.StopAsync();
            var raw = await workspace.ReadTimingAsync(list.Id);
            var race = FisRaceResults.Assemble(raw.List, TimingReplay.Restore(raw, new AlgeDecoderFactory()));
            var penalty = FisPenalty.Calculate(values.Discipline, race.PenaltyCompetitors, new(0, 999, 0));
            var metadata = new FisXmlDetails("FIS", new("T", "Delegate", "FIN"),
                new("C", "Chief", "FIN"), [new(42, 40, "12:00", new("S", "Setter", "FIN"))]);
            var xml = FisResultXml.Create(race, (await workspace.ReadAsync()).Values, penalty, metadata);
            var information = RaceInformation.Empty(values) with { Category = "FIS" };
            var request = new ApproveResultRequest(competition.Id, list.Id, null,
                ResultSourceFingerprint.Create(raw), (await workspace.ReadAsync()).Revision, "TD",
                "FIN1234.xml", xml, penalty.Calculated, penalty.Applied, information);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveResultAsync(
                request with { SourceFingerprint = new string('0', 64) }));
            Assert.Null(await workspace.ReadRaceInformationAsync(competition.Id));
            var approved = await workspace.ApproveResultAsync(request);
            Assert.Equal(xml, approved.Xml);
            await workspace.CloseAsync();
            await workspace.OpenAsync(path);
            var reopened = Assert.Single(await workspace.ReadApprovedResultsAsync(competition.Id));
            Assert.Equal(approved.Id, reopened.Id);
            Assert.Equal(xml, reopened.Xml);
            Assert.Equal("FIS", reopened.Information!.Category);
            Assert.Equal("FIS", (await workspace.ReadRaceInformationAsync(competition.Id))!.Values.Category);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApproveResultAsync(request));
            await using var sql = new SqliteConnection($"Data Source={path};Pooling=False");
            await sql.OpenAsync();
            foreach (var statement in new[] { "UPDATE ApprovedResults SET ApprovedBy='other'", "DELETE FROM ApprovedResults" })
            {
                await using var command = sql.CreateCommand(); command.CommandText = statement;
                await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            }
        }
        finally
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openskitime-m7-storage"));
            if (Path.GetFullPath(folder).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(folder, recursive: true); }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }
}
