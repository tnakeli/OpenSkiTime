using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

public sealed class SeriesDbContext(DbContextOptions<SeriesDbContext> options) : DbContext(options)
{
    internal const string Format = "OpenSkiTime.New/1";
    internal DbSet<SeriesRow> Series => Set<SeriesRow>();
    internal DbSet<CompetitionRow> Competitions => Set<CompetitionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var series = modelBuilder.Entity<SeriesRow>();
        series.ToTable("Series", table =>
        {
            table.HasCheckConstraint("CK_Series_OneRow", "SingleRow = 1");
            table.HasCheckConstraint("CK_Series_Format", "FormatId = 'OpenSkiTime.New/1'");
        });
        series.HasKey(x => x.SingleRow);
        series.Property(x => x.SingleRow).ValueGeneratedNever();
        series.HasIndex(x => x.Id).IsUnique();
        series.Property(x => x.FormatId).IsRequired().HasMaxLength(32);
        series.Property(x => x.Name).IsRequired().HasMaxLength(160);
        series.Property(x => x.Location).IsRequired().HasMaxLength(160);
        series.Property(x => x.Organizer).IsRequired().HasMaxLength(160);
        series.Property(x => x.Nation).IsRequired().HasMaxLength(3);
        series.Property(x => x.Season).IsRequired().HasMaxLength(40);
        series.Property(x => x.Revision).IsConcurrencyToken();

        var competition = modelBuilder.Entity<CompetitionRow>();
        competition.ToTable("Competitions", table =>
        {
            table.HasCheckConstraint("CK_Competition_Runs", "RunCount BETWEEN 1 AND 9");
            table.HasCheckConstraint("CK_Competition_Intermediates", "IntermediateCount BETWEEN 0 AND 20");
        });
        competition.HasKey(x => x.Id);
        competition.Property(x => x.Id).ValueGeneratedNever();
        competition.HasOne<SeriesRow>().WithMany().HasForeignKey(x => x.SeriesId)
            .HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        competition.HasIndex(x => new { x.SeriesId, x.ShortLabelKey }).IsUnique();
        competition.Property(x => x.Name).IsRequired().HasMaxLength(160);
        competition.Property(x => x.ShortLabel).IsRequired().HasMaxLength(20);
        competition.Property(x => x.ShortLabelKey).IsRequired().HasMaxLength(20);
        competition.Property(x => x.Discipline).HasConversion<string>().HasMaxLength(30);
        competition.Property(x => x.RaceType).HasConversion<string>().HasMaxLength(30);
        competition.Property(x => x.FisCode).HasMaxLength(50);
        competition.Property(x => x.LocalRaceCode).HasMaxLength(50);
        competition.Property(x => x.CourseName).HasMaxLength(160);
        competition.Property(x => x.HomologationNumber).HasMaxLength(50);
    }
}

internal sealed class SeriesRow
{
    public int SingleRow { get; set; } = 1;
    public string FormatId { get; set; } = SeriesDbContext.Format;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Organizer { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Nation { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;
    public long Revision { get; set; }
}

internal sealed class CompetitionRow
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShortLabel { get; set; } = string.Empty;
    public string ShortLabelKey { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public Discipline Discipline { get; set; }
    public RaceType RaceType { get; set; }
    public int RunCount { get; set; }
    public int IntermediateCount { get; set; }
    public string? FisCode { get; set; }
    public string? LocalRaceCode { get; set; }
    public string? CourseName { get; set; }
    public int? StartAltitudeMeters { get; set; }
    public int? FinishAltitudeMeters { get; set; }
    public int? VerticalDropMeters { get; set; }
    public string? HomologationNumber { get; set; }
}

public sealed class SeriesDbContextFactory : IDesignTimeDbContextFactory<SeriesDbContext>
{
    public SeriesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SeriesDbContext>()
            .UseSqlite("Data Source=openskitime-rewrite-design.db")
            .Options;
        return new SeriesDbContext(options);
    }
}
