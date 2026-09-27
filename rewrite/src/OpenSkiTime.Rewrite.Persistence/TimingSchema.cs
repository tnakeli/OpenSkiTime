using Microsoft.EntityFrameworkCore;

namespace OpenSkiTime.Rewrite.Persistence;

public sealed partial class SeriesDbContext
{
    internal DbSet<CaptureRow> Captures => Set<CaptureRow>();
    internal DbSet<RawPacketRow> RawPackets => Set<RawPacketRow>();
    internal DbSet<TimingAuditRow> TimingAudit => Set<TimingAuditRow>();

    private static void ConfigureTiming(ModelBuilder model)
    {
        var capture = model.Entity<CaptureRow>();
        capture.ToTable("TimingCaptures");
        capture.HasKey(x => x.Id);
        capture.Property(x => x.Id).ValueGeneratedNever();
        capture.HasOne<StartListRow>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        var raw = model.Entity<RawPacketRow>();
        raw.ToTable("RawTimingPackets", t => t.HasCheckConstraint("CK_Raw_Sequence", "Sequence > 0"));
        raw.HasKey(x => new { x.SessionId, x.Sequence });
        raw.HasOne<CaptureRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<TimingAuditRow>();
        audit.ToTable("TimingAudit");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Id).ValueGeneratedOnAdd();
        audit.HasOne<StartListRow>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        audit.HasIndex(x => new { x.ListId, x.Id });
    }
}

internal sealed class CaptureRow
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public string OptionsJson { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public bool CleanStop { get; set; }
}

internal sealed class RawPacketRow
{
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Protocol { get; set; } = "";
    public string Source { get; set; } = "";
    public string Stream { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
}

internal sealed class TimingAuditRow
{
    public long Id { get; set; }
    public Guid ListId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Operator { get; set; } = "";
    public string Reason { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public long? ReversesId { get; set; }
}
