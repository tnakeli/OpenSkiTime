using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Persistence.Configurations;

internal sealed class EventSeriesConfiguration : IEntityTypeConfiguration<EventSeries>
{
    public void Configure(EntityTypeBuilder<EventSeries> b)
    {
        b.ToTable("EventSeries");

        b.HasKey(e => e.Id);

        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.Location).HasMaxLength(200).IsRequired();
        b.Property(e => e.Organizer).HasMaxLength(200).IsRequired();
        b.Property(e => e.StartDate).IsRequired();
        b.Property(e => e.EndDate).IsRequired();
        b.Property(e => e.Nation).HasMaxLength(3).IsRequired();
        b.Property(e => e.Season).HasMaxLength(20).IsRequired();
        b.Property(e => e.CreatedAt).IsRequired();

        // Plain long, hand-incremented in DbContext.SaveChangesAsync — not
        // an EF concurrency token (we're a single-process desktop app).
        b.Property(e => e.RowVersion).IsRequired();

        // Owned children
        b.HasMany(e => e.Competitions)
            .WithOne()
            .HasForeignKey(c => c.EventSeriesId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(e => e.Competitions)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_competitions");
    }
}
