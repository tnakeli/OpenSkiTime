using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class SharedCompetitionCourseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedCourseSaveIsAtomicAndPortableForExistingAndNewRaces(bool createNew)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-shared-course-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var first = new CompetitionValues("Synthetic race", "SL1 W", date, Discipline.Slalom, RaceType.Fis, 2, 0, "0034",
                CourseName: "Original slope", HomologationNumber: "123/10/26", Calendar: new(2027, "Test place", "FIN", "NJR", "W", null));
            var other = first with { Name = "Other race", ShortLabel = "GS1 M", FisCode = "0035", Discipline = Discipline.GiantSlalom,
                Calendar = first.Calendar! with { Gender = "M", Category = "NC" } };
            var created = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"), [first, other]);
            var target = created.Competitions.Single(x => x.Values.ShortLabel == first.ShortLabel);
            var otherId = created.Competitions.Single(x => x.Values.ShortLabel == other.ShortLabel).Id;
            var values = first with { Name = "Edited race", ShortLabel = createNew ? "SL2 W" : first.ShortLabel, FisCode = createNew ? "0036" : first.FisCode,
                CourseName = "Shared slope", HomologationNumber = "234/10/26", StartAltitudeMeters = 500,
                FinishAltitudeMeters = 300, VerticalDropMeters = 200, CourseLengthMeters = 640 };
            var saved = await workspace.SaveCompetitionAsync(createNew ? null : target.Id, values, created.Revision, true);
            Assert.Equal(created.Revision + 1, saved.Revision);
            Assert.Equal(createNew ? 3 : 2, saved.Competitions.Count);
            Assert.Equal(other with { CourseName = values.CourseName, HomologationNumber = values.HomologationNumber,
                StartAltitudeMeters = 500, FinishAltitudeMeters = 300, VerticalDropMeters = 200, CourseLengthMeters = 640 },
                saved.Competitions.Single(x => x.Id == otherId).Values);
            await workspace.CloseAsync(); var reopened = await workspace.OpenAsync(path);
            Assert.Equal(saved.Competitions, reopened.Competitions);

            // A constraint failure must roll back every copied field and the edited competition.
            var conflict = values with { ShortLabel = other.ShortLabel, CourseName = "Must roll back" };
            await Assert.ThrowsAsync<SeriesFileException>(() => workspace.SaveCompetitionAsync(target.Id, conflict, saved.Revision, true));
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.SaveCompetitionAsync(target.Id, values, created.Revision, true));
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveCompetitionAsync(target.Id,
                values with { StartAltitudeMeters = -1 }, saved.Revision, true));
            var unchanged = await workspace.ReadAsync();
            Assert.Equal(saved.Revision, unchanged.Revision); Assert.Equal(saved.Competitions, unchanged.Competitions);

            // Ordinary save stays scoped to one race; explicit sharing can also clear course values.
            var empty = values with { ShortLabel = first.ShortLabel, CourseName = null, HomologationNumber = null,
                StartAltitudeMeters = null, FinishAltitudeMeters = null, VerticalDropMeters = null, CourseLengthMeters = null };
            var local = await workspace.SaveCompetitionAsync(target.Id, empty, saved.Revision);
            Assert.Equal("Shared slope", local.Competitions.Single(x => x.Id == otherId).Values.CourseName);
            var cleared = await workspace.SaveCompetitionAsync(target.Id, empty, local.Revision, true);
            Assert.All(cleared.Competitions, race =>
            {
                Assert.Null(race.Values.CourseName); Assert.Null(race.Values.HomologationNumber);
                Assert.Null(race.Values.StartAltitudeMeters); Assert.Null(race.Values.FinishAltitudeMeters);
                Assert.Null(race.Values.VerticalDropMeters); Assert.Null(race.Values.CourseLengthMeters);
            });
        }
        finally { Directory.Delete(folder, true); }
    }
}
