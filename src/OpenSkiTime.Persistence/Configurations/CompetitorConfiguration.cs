using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitors;

namespace OpenSkiTime.Persistence.Configurations;

internal sealed class CompetitorConfiguration : IEntityTypeConfiguration<Competitor>
{
    public void Configure(EntityTypeBuilder<Competitor> b)
    {
        b.ToTable("Competitors");

        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.EventSeriesId).IsRequired();

        // UpperCaseName value object — stored as a plain varchar column.
        b.Property(c => c.LastName)
            .HasColumnName("LastName")
            .HasMaxLength(100)
            .IsRequired()
            .HasConversion(
                vo => vo.Value,
                s => new UpperCaseName(s));

        b.Property(c => c.FirstName).HasMaxLength(100).IsRequired();
        b.Property(c => c.YearOfBirth).IsRequired();

        b.Property(c => c.FisCode).HasMaxLength(9);
        b.Property(c => c.NationCode).HasMaxLength(3);
        b.Property(c => c.ClubName).HasMaxLength(200);
        b.Property(c => c.Gender).HasConversion<string>().HasMaxLength(10);
        b.Property(c => c.BibNumber);

        // Participations are owned by the Competitor (not the series directly).
        b.HasMany(c => c.Participations)
            .WithOne()
            .HasForeignKey(p => p.CompetitorId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(c => c.Participations)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_participations");
    }
}
