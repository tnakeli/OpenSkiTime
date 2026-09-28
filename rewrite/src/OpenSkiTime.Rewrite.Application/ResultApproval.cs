using System.Security.Cryptography;
using System.Text.Json;

namespace OpenSkiTime.Rewrite.Application;

public sealed record ApprovedResult(Guid Id, Guid CompetitionId, int Revision, Guid FirstListId, Guid? SecondListId,
    string SourceFingerprint, DateTimeOffset ApprovedAt, string ApprovedBy, string XmlFileName, byte[] Xml,
    decimal CalculatedPenalty, decimal AppliedPenalty);

public sealed record ApproveResultRequest(Guid CompetitionId, Guid FirstListId, Guid? SecondListId,
    string SourceFingerprint, long ExpectedSeriesRevision, string ApprovedBy,
    string XmlFileName, byte[] Xml, decimal CalculatedPenalty, decimal AppliedPenalty);

public interface IResultStore
{
    Task<IReadOnlyList<ApprovedResult>> ReadApprovedResultsAsync(Guid competitionId, CancellationToken ct = default);
    Task<ApprovedResult> ApproveResultAsync(ApproveResultRequest request, CancellationToken ct = default);
}

public static class ResultSourceFingerprint
{
    public static string Create(TimingReplayData first, TimingReplayData? second = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        static object Canonical(TimingReplayData data) => new
        {
            data.List.Id, data.List.Revision, data.List.Plan,
            Sessions = data.Sessions.OrderBy(x => x.Id).ToArray(),
            Packets = data.Packets.OrderBy(x => x.SessionId).ThenBy(x => x.Sequence).ToArray(),
            Audit = data.Audit.OrderBy(x => x.Id).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { First = Canonical(first), Second = second is null ? null : Canonical(second) })));
    }
}
