using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class SharedCompetitionTdTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TdCanBeSharedIndependentlyOrWithCourseInOnePortableAtomicSave(bool createNew, bool shareCourse)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-shared-td-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var race = new CompetitionValues("Synthetic race", "SL1 W", date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034",
                CourseName: "Original slope", Calendar: new(2027, "Test place", "FIN", "NJR", "W", null));
            var other = race with { ShortLabel = "GS1 M", FisCode = "0035", Discipline = Discipline.GiantSlalom,
                Calendar = race.Calendar! with { Season = 2026, Location = "Other place", Nation = "SWE", Category = "NC", Gender = "M" } };
            var local = race with { ShortLabel = "Club race", RaceType = RaceType.Club, FisCode = null, Calendar = null };
            var created = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"), [race, other, local]);
            var id = created.Competitions.Single(x => x.Values.ShortLabel == race.ShortLabel).Id;
            var td = new CompetitionTechnicalDelegateInfo("TESTLAST", "Testfirst", "FIN", "1047", "Test original name");
            var values = race with { ShortLabel = createNew ? "SL2 W" : race.ShortLabel, FisCode = createNew ? "0036" : race.FisCode,
                CourseName = "New slope", Calendar = race.Calendar! with { TechnicalDelegate = td } };
            var saved = await workspace.SaveCompetitionAsync(createNew ? null : id, values, created.Revision, shareCourse, true);
            Assert.Equal(created.Revision + 1, saved.Revision);
            Assert.All(saved.Competitions, x => Assert.Equal(td, x.Values.Calendar!.TechnicalDelegate));
            var updated = saved.Competitions.Single(x => x.Values.ShortLabel == other.ShortLabel).Values;
            Assert.Equal(other with { CourseName = shareCourse ? "New slope" : other.CourseName,
                Calendar = other.Calendar! with { TechnicalDelegate = td } }, updated);
            Assert.Equal(RaceType.Club, saved.Competitions.Single(x => x.Values.ShortLabel == local.ShortLabel).Values.RaceType);
            await workspace.CloseAsync(); var reopened = await workspace.OpenAsync(path);
            Assert.Equal(saved.Competitions, reopened.Competitions);
            await Assert.ThrowsAsync<SeriesFileException>(() => workspace.SaveCompetitionAsync(id,
                values with { ShortLabel = other.ShortLabel, Calendar = values.Calendar! with { TechnicalDelegate = null } }, saved.Revision, true, true));
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.SaveCompetitionAsync(id, values, created.Revision, false, true));
            var unchanged = await workspace.ReadAsync();
            Assert.Equal(saved.Revision, unchanged.Revision); Assert.Equal(saved.Competitions, unchanged.Competitions);
            var cleared = await workspace.SaveCompetitionAsync(id, values with { ShortLabel = race.ShortLabel,
                Calendar = values.Calendar! with { TechnicalDelegate = null } }, saved.Revision, false, true);
            Assert.All(cleared.Competitions, x => Assert.Null(x.Values.Calendar!.TechnicalDelegate));
        }
        finally { Directory.Delete(folder, true); }
    }
}
