using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;

namespace OpenSkiTime.Domain.Tests.Competitions;

public class CompetitionTests
{
    private static Competition NewClubSlalom(int runs = 2, int intermediates = 1) =>
        Competition.Create(
            id: Guid.NewGuid(),
            eventSeriesId: Guid.NewGuid(),
            name: "Slalom",
            shortLabel: "3.1 SL",
            date: new DateOnly(2026, 4, 3),
            discipline: Discipline.SL,
            raceType: RaceType.Club,
            numberOfRuns: runs,
            numberOfIntermediateTimes: intermediates);

    [Fact]
    public void Create_assigns_all_fields()
    {
        var c = NewClubSlalom();
        c.Name.Should().Be("Slalom");
        c.ShortLabel.Should().Be("3.1 SL");
        c.NumberOfRuns.Should().Be(2);
        c.NumberOfIntermediateTimes.Should().Be(1);
        c.RaceType.Should().Be(RaceType.Club);
        c.FisCode.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_runs_below_one(int runs)
    {
        var act = () => NewClubSlalom(runs: runs);
        act.Should().Throw<ArgumentException>().WithMessage("*NumberOfRuns*");
    }

    [Fact]
    public void Create_rejects_negative_intermediates()
    {
        var act = () => NewClubSlalom(intermediates: -1);
        act.Should().Throw<ArgumentException>().WithMessage("*NumberOfIntermediateTimes*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Create_with_FIS_race_type_requires_FisCode(string? fisCode)
    {
        var act = () => Competition.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            "Slalom", "3.1 SL", new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.FIS, 2, 0,
            fisCode: fisCode);

        act.Should().Throw<ArgumentException>().WithMessage("*FIS code*");
    }

    [Theory]
    [InlineData(RaceType.Club)]
    [InlineData(RaceType.National)]
    [InlineData(RaceType.Training)]
    public void Create_with_non_FIS_race_type_allows_blank_FisCode(RaceType raceType)
    {
        var c = Competition.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            "Slalom", "3.1 SL", new DateOnly(2026, 4, 3),
            Discipline.SL, raceType, 2, 0,
            fisCode: null);

        c.RaceType.Should().Be(raceType);
        c.FisCode.Should().BeNull();
    }

    [Fact]
    public void Create_with_FIS_race_type_and_FisCode_succeeds()
    {
        var c = Competition.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            "Slalom", "3.1 SL", new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.FIS, 2, 0,
            fisCode: "FIN-001");

        c.FisCode.Should().Be("FIN-001");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_rejects_blank_short_label(string label)
    {
        var act = () => Competition.Create(
            Guid.NewGuid(), Guid.NewGuid(),
            "Slalom", label, new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.Club, 2, 0);

        act.Should().Throw<ArgumentException>().WithMessage("*short label*");
    }

    [Fact]
    public void HasAllRequiredData_true_for_valid_competition()
    {
        NewClubSlalom().HasAllRequiredData().Should().BeTrue();
    }

    [Fact]
    public void UpdateBasicData_re_validates_FIS_rule()
    {
        var c = NewClubSlalom();
        var act = () => c.UpdateBasicData(
            "Slalom", "3.1 SL", new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.FIS, 2, 0,
            fisCode: null,
            localRaceCode: null, gender: null, courseName: null,
            startAltitudeMeters: null, finishAltitudeMeters: null,
            verticalDropMeters: null, homologationNumber: null);

        act.Should().Throw<ArgumentException>().WithMessage("*FIS code*");
    }
}
