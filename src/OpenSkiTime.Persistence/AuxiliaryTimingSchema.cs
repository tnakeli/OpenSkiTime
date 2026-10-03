using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;

namespace OpenSkiTime.Persistence;

public sealed partial class SeriesDbContext
{
    internal DbSet<AuxiliaryCaptureRow> AuxiliaryCaptures => Set<AuxiliaryCaptureRow>();
    internal DbSet<AuxiliaryRawPacketRow> AuxiliaryRawPackets => Set<AuxiliaryRawPacketRow>();
    private static void ConfigureAuxiliaryTiming(ModelBuilder model)
    {
        var capture = model.Entity<AuxiliaryCaptureRow>();
        capture.ToTable("AuxiliaryCaptures", t => t.HasCheckConstraint("CK_Auxiliary_Role", "Role IN ('B','HandStart','HandFinish')"));
        capture.HasKey(x => x.Id);
        capture.Property(x => x.Id).ValueGeneratedNever();
        capture.Property(x => x.Role).HasConversion<string>();
        capture.HasOne<StartListRow>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        var raw = model.Entity<AuxiliaryRawPacketRow>();
        raw.ToTable("AuxiliaryRawPackets", t => t.HasCheckConstraint("CK_Auxiliary_Sequence", "Sequence > 0"));
        raw.HasKey(x => new { x.SessionId, x.Sequence });
        raw.HasOne<AuxiliaryCaptureRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuxiliaryCaptureRow
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public AuxiliaryTimingRole Role { get; set; }
    public bool Live { get; set; }
    public string OptionsJson { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public bool CleanStop { get; set; }
}
internal sealed class AuxiliaryRawPacketRow
{
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Protocol { get; set; } = "";
    public string Source { get; set; } = "";
    public string Stream { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
}
