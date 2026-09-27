using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

public sealed partial class SeriesDbContext(DbContextOptions<SeriesDbContext> options) : DbContext(options)
{
    internal const string Format = "OpenSkiTime.New/1";
    internal DbSet<SeriesRow> Series => Set<SeriesRow>();
    internal DbSet<CompetitionRow> Competitions => Set<CompetitionRow>();
    internal DbSet<CompetitorRow> Competitors => Set<CompetitorRow>();
    internal DbSet<ParticipationRow> Participations => Set<ParticipationRow>();
    internal DbSet<CategoryRuleRow> CategoryRules => Set<CategoryRuleRow>();
    internal DbSet<ImportReceiptRow> ImportReceipts => Set<ImportReceiptRow>();
    internal DbSet<RunRow> Runs => Set<RunRow>();
    internal DbSet<StartListRow> StartLists => Set<StartListRow>();
    internal DbSet<StartListEntryRow> StartListEntries => Set<StartListEntryRow>();

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
        competition.HasAlternateKey(x => new { x.SeriesId, x.Id });
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

        var competitor = modelBuilder.Entity<CompetitorRow>();
        competitor.ToTable("Competitors", table =>
            table.HasCheckConstraint("CK_Competitor_BirthYear", "BirthYear IS NULL OR BirthYear >= 1850"));
        competitor.HasKey(x => x.Id);
        competitor.Property(x => x.Id).ValueGeneratedNever();
        competitor.HasAlternateKey(x => new { x.SeriesId, x.Id });
        competitor.HasOne<SeriesRow>().WithMany().HasForeignKey(x => x.SeriesId)
            .HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        competitor.HasIndex(x => new { x.SeriesId, x.FederationCodeKey }).IsUnique();
        competitor.Property(x => x.Surname).IsRequired().HasMaxLength(100);
        competitor.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
        competitor.Property(x => x.FederationCode).HasMaxLength(20);
        competitor.Property(x => x.FederationCodeKey).HasMaxLength(20);
        competitor.Property(x => x.Nation).HasMaxLength(3);
        competitor.Property(x => x.Club).HasMaxLength(160);
        competitor.Property(x => x.Gender).HasConversion<string>().HasMaxLength(10);

        var participation = modelBuilder.Entity<ParticipationRow>();
        participation.ToTable("Participations", table =>
            table.HasCheckConstraint("CK_Participation_Bib", "ImportedBib IS NULL OR ImportedBib BETWEEN 1 AND 99999"));
        participation.HasKey(x => new { x.CompetitorId, x.CompetitionId });
        participation.HasOne<CompetitorRow>().WithMany().HasForeignKey(x => new { x.SeriesId, x.CompetitorId })
            .HasPrincipalKey(x => new { x.SeriesId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        participation.HasOne<CompetitionRow>().WithMany().HasForeignKey(x => new { x.SeriesId, x.CompetitionId })
            .HasPrincipalKey(x => new { x.SeriesId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        participation.HasIndex(x => new { x.SeriesId, x.CompetitionId, x.ImportedBib }).IsUnique();

        var category = modelBuilder.Entity<CategoryRuleRow>();
        category.ToTable("CategoryRules", table =>
            table.HasCheckConstraint("CK_Category_Years", "BirthYearMin >= 1850 AND BirthYearMin <= BirthYearMax"));
        category.HasKey(x => x.Id);
        category.Property(x => x.Id).ValueGeneratedNever();
        category.HasOne<SeriesRow>().WithMany().HasForeignKey(x => x.SeriesId)
            .HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        category.HasIndex(x => new { x.SeriesId, x.LabelKey }).IsUnique();
        category.Property(x => x.Label).IsRequired().HasMaxLength(100);
        category.Property(x => x.LabelKey).IsRequired().HasMaxLength(100);
        category.Property(x => x.Gender).HasConversion<string>().HasMaxLength(10);

        var receipt = modelBuilder.Entity<ImportReceiptRow>();
        receipt.ToTable("ImportReceipts");
        receipt.HasKey(x => new { x.SeriesId, x.SourceHash });
        receipt.HasOne<SeriesRow>().WithMany().HasForeignKey(x => x.SeriesId)
            .HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        receipt.Property(x => x.SourceHash).HasMaxLength(64);

        var run = modelBuilder.Entity<RunRow>();
        run.ToTable("Runs", t => t.HasCheckConstraint("CK_Run_Number", "Number BETWEEN 1 AND 9"));
        run.HasKey(x => x.Id);
        run.Property(x => x.Id).ValueGeneratedNever();
        run.HasOne<CompetitionRow>().WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
        run.HasIndex(x => new { x.CompetitionId, x.Gender, x.Number }).IsUnique();
        run.Property(x => x.Gender).HasConversion<string>();
        var list = modelBuilder.Entity<StartListRow>();
        list.ToTable("StartLists");
        list.HasKey(x => x.Id);
        list.Property(x => x.Id).ValueGeneratedNever();
        list.HasOne<RunRow>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        list.HasIndex(x => new { x.RunId, x.Revision }).IsUnique();
        var start = modelBuilder.Entity<StartListEntryRow>();
        start.ToTable("StartListEntries", t =>
        {
            t.HasCheckConstraint("CK_Start_Position", "Position > 0");
            t.HasCheckConstraint("CK_Start_Bib", "Bib BETWEEN 1 AND 99999");
        });
        start.HasKey(x => new { x.ListId, x.Position });
        start.HasIndex(x => new { x.ListId, x.Bib }).IsUnique();
        start.HasIndex(x => new { x.ListId, x.CompetitorId }).IsUnique();
        start.HasOne<StartListRow>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        ConfigureTiming(modelBuilder);
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

internal sealed class CompetitorRow
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public string Surname { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public int? BirthYear { get; set; }
    public string? FederationCode { get; set; }
    public string? FederationCodeKey { get; set; }
    public string? Nation { get; set; }
    public string? Club { get; set; }
    public Gender? Gender { get; set; }
}

internal sealed class ParticipationRow
{
    public Guid SeriesId { get; set; }
    public Guid CompetitorId { get; set; }
    public Guid CompetitionId { get; set; }
    public bool Participates { get; set; }
    public int? ImportedBib { get; set; }
    public int? StartOrder { get; set; }
}

internal sealed class CategoryRuleRow
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public int BirthYearMin { get; set; }
    public int BirthYearMax { get; set; }
    public Gender? Gender { get; set; }
    public int DisplayOrder { get; set; }
}

internal sealed class ImportReceiptRow
{
    public Guid SeriesId { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public long Revision { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
}

internal sealed class RunRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public Gender Gender { get; set; }
    public int Number { get; set; }
}

internal sealed class StartListRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? StartedBy { get; set; }
    public string? SourceTimingVersion { get; set; }
    public string Operator { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
}

internal sealed class StartListEntryRow
{
    public Guid ListId { get; set; }
    public int Position { get; set; }
    public int Bib { get; set; }
    public Guid CompetitorId { get; set; }
    public string EntryJson { get; set; } = string.Empty;
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
