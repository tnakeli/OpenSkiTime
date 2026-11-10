using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;

namespace OpenSkiTime.Persistence;

/// <summary>Read-only, paged projections over existing history. No schema changes or write pragmas.</summary>
public sealed class SqliteAuditTrailReader(ITimingDecoderFactory? decoderFactory = null) : IAuditTrailReader
{
    private sealed record Source(string Table, AuditArea Area, string From, string Time = "NULL",
        string CompetitionId = "NULL", string Run = "NULL", string Operator = "''", string Bib = "NULL",
        string Summary = "''", string Provenance = "Stored");
    private const string ListJoin = " JOIN StartLists l ON l.Id=t.ListId JOIN Runs r ON r.Id=l.RunId";
    private const string CaptureJoin = " JOIN TimingCaptures s ON s.Id=t.SessionId JOIN StartLists l ON l.Id=s.ListId JOIN Runs r ON r.Id=l.RunId";
    private const string AuxiliaryJoin = " JOIN AuxiliaryCaptures s ON s.Id=t.SessionId JOIN StartLists l ON l.Id=s.ListId JOIN Runs r ON r.Id=l.RunId";
    private const string AuditBibSql = """
        COALESCE(json_extract(CASE WHEN json_valid(t.AfterJson) THEN t.AfterJson ELSE '{}' END,'$.Bib'),
          json_extract(CASE WHEN json_valid(t.BeforeJson) THEN t.BeforeJson ELSE '{}' END,'$.Bib'),
          (SELECT e.Bib FROM StartListEntries e WHERE e.ListId=t.ListId AND e.CompetitorId=upper(COALESCE(
            json_extract(CASE WHEN json_valid(t.AfterJson) THEN t.AfterJson ELSE '{}' END,'$.CompetitorId'),
            json_extract(CASE WHEN json_valid(t.BeforeJson) THEN t.BeforeJson ELSE '{}' END,'$.CompetitorId'))) LIMIT 1))
        """;
    private static readonly Source[] s_sources =
    [
        new("TimingAudit", AuditArea.Events, "TimingAudit t" + ListJoin, "t.At", "r.CompetitionId", "r.Number", "t.Operator",
            AuditBibSql, "t.Reason"),
        new("StartLists", AuditArea.Events, "StartLists t JOIN Runs r ON r.Id=t.RunId", "t.CreatedAt", "r.CompetitionId", "r.Number", "t.Operator", Summary: "'Revision ' || t.Revision || ' · ' || t.Reason"),
        new("TimingCaptures", AuditArea.Events, "TimingCaptures t" + ListJoin, "t.StartedAt", "r.CompetitionId", "r.Number", Summary: "CASE WHEN t.StoppedAt IS NULL THEN 'No recorded stop' WHEN t.CleanStop=1 THEN 'Clean stop' ELSE 'Unclean stop' END"),
        new("AuxiliaryCaptures", AuditArea.Events, "AuxiliaryCaptures t" + ListJoin, "t.StartedAt", "r.CompetitionId", "r.Number", Summary: "t.Role || ' · ' || CASE WHEN t.StoppedAt IS NULL THEN 'No recorded stop' WHEN t.CleanStop=1 THEN 'Clean stop' ELSE 'Unclean stop' END"),
        new("RaceInformation", AuditArea.Events, "RaceInformation t", "t.SavedAt", "t.CompetitionId", Summary: "'Revision ' || t.Revision"),
        new("ApprovedResults", AuditArea.Events, "ApprovedResults t", "t.ApprovedAt", "t.CompetitionId", Operator: "t.ApprovedBy", Summary: "'Approved XML revision ' || t.Revision"),
        new("TimingReports", AuditArea.Events, "TimingReports t", "t.SavedAt", "t.CompetitionId", Operator: "t.Operator", Summary: "'Revision ' || t.Revision || ' · ' || t.Reason"),
        new("ApprovedTimingReports", AuditArea.Events, "ApprovedTimingReports t", "t.ApprovedAt", "t.CompetitionId", Operator: "t.ApprovedBy", Summary: "'Approved draft ' || t.DraftRevision || ' · revision ' || t.Revision"),
        new("TimingReportSubmissions", AuditArea.Events, "TimingReportSubmissions t JOIN ApprovedTimingReports a ON a.Id=t.ApprovalId", "t.At", "a.CompetitionId", Summary: "t.Status"),
        new("TimingReportImages", AuditArea.Events, "TimingReportImages t", "t.ImportedAt", "t.CompetitionId", "t.RunNumber", Summary: "t.Engine || ' · ' || length(t.Bytes) || ' bytes'"),
        new("RawTimingPackets", AuditArea.RawTiming, "RawTimingPackets t" + CaptureJoin, "t.ReceivedAt", "r.CompetitionId", "r.Number", Summary: "t.Protocol || ' · sequence ' || t.Sequence || ' · ' || length(t.Bytes) || ' bytes'"),
        new("AuxiliaryRawPackets", AuditArea.RawTiming, "AuxiliaryRawPackets t" + AuxiliaryJoin, "t.ReceivedAt", "r.CompetitionId", "r.Number", Summary: "s.Role || ' · ' || t.Protocol || ' · sequence ' || t.Sequence || ' · ' || length(t.Bytes) || ' bytes'"),
        new("Series", AuditArea.CurrentState, "Series t", Summary: "'Series revision ' || t.Revision", Provenance: "Current state"),
        new("Competitions", AuditArea.CurrentState, "Competitions t", CompetitionId: "t.Id", Summary: "t.ShortLabel", Provenance: "Current state"),
        new("Competitors", AuditArea.CurrentState, "Competitors t", Summary: "'Registration ' || t.Id", Provenance: "Current state"),
        new("Participations", AuditArea.CurrentState, "Participations t", CompetitionId: "t.CompetitionId", Bib: "t.ImportedBib", Summary: "t.CompetitorId", Provenance: "Current state"),
        new("CategoryRules", AuditArea.CurrentState, "CategoryRules t", Summary: "t.Label", Provenance: "Current state"),
        new("ImportReceipts", AuditArea.CurrentState, "ImportReceipts t", Summary: "'Revision ' || t.Revision || ' · created ' || t.Created || ' · updated ' || t.Updated", Provenance: "Stored; time not recorded"),
        new("StartListEntries", AuditArea.CurrentState, "StartListEntries t" + ListJoin, CompetitionId: "r.CompetitionId", Run: "r.Number", Bib: "t.Bib", Summary: "'Saved position ' || t.Position", Provenance: "Stored snapshot"),
        new("Runs", AuditArea.CurrentState, "Runs t", CompetitionId: "t.CompetitionId", Run: "t.Number", Provenance: "Current state"),
        new("CompetitorFisPoints", AuditArea.CurrentState, "CompetitorFisPoints t", Summary: "t.CompetitorId", Provenance: "Current state"),
        new("FisPointsLists", AuditArea.CurrentState, "FisPointsLists t", Summary: "t.Code", Provenance: "Stored context"),
        new("PdfFactory", AuditArea.CurrentState, "PdfFactory t", Summary: "t.Id", Provenance: "Latest state; previous values not retained")
    ];
    private static readonly JsonSerializerOptions s_pretty = new() { WriteIndented = true };
    private static string ListSql(string table) => table switch
    {
        "TimingAudit" or "TimingCaptures" or "AuxiliaryCaptures" or "StartListEntries" => "t.ListId",
        "StartLists" => "t.Id",
        "RawTimingPackets" or "AuxiliaryRawPackets" => "s.ListId",
        _ => "NULL"
    };
    private static string IdentitySql(string table) => table switch
    {
        "RawTimingPackets" or "AuxiliaryRawPackets" => "t.SessionId || '/' || t.Sequence",
        "RaceInformation" or "TimingReports" => "t.CompetitionId || '/' || t.Revision",
        "Participations" => "t.CompetitorId || '/' || t.CompetitionId",
        "ImportReceipts" => "t.SeriesId || '/' || t.SourceHash",
        "StartListEntries" => "t.ListId || '/' || t.Position",
        "CompetitorFisPoints" => "t.CompetitorId",
        "FisPointsLists" => "t.Code",
        _ => "CAST(t.Id AS TEXT)"
    };

    public IReadOnlyList<string> Sources(AuditArea area) => s_sources.Where(s => s.Area == area).Select(s => s.Table).ToArray();

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection(SqliteSeriesFileStore.ConnectionString(Path.GetFullPath(path), SqliteOpenMode.ReadOnly));
        try
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT FormatId FROM Series";
            if (!Equals(await command.ExecuteScalarAsync(ct), SeriesDbContext.Format))
            { throw new SeriesFileException("This development file uses an incompatible format. Inspection never upgrades files."); }
            connection.CreateFunction("audit_ticks", (string? value) => ParseTime(value)?.UtcTicks ?? long.MinValue, isDeterministic: true);
            connection.CreateFunction("audit_contains", (string? value, string search) => (value ?? "").Contains(search, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static DateTimeOffset? ParseTime(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) ? time : null;

    private static async Task<HashSet<string>> TablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) { tables.Add(reader.GetString(0)); }
        return tables;
    }

    public async Task<AuditPage> ReadAsync(string path, AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > 250 || !Enum.IsDefined(query.Area) || query.Search.Length > 500 || query.Operator.Length > 200)
        { throw new ArgumentException("Invalid audit query.", nameof(query)); }
        await using var connection = await OpenAsync(path, ct);
        var tables = await TablesAsync(connection, ct);
        var sources = s_sources.Where(s => s.Area == query.Area && tables.Contains(s.Table) && (query.Source is null || s.Table == query.Source)).ToArray();
        if (sources.Length == 0) { return new([], null); }
        var selects = sources.Select(s => $"SELECT '{s.Table}' Source,t.rowid RowId,{s.Time} At,audit_ticks({s.Time}) SortAt,{s.CompetitionId} CompetitionId,{s.Run} Run,{s.Operator} Operator,{s.Bib} Bib,{s.Summary} Summary,'{s.Provenance}' Provenance,{IdentitySql(s.Table)} StoredKey,{ListSql(s.Table)} ListId FROM {s.From}");
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            WITH records AS ({{string.Join(" UNION ALL ", selects)}}), named AS (
              SELECT x.*,COALESCE(c.ShortLabel,'') Competition,
                COALESCE(p.Surname,json_extract(CASE WHEN json_valid(e.EntryJson) THEN e.EntryJson ELSE '{}' END,'$.Entrant.Athlete.Surname'),'') Surname,
                COALESCE(p.FirstName,json_extract(CASE WHEN json_valid(e.EntryJson) THEN e.EntryJson ELSE '{}' END,'$.Entrant.Athlete.FirstName'),'') FirstName
              FROM records x LEFT JOIN Competitions c ON c.Id=x.CompetitionId
              LEFT JOIN StartListEntries e ON e.ListId=x.ListId AND e.Bib=x.Bib
              LEFT JOIN Competitors p ON x.Source='Competitors' AND p.rowid=x.RowId AND p.Id=x.StoredKey)
            SELECT x.Source,x.RowId,x.At,x.SortAt,x.Competition,x.Run,x.Operator,x.Bib,x.Summary,x.Provenance,x.StoredKey,x.Surname,x.FirstName
            FROM named x
            WHERE ($competition IS NULL OR x.CompetitionId=$competition) AND ($run IS NULL OR x.Run=$run)
              AND ($bib IS NULL OR x.Bib=$bib) AND audit_contains(x.Operator,$operator)
              AND audit_contains(COALESCE(x.Summary,'') || ' ' || x.Source || ' ' || x.Competition || ' ' || x.Surname || ' ' || x.FirstName,$search)
              AND ($from IS NULL OR x.SortAt >= $from) AND ($until IS NULL OR x.SortAt <= $until)
              AND ($after=0 OR x.SortAt<$time OR (x.SortAt=$time AND (x.Source>$source OR (x.Source=$source AND x.RowId>$row))))
            ORDER BY x.SortAt DESC,x.Source,x.RowId LIMIT $limit
            """;
        command.Parameters.AddWithValue("$competition", query.CompetitionId is { } id ? id.ToString().ToUpperInvariant() : DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)query.Run ?? DBNull.Value);
        command.Parameters.AddWithValue("$bib", (object?)query.Bib ?? DBNull.Value);
        command.Parameters.AddWithValue("$operator", query.Operator.Trim());
        command.Parameters.AddWithValue("$search", query.Search.Trim());
        command.Parameters.AddWithValue("$from", (object?)query.From?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$until", (object?)query.Until?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", query.After is null ? 0 : 1);
        command.Parameters.AddWithValue("$time", query.After?.TimeTicks ?? 0);
        command.Parameters.AddWithValue("$source", query.After?.Source ?? "");
        command.Parameters.AddWithValue("$row", query.After?.RowId ?? 0);
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);
        using var reader = await command.ExecuteReaderAsync(ct);
        var records = new List<AuditRecord>();
        while (await reader.ReadAsync(ct))
        {
            records.Add(new(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(10)), reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? "" : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? "" : reader.GetString(8), reader.GetString(9), reader.GetInt64(3), reader.GetString(11), reader.GetString(12)));
        }
        var hasMore = records.Count > query.PageSize;
        if (hasMore) { records.RemoveAt(records.Count - 1); }
        var last = records.LastOrDefault();
        return new(records, hasMore && last is not null ? new(last.TimeTicks, last.Reference.Source, last.Reference.RowId) : null);
    }

    public async Task<AuditDetail> ReadDetailAsync(string path, AuditReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var source = s_sources.SingleOrDefault(s => s.Table == reference.Source) ?? throw new ArgumentException("Unknown audit source.", nameof(reference));
        await using var connection = await OpenAsync(path, ct);
        using var command = connection.CreateCommand();
        // One selected record only. Blobs are capped in the preview; exact originals stay in the .ost.
        command.CommandText = $"SELECT * FROM {source.Table} t WHERE t.rowid=$id AND ($key IS NULL OR {IdentitySql(source.Table)}=$key)";
        command.Parameters.AddWithValue("$id", reference.RowId);
        command.Parameters.AddWithValue("$key", (object?)reference.StoredKey ?? DBNull.Value);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) { return new("Record no longer available. Refresh the view.", []); }
        var text = new StringBuilder().AppendLine(source.Table + " · " + source.Provenance);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            text.AppendLine().Append(name).AppendLine(":");
            if (reader.IsDBNull(i)) { text.AppendLine("Not recorded"); continue; }
            if (reader.GetFieldType(i) == typeof(byte[]))
            {
                var length = reader.GetBytes(i, 0, null, 0, 0);
                if (length > 1_048_576) { text.AppendLine(CultureInfo.InvariantCulture, $"{length} bytes; preview omitted. Include the complete series to retain this evidence."); continue; }
                var bytes = (byte[])reader.GetValue(i);
                text.AppendLine(CultureInfo.InvariantCulture, $"{length} bytes").AppendLine("Base64: " + Convert.ToBase64String(bytes));
                if (name == "Xml") { text.AppendLine(Encoding.UTF8.GetString(bytes)); }
                else { text.AppendLine("Hex: " + Convert.ToHexString(bytes)).AppendLine("Escaped UTF-8: " + JsonSerializer.Serialize(Encoding.UTF8.GetString(bytes))); }
                continue;
            }
            var value = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            values[name] = value;
            try { using var json = JsonDocument.Parse(value); text.AppendLine(JsonSerializer.Serialize(json.RootElement, s_pretty)); }
            catch (JsonException) { text.AppendLine(value); }
        }
        reader.Close();
        if (values.TryGetValue("BeforeJson", out var before) && values.TryGetValue("AfterJson", out var after))
        {
            try
            {
                using var oldValue = JsonDocument.Parse(before); using var newValue = JsonDocument.Parse(after);
                text.AppendLine().AppendLine("Decision changes:");
                foreach (var field in oldValue.RootElement.EnumerateObject().Select(p => p.Name)
                    .Concat(newValue.RootElement.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal))
                {
                    var oldField = oldValue.RootElement.TryGetProperty(field, out var oldItem) ? oldItem.GetRawText() : "Not recorded";
                    var newField = newValue.RootElement.TryGetProperty(field, out var newItem) ? newItem.GetRawText() : "Not recorded";
                    if (oldField != newField) { text.Append(field).Append(": ").Append(oldField).Append(" → ").AppendLine(newField); }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { text.AppendLine("Change summary unavailable; original before/after text is retained above."); }
        }
        var links = new List<AuditLink>();
        foreach (var (field, table, column) in new[] { ("ListId", "StartLists", "Id"), ("FirstListId", "StartLists", "Id"),
            ("SecondListId", "StartLists", "Id"), ("SessionId", source.Table == "AuxiliaryRawPackets" ? "AuxiliaryCaptures" : "TimingCaptures", "Id"),
            ("ReversesId", "TimingAudit", "Id"), ("ApprovalId", "ApprovedTimingReports", "Id") })
        {
            if (!values.TryGetValue(field, out var value)) { continue; }
            command.Parameters.Clear(); command.CommandText = $"SELECT rowid FROM {table} WHERE {column}=$value";
            command.Parameters.AddWithValue("$value", value);
            if (await command.ExecuteScalarAsync(ct) is long row) { links.Add(new(field, new(table, row))); }
        }
        // Observation keys encode their original session and packet sequence. Resolve only that defined shape.
        foreach (var value in values.Values.Where(v => v.StartsWith('{') || v.StartsWith('[')))
        {
            try
            {
                using var json = JsonDocument.Parse(value);
                foreach (var key in JsonStrings(json.RootElement).Distinct(StringComparer.Ordinal))
                {
                    var parts = key.Split(':');
                    if (parts.Length < 3 || !Guid.TryParseExact(parts[0], "N", out var session)
                        || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) { continue; }
                    foreach (var table in new[] { "RawTimingPackets", "AuxiliaryRawPackets" })
                    {
                        command.Parameters.Clear(); command.CommandText = $"SELECT rowid FROM {table} WHERE SessionId=$session AND Sequence=$sequence";
                        command.Parameters.AddWithValue("$session", session.ToString().ToUpperInvariant());
                        command.Parameters.AddWithValue("$sequence", sequence);
                        if (await command.ExecuteScalarAsync(ct) is long row) { links.Add(new("Source packet " + key, new(table, row))); }
                    }
                }
            }
            catch (JsonException) { /* Preserve malformed original JSON above; no inferred references. */ }
        }
        if (decoderFactory is not null && source.Area == AuditArea.RawTiming)
        { await AppendDecodedAsync(connection, source, values, text, ct); }
        return new(text.ToString(), links.DistinctBy(l => l.Reference).ToArray());
    }

    private static IEnumerable<string> JsonStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { yield return value.GetString()!; }
        else if (value.ValueKind == JsonValueKind.Object)
        { foreach (var property in value.EnumerateObject()) { foreach (var item in JsonStrings(property.Value)) { yield return item; } } }
        else if (value.ValueKind == JsonValueKind.Array)
        { foreach (var child in value.EnumerateArray()) { foreach (var item in JsonStrings(child)) { yield return item; } } }
    }

    private async Task AppendDecodedAsync(SqliteConnection connection, Source source, Dictionary<string, string> values, StringBuilder text, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var captureTable = source.Table == "AuxiliaryRawPackets" ? "AuxiliaryCaptures" : "TimingCaptures";
        command.CommandText = $"SELECT Id,ListId,OptionsJson,StartedAt,StoppedAt,CleanStop FROM {captureTable} WHERE Id=$session";
        command.Parameters.AddWithValue("$session", values["SessionId"]);
        CaptureSession session;
        using (var sessionReader = await command.ExecuteReaderAsync(ct))
        {
            if (!await sessionReader.ReadAsync(ct)) { return; }
            try
            {
                session = new(sessionReader.GetGuid(0), sessionReader.GetGuid(1), JsonSerializer.Deserialize<CaptureOptions>(sessionReader.GetString(2))!,
                    ParseTime(sessionReader.GetString(3))!.Value, sessionReader.IsDBNull(4) ? null : ParseTime(sessionReader.GetString(4)), sessionReader.GetBoolean(5));
            }
            catch (JsonException) { text.AppendLine("Decoding unavailable: malformed capture options. Original data retained."); return; }
        }
        var sequence = long.Parse(values["Sequence"], CultureInfo.InvariantCulture);
        command.CommandText = $"SELECT Sequence,ReceivedAt,Protocol,Source,Stream,Bytes FROM {source.Table} WHERE SessionId=$session AND Sequence<=$sequence ORDER BY Sequence";
        command.Parameters.AddWithValue("$sequence", sequence);
        using var packets = await command.ExecuteReaderAsync(ct);
        var decoders = new Dictionary<(string, string, string), ITimingDecoder>();
        text.AppendLine().AppendLine("Derived by replay through this packet (integer ticks are 100 ns units; receive time is not race time):");
        while (await packets.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var packet = new RawTimingPacket(session.Id, packets.GetInt64(0), ParseTime(packets.GetString(1))!.Value,
                packets.GetString(2), packets.GetString(3), packets.GetString(4), (byte[])packets.GetValue(5));
            var key = (packet.Protocol, packet.Source, packet.Stream);
            try
            {
                if (!decoders.TryGetValue(key, out var decoder))
                { decoder = decoderFactory!.Create(session, packet.Protocol, packet.Source, packet.Stream); decoders.Add(key, decoder); }
                var observations = decoder.Feed(packet);
                foreach (var observation in observations.Where(o => packet.Sequence == sequence || o.PacketSequence == sequence))
                { text.AppendLine(JsonSerializer.Serialize(observation, s_pretty)); }
                if (packet.Sequence == sequence && decoder.HasPendingInput)
                { text.AppendLine("Incomplete input at this boundary; a later packet may complete the message."); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            { text.AppendLine("Decoder could not interpret this input. Original bytes are retained."); }
        }
    }

    public async Task<AuditSeriesSummary> ReadSummaryAsync(string path, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(path, ct);
        var tables = await TablesAsync(connection, ct);
        using var transaction = connection.BeginTransaction(deferred: true);
        var counts = new List<AuditTableCount>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var source in s_sources.Where(s => tables.Contains(s.Table)))
        {
            command.CommandText = $"SELECT count(*) FROM {source.Table}";
            counts.Add(new(source.Table, (long)(await command.ExecuteScalarAsync(ct))!));
        }
        command.CommandText = "SELECT Revision,Id FROM Series";
        using var result = await command.ExecuteReaderAsync(ct);
        await result.ReadAsync(ct);
        return new(SeriesDbContext.Format, result.GetInt64(0), counts, result.GetGuid(1));
    }

    public async Task CreateSupportSnapshotAsync(string path, string destination, CancellationToken ct = default)
    {
        // An independent capture lease prevents a concurrent capture start in this or another process.
        await using var check = await OpenAsync(path, ct);
        FileStream lease;
        try { lease = new(path + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new SeriesFileException("Disconnect and drain A, B and hand timing before including the complete series. Diagnostics can still be exported.", ex); }
        await using (lease)
        {
            ct.ThrowIfCancellationRequested();
            await SqliteSeriesFileStore.BackupFileAsync(path, destination, ct);
        }
    }
}
