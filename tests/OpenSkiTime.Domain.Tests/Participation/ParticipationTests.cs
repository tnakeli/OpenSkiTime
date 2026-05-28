namespace OpenSkiTime.Domain.Tests.Participation;

public class ParticipationTests
{
    private static readonly Guid CompetitorId = Guid.NewGuid();
    private static readonly Guid CompetitionId = Guid.NewGuid();

    [Fact]
    public void Create_sets_properties_correctly()
    {
        var p = Domain.Participation.Participation.Create(
            Guid.NewGuid(), CompetitorId, CompetitionId, isParticipating: true, startOrder: 3);

        p.CompetitorId.Should().Be(CompetitorId);
        p.CompetitionId.Should().Be(CompetitionId);
        p.IsParticipating.Should().BeTrue();
        p.StartOrder.Should().Be(3);
    }

    [Fact]
    public void Create_with_empty_competitor_id_throws()
    {
        var act = () => Domain.Participation.Participation.Create(
            Guid.NewGuid(), Guid.Empty, CompetitionId, true);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_zero_start_order_throws()
    {
        var act = () => Domain.Participation.Participation.Create(
            Guid.NewGuid(), CompetitorId, CompetitionId, true, startOrder: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SetParticipating_toggles_flag()
    {
        var p = Domain.Participation.Participation.Create(
            Guid.NewGuid(), CompetitorId, CompetitionId, true);
        p.SetParticipating(false);
        p.IsParticipating.Should().BeFalse();
    }

    [Fact]
    public void SetStartOrder_updates_order()
    {
        var p = Domain.Participation.Participation.Create(
            Guid.NewGuid(), CompetitorId, CompetitionId, true);
        p.SetStartOrder(7);
        p.StartOrder.Should().Be(7);
    }

    [Fact]
    public void SetStartOrder_null_clears_order()
    {
        var p = Domain.Participation.Participation.Create(
            Guid.NewGuid(), CompetitorId, CompetitionId, true, startOrder: 5);
        p.SetStartOrder(null);
        p.StartOrder.Should().BeNull();
    }
}
