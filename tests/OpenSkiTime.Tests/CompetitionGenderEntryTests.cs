using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// A men's competition takes only men and a women's competition only women, on every registration write path.
public class CompetitionGenderEntryTests
{
    private static readonly SeriesValues s_series = new("Synthetic Cup", "Synthetic Fell", "Synthetic Club",
        new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20), "FIN", "2026/27");
    private static readonly CompetitorValues s_woman = new("Synthwoman", "Aino", 2008, "990001", "FIN", "Club", Gender.Female);
    private static readonly CompetitorValues s_man = new("Synthman", "Eino", 2007, "990002", "FIN", "Club", Gender.Male);

    private static CompetitionValues Race(string label, string gender) => new($"Synthetic {label}", label, new DateOnly(2026, 11, 19),
        Discipline.GiantSlalom, RaceType.Fis, 2, 0, "9123", Calendar: new CompetitionCalendarData(2027, "Synthetic Fell", "FIN", "NJR", gender));

    [Theory]
    [InlineData("M", Gender.Male, true)]
    [InlineData("M", Gender.Female, false)]
    [InlineData("W", Gender.Female, true)]
    [InlineData("w", Gender.Male, false)]
    [InlineData("L", Gender.Male, false)]
    [InlineData("A", Gender.Female, true)]
    [InlineData("", Gender.Male, true)]
    [InlineData(null, Gender.Female, true)]
    public void CalendarGenderDecidesWhoMayBeEntered(string? calendar, Gender competitor, bool allowed)
    {
        Assert.Equal(allowed, CompetitionGenderRule.Allows(calendar, competitor));
        // A competitor whose gender is not known yet is not refused; the draw names them instead.
        Assert.True(CompetitionGenderRule.Allows(calendar, null));
    }

    [Theory]
    [InlineData("M", "Men")]
    [InlineData("W", "Women")]
    [InlineData("L", "Women")]
    [InlineData("A", "Mixed")]
    [InlineData("", "")]
    public void CompetitionGenderCodesShowTheCompetitorWords(string code, string label)
    {
        Assert.Equal(label, CompetitionGenderLabels.Format(code));
        Assert.Equal(code == "L" ? "W" : code, CompetitionGenderLabels.ToCode(label));
    }

    [Fact]
    public async Task EveryWritePathRefusesTheOtherGenderAndSavesNothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-gender-entry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(folder, "series.ost"), s_series);
            var men = await workspace.SaveCompetitionAsync(null, Race("GS M", "M"), series.Revision);
            var menId = Assert.Single(men.Competitions).Id;
            var women = await workspace.SaveCompetitionAsync(null, Race("GS W", "W"), men.Revision);
            var womenId = women.Competitions.Single(x => x.Id != menId).Id;

            // Batch (grid save): a woman in the men's race.
            var batch = new DeskBatch(series.Id, women.Revision, [new DeskBatchRow(null, s_woman, [new ImportEntryPatch(menId, true, false, null)])], []);
            var error = await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(batch));
            Assert.Contains("SYNTHWOMAN Aino (Women) cannot be entered in GS M, a men's competition", error.Message, StringComparison.Ordinal);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(women.Revision, desk.Revision);
            Assert.Empty(desk.Competitors);

            // Single row save.
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveDeskRowAsync(null, s_man, womenId, true, null, women.Revision));
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);

            // Import (paste from Excel).
            var import = new ImportCommit(series.Id, women.Revision, "synthetic-source",
                [new ImportCommitRow(null, s_man, [new ImportEntryPatch(womenId, true, false, null)])]);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyImportAsync(import));
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);

            // The right gender saves; changing an entered competitor's gender is refused afterwards.
            var saved = await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, women.Revision,
                [new DeskBatchRow(null, s_woman, [new ImportEntryPatch(womenId, true, false, null)]),
                 new DeskBatchRow(null, s_man, [new ImportEntryPatch(menId, true, false, null)])], []));
            Assert.Equal(2, saved.Created);
            desk = await workspace.ReadCompetitorDeskAsync();
            var woman = desk.Competitors.Single(x => x.Values.Gender == Gender.Female);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, saved.Revision,
                [new DeskBatchRow(woman.Id, woman.Values with { Gender = Gender.Male }, [])], [])));
            Assert.Equal(saved.Revision, (await workspace.ReadCompetitorDeskAsync()).Revision);

            // An existing wrong entry (here: the race's gender changed later) never blocks unrelated registration work.
            var raceChange = await workspace.SaveCompetitionAsync(menId, Race("GS M", "W"), saved.Revision);
            var man = desk.Competitors.Single(x => x.Values.Gender == Gender.Male);
            var unrelated = await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, raceChange.Revision,
                [new DeskBatchRow(woman.Id, woman.Values with { Club = "Synthetic Other Club" }, [])], []));
            Assert.Equal(1, unrelated.Updated);
            // Removing the wrong entry is always allowed.
            Assert.Equal(1, (await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, unrelated.Revision,
                [new DeskBatchRow(man.Id, man.Values, [new ImportEntryPatch(menId, false, false, null)])], []))).Updated);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
