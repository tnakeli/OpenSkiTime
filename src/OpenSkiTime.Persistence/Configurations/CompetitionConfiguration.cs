using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenSkiTime.Domain.Competitions;

namespace OpenSkiTime.Persistence.Configurations;

internal sealed class CompetitionConfiguration : IEntityTypeConfiguration<Competition>
{
    public void Configure(EntityTypeBuilder<Competition> b)
    {
        b.ToTable("Competitions");

        b.HasKey(c => c.Id);

        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.EventSeriesId).IsRequired();

        b.Property(c => c.Name).HasMaxLength(200).IsRequired();
        b.Property(c => c.ShortLabel).HasMaxLength(20).IsRequired();
        b.Property(c => c.Date).IsRequired();
        b.Property(c => c.Discipline).HasConversion<int>().IsRequired();
        b.Property(c => c.RaceType).HasConversion<int>().IsRequired();
        b.Property(c => c.FisCode).HasMaxLength(50);
        b.Property(c => c.LocalRaceCode).HasMaxLength(50);
        b.Property(c => c.Gender).HasConversion<int?>();
        b.Property(c => c.CourseName).HasMaxLength(200);
        b.Property(c => c.HomologationNumber).HasMaxLength(50);
        b.Property(c => c.NumberOfRuns).IsRequired();
        b.Property(c => c.NumberOfIntermediateTimes).IsRequired();

        // Indexes per data-model.md
        b.HasIndex(c => new { c.EventSeriesId, c.Date })
            .HasDatabaseName("IX_Competitions_EventSeriesId_Date");

        b.HasIndex(c => new { c.EventSeriesId, c.ShortLabel })
            .IsUnique()
            .HasDatabaseName("UQ_Competitions_EventSeriesId_ShortLabel");
    }
}
