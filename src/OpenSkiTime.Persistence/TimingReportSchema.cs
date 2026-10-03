using Microsoft.EntityFrameworkCore;

namespace OpenSkiTime.Persistence;

public sealed partial class SeriesDbContext
{
    private static void ConfigureTimingReport(ModelBuilder model)
    {
        var report = model.Entity<TimingReportRow>();
        report.ToTable("TimingReports", t => t.HasCheckConstraint("CK_TimingReport_Revision", "Revision > 0"));
        report.HasKey(x => new { x.CompetitionId, x.Revision });
        report.HasOne<CompetitionRow>().WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
        var image = model.Entity<TimingReportImageRow>();
        image.ToTable("TimingReportImages"); image.HasKey(x => x.Id); image.Property(x => x.Id).ValueGeneratedNever();
        image.HasOne<CompetitionRow>().WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
        var approved = model.Entity<ApprovedTimingReportRow>();
        approved.ToTable("ApprovedTimingReports", t => t.HasCheckConstraint("CK_ApprovedTimingReport_Revision", "Revision > 0"));
        approved.HasKey(x => x.Id); approved.Property(x => x.Id).ValueGeneratedNever();
        approved.HasIndex(x => new { x.CompetitionId, x.Revision }).IsUnique();
        approved.HasOne<TimingReportRow>().WithMany().HasForeignKey(x => new { x.CompetitionId, x.DraftRevision }).OnDelete(DeleteBehavior.Restrict);
        var submission = model.Entity<TimingReportSubmissionRow>();
        submission.ToTable("TimingReportSubmissions"); submission.HasKey(x => x.Id);
        submission.HasOne<ApprovedTimingReportRow>().WithMany().HasForeignKey(x => x.ApprovalId).OnDelete(DeleteBehavior.Restrict);
    }
}
