using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class ImportExchangeTests
{
    private static readonly SeriesValues s_series = new("Levi", "Levi", "Club",
        new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26");
    private static readonly CompetitionValues s_race = new("Slalom", "SL", new DateOnly(2026, 1, 10),
        Discipline.Slalom, RaceType.Club, 2, 0);
    private static readonly CompetitorValues s_athlete = new("Mäkelä", "Aino", 2010, "FIN123",
        "FIN", "North\tClub \"A\"\nLane", Gender.Female);

    [Fact]
    public async Task PreviewAndCommitPreserveBlanksAndApplyAllFieldsAndEntries()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, s_series);
            series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var sl = Assert.Single(series.Competitions).Id;
            series = await workspace.SaveCompetitionAsync(null,
                s_race with { Name = "Giant slalom", ShortLabel = "GS" }, series.Revision);
            var gs = series.Competitions.Single(x => x.Id != sl).Id;
            var existing = await workspace.SaveDeskRowAsync(null, s_athlete, sl, true, 27, series.Revision);
            var source = "Surname\tFirst name\tYear\tGender\tClub\tFed ID\tSL\tBib:SL\tGS\r\n"
                + "Mäkelä\tAino\t2010\tFemale\t\tFIN123\t0\t~\tX\r\n"
                + "Korhonen\tKai\t2011\tMale\tNew Club\tNEW2\tX\t13\t\r\n";
            var before = await workspace.ReadCompetitorDeskAsync();
            var preview = TsvExchange.Preview(source, await workspace.ReadAsync(), before);
            Assert.Equal(2, preview.Rows.Count);
            Assert.Equal("North\tClub \"A\"\nLane", preview.Rows[0].Values.Club);
            Assert.Contains("Entry", preview.Rows[0].ChangedFields);
            Assert.True(preview.Rows[1].IsNew);
            Assert.Single(before.Competitors);
            var commit = new ImportCommit(preview.SeriesId, preview.Revision, preview.SourceHash,
                preview.Rows.Select(x => new ImportCommitRow(x.CompetitorId, x.Values, x.Entries)).ToArray());
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApplyImportAsync(commit with { SeriesId = Guid.NewGuid() }));
            var result = await workspace.ApplyImportAsync(commit);
            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.Updated);
            Assert.True((await workspace.ApplyImportAsync(commit)).AlreadyApplied);
            await workspace.CloseAsync();
            await workspace.OpenAsync(path);
            Assert.True((await workspace.ApplyImportAsync(commit)).AlreadyApplied);
            var saved = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(2, saved.Competitors.Count);
            Assert.Equal("North\tClub \"A\"\nLane", saved.Competitors.Single(x => x.Id == existing.Value.Id).Values.Club);
            var oldSl = saved.Participations.Single(x => x.CompetitorId == existing.Value.Id && x.CompetitionId == sl);
            Assert.False(oldSl.Participates);
            Assert.Null(oldSl.ImportedBib);
            Assert.True(saved.Participations.Single(x => x.CompetitorId == existing.Value.Id && x.CompetitionId == gs).Participates);
            var added = saved.Competitors.Single(x => x.Values.Surname == "KORHONEN");
            Assert.Equal("New Club", added.Values.Club);
            Assert.Equal(13, saved.Participations.Single(x => x.CompetitorId == added.Id && x.CompetitionId == sl).ImportedBib);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidSecondRowRollsBackAndExportQuotesRoundTrip()
    {
        var root = NewRoot();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "series.ost"), s_series);
            series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var race = Assert.Single(series.Competitions).Id;
            var existing = await workspace.SaveDeskRowAsync(null, s_athlete, race, false, 27, series.Revision);
            var desk = await workspace.ReadCompetitorDeskAsync();
            var exported = TsvExchange.Export(await workspace.ReadAsync(), desk, [existing.Value.Id]);
            var parsed = TsvExchange.Parse(exported);
            Assert.Equal("North\tClub \"A\"\nLane", parsed[1][5]);
            Assert.Equal(string.Empty, parsed[1][7]); // false participation exports blank
            Assert.Equal("27", parsed[1][8]);
            var importSource = "Surname\tFirst name\tYear\tSL\tBib:SL\n"
                + "Korhonen\tKai\t2011\tX\t12\n"
                + "Laine\tLea\t2012\tX\t27";
            var preview = TsvExchange.Preview(importSource, await workspace.ReadAsync(), desk);
            var commit = new ImportCommit(preview.SeriesId, preview.Revision, preview.SourceHash,
                preview.Rows.Select(x => new ImportCommitRow(x.CompetitorId, x.Values, x.Entries)).ToArray());
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplyImportAsync(commit));
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Assert.Equal(desk.Revision, (await workspace.ReadCompetitorDeskAsync()).Revision);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ApplyImportAsync(commit, canceled.Token));
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PreviewRejectsAmbiguousAndMalformedInputWithoutMutation()
    {
        var root = NewRoot();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "series.ost"), s_series);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(TsvExchange.Hash("Surname\nLaine"), TsvExchange.Hash("Surname\r\nLaine\r\n"));
            Assert.Throws<DomainValidationException>(() => TsvExchange.Parse("Surname\tClub\n\"Open quote\tClub"));
            Assert.Throws<DomainValidationException>(() => TsvExchange.Preview(
                "Surname\tFirst name\tYear\nLaine\tLea\t2012\nLaine\tLea\t2012", series, desk));
            Assert.Throws<DomainValidationException>(() => TsvExchange.Preview(
                "Surname\tYear\nLaine\tabc", series, desk));
            var names = TsvExchange.Preview("Name\tYear\nVAN DER POEL Jeroen\t2010\nLea Laine\t2011\nSMITH, John\t2012", series, desk);
            Assert.Equal("VAN DER POEL", names.Rows[0].Values.Surname);
            Assert.Equal("Jeroen", names.Rows[0].Values.FirstName);
            Assert.Equal("LAINE", names.Rows[1].Values.Surname);
            Assert.NotEmpty(names.Rows[1].Warnings);
            Assert.Equal("SMITH", names.Rows[2].Values.Surname);
            Assert.Empty(names.Rows[2].Warnings);
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HundredRowPasteCommitsAsOneRevisionAndCannotDuplicateOnRetry()
    {
        var root = NewRoot();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "series.ost"), s_series);
            series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            var rows = Enumerable.Range(1, 100).Select(n => $"Athlete{n}\tFirst{n}\t2010\tID{n}\tX");
            var source = "Surname\tFirst name\tYear\tFed ID\tSL\n" + string.Join('\n', rows);
            var preview = TsvExchange.Preview(source, series, await workspace.ReadCompetitorDeskAsync());
            var commit = new ImportCommit(preview.SeriesId, preview.Revision, preview.SourceHash,
                preview.Rows.Select(x => new ImportCommitRow(x.CompetitorId, x.Values, x.Entries)).ToArray());
            var applied = await workspace.ApplyImportAsync(commit);
            Assert.Equal(100, applied.Created);
            Assert.Equal(preview.Revision + 1, applied.Revision);
            Assert.True((await workspace.ApplyImportAsync(commit)).AlreadyApplied);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(100, desk.Competitors.Count);
            Assert.Equal(100, desk.Participations.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellationAfterFirstStagedRowRollsBackWholeImport()
    {
        var root = NewRoot();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "series.ost"), s_series);
            var source = "Surname\tFirst name\tYear\nLaine\tLea\t2010\nKorhonen\tKai\t2011";
            var preview = TsvExchange.Preview(source, series, await workspace.ReadCompetitorDeskAsync());
            var rows = preview.Rows.Select(x => new ImportCommitRow(x.CompetitorId, x.Values, x.Entries)).ToArray();
            using var cancellation = new CancellationTokenSource();
            var commit = new ImportCommit(preview.SeriesId, preview.Revision, preview.SourceHash,
                new CancelAfterFirstRowList(rows, cancellation));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ApplyImportAsync(commit, cancellation.Token));
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Assert.Equal(preview.Revision, (await workspace.ReadAsync()).Revision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CancelAfterFirstRowList(ImportCommitRow[] rows, CancellationTokenSource cancellation)
        : IReadOnlyList<ImportCommitRow>
    {
        public int Count => rows.Length;
        public ImportCommitRow this[int index] => rows[index];
        public IEnumerator<ImportCommitRow> GetEnumerator()
        {
            yield return rows[0];
            cancellation.Cancel();
            yield return rows[1];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-m3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
