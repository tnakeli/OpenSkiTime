using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenSkiTime.Persistence.Configurations;

internal sealed class ParticipationConfiguration
    : IEntityTypeConfiguration<Domain.Participation.Participation>
{
    public void Configure(EntityTypeBuilder<Domain.Participation.Participation> b)
    {
        b.ToTable("Participations");

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.CompetitorId).IsRequired();
        b.Property(p => p.CompetitionId).IsRequired();
        b.Property(p => p.IsParticipating).IsRequired();
        b.Property(p => p.StartOrder);

        // Unique constraint: one participation record per competitor per competition.
        b.HasIndex(p => new { p.CompetitorId, p.CompetitionId }).IsUnique();

        // No navigation back to Competition here — keep EF model simple;
        // competition is looked up via CompetitionId when needed.
    }
}
