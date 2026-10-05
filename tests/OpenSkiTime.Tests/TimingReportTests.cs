using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingReportTests
{
    [Fact]
    public void XmlPreservesPrecisionAndOmitsUnrequestedSupportSystems()
    {
        var draft = ValidDraft();
        draft = draft with { Defaults = draft.Defaults with { ChiefOfTiming = new("Test", "Chief", "FIN", Number: "123") } };
        var bytes = TimingReportXml.Create(draft, "1.2.3");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        Assert.Equal("TR", xml.Descendants("Type").Single().Value);
        Assert.Equal("AL", xml.Descendants("Raceheader").Single().Attribute("Sector")!.Value);
        Assert.Equal("1.17", xml.Descendants("XMLversion").Single().Value);
        Assert.Equal("OpenSkiTime", xml.Descendants("Software").Single().Element("Brand")!.Value);
        Assert.Null(xml.Descendants("Jury").Single(x => x.Attribute("Function")!.Value == "CHIEFOFTIMING").Element("Number"));
        Assert.Equal("12:00:00.1234567", xml.Descendants("Bibfirst").Single().Elements("Start").First().Value);
        Assert.Equal("12:00:00.12", xml.Descendants("Bibfirst").Single().Elements("Start").Last().Value);
        Assert.Equal("1:00.00", xml.Descendants("Net").First().Value);
        Assert.Equal("yes", xml.Descendants("Allresults").Single().Attribute("SystemA")!.Value);
        Assert.Empty(xml.Descendants("Photofinish")); Assert.Empty(xml.Descendants("Transponder"));
        Assert.Equal("FIN9991.xml", TimingReportXml.FileName(draft));
        Assert.Equal(bytes, TimingReportXml.Create(draft, "1.2.3"));
        // Approved bytes are the same on every operating system: line feeds only, as in the result XML.
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Contains((byte)'\n', bytes);
    }

    [Fact]
    public void ApprovalValidationRequiresReviewedEvidenceAndIndependentBackupEvenWithoutConnection()
    {
        var draft = ValidDraft();
        Assert.Empty(TimingReportXml.Validate(draft));
        var run = draft.Runs[0];
        var incomplete = draft with { Runs = [run with { First = run.First with { BStart = null, HandFinish = run.First.HandFinish! with { Verified = false } } }] };
        Assert.Contains(TimingReportXml.Validate(incomplete), x => x.Contains("B start is missing", StringComparison.Ordinal));
        Assert.Contains(TimingReportXml.Validate(incomplete), x => x.Contains("must be verified", StringComparison.Ordinal));
        Assert.Throws<DomainValidationException>(() => TimingReportXml.Create(incomplete, "test"));
        Assert.Throws<DomainValidationException>(() => TimingReportXml.Create(draft with { CertifyFis = false }, "test"));
        Assert.Throws<DomainValidationException>(() => TimingReportXml.Create(draft with { Reviewed = false }, "test"));
    }

    [Fact]
    public void EquipmentHomologationUsesReportSeasonRatherThanCurrentDate()
    {
        var draft = ValidDraft();
        var expires = draft.Defaults.TimerA with { ValidUntilSeason = 2026 };
        var expired = draft with { Defaults = draft.Defaults with { TimerA = expires } };
        Assert.Contains(TimingReportXml.Validate(expired), x => x.Contains("homologation expired", StringComparison.Ordinal));
        Assert.DoesNotContain(TimingReportXml.Validate(expired with { Header = expired.Header with { Season = 2026 } }), x => x.Contains("homologation expired", StringComparison.Ordinal));
    }

    [Fact]
    public void SyncChecksUseExactMillisecondAndIncludeSeparateStartTimers()
    {
        var draft = ValidDraft();
        Assert.Empty(TimingReportXml.Validate(draft with { SyncCheckB = draft.SyncCheckA! with { Ticks = draft.SyncCheckA.Ticks + 10_000 } }));
        Assert.Contains(TimingReportXml.Validate(draft with { SyncCheckB = draft.SyncCheckA! with { Ticks = draft.SyncCheckA.Ticks + 10_001 } }),
            x => x.Contains("0.001", StringComparison.Ordinal));
        var separate = draft with { Defaults = draft.Defaults with { TimerStartA = draft.Defaults.TimerA } };
        Assert.Contains(TimingReportXml.Validate(separate), x => x.Contains("Start timer A synchronization", StringComparison.Ordinal));
        separate = separate with { SyncCheckAStart = draft.SyncCheckA };
        Assert.Contains("System=\"AStart\"", Encoding.UTF8.GetString(TimingReportXml.Create(separate, "test")), StringComparison.Ordinal);
        Assert.Contains(TimingReportXml.Validate(draft with { SyncCheckA = draft.Sync! with { Ticks = draft.Sync.Ticks + TimeSpan.TicksPerSecond } }),
            x => x.Contains("one minute", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectionUsesFinishOrderAndFastestClassifiedTimeIncludingReplacements()
    {
        var date = new DateOnly(2026, 10, 3);
        var competition = new CompetitionValues("Test", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991");
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), competition, Gender.Male,
            [new(Guid.NewGuid(), new("ALPHA", "A", 2000, "900001", "FIN", "Club", Gender.Male), 10),
             new(Guid.NewGuid(), new("BETA", "B", 2000, "900002", "FIN", "Club", Gender.Male), 20)],
            new("1327", date, date), new(), "test");
        var list = new StartListRevision(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, null, "op", "test", plan);
        TimingObservation O(string key, long seconds, bool manual = false) => new(key, Guid.Empty, 1, "synthetic", key,
            ObservationKind.Impulse, 0, seconds * TimeSpan.TicksPerSecond, 4, null, manual, "clock", "");
        var observations = new[] { O("s1", 100), O("f1", 200), O("s2", 110), O("f2", 190, true) };
        var results = new[] { new TimingResult(plan.Entries[0], TimingStatus.Finished, 10000, 2, "s1", "f1", ""),
            new TimingResult(plan.Entries[1], TimingStatus.Finished, 8000, 1, "s2", "f2", "") };
        var snapshot = new TimingSnapshot(list.Id, 0, results, observations.Select(x => new ObservationReview(x, null, false, null, "Assigned")).ToArray(), []);
        var projected = TimingReportProjection.FromTiming(new(list, [], [], []), snapshot);
        Assert.Equal(plan.Entries[1].Bib, projected.First.Bib);
        Assert.Equal(plan.Entries[0].Bib, projected.Last.Bib);
        Assert.Equal(plan.Entries[1].Bib, projected.BestBib); Assert.Equal(8000, projected.BestHundredths);
        Assert.False(projected.AllResultsA); Assert.Single(projected.MissedA);
        var electronic = observations.Select(x => new ObservationReview(x with { Manual = false }, null, false, null, "Assigned")).ToArray();
        var competitor = results[1].CompetitorId;
        var before = new TimingDecision(DecisionKind.Time, CompetitorId: competitor);
        var after = before with { Hundredths = 7000 };
        var correction = new TimingAudit(1, list.Id, DateTimeOffset.UnixEpoch, "op", "Backup correction", before, after);
        var correctedSnapshot = snapshot with { Observations = electronic, Audit = [correction], Results = [results[0], results[1] with { Hundredths = 7000 }] };
        var corrected = TimingReportProjection.FromTiming(new(list, [], [], [correction]), correctedSnapshot);
        Assert.Equal(results[1].Bib, corrected.BestBib); Assert.Equal(7000, corrected.BestHundredths); Assert.False(corrected.AllResultsA); Assert.Equal(results[1].Bib, Assert.Single(corrected.MissedA).Bib);
        var undo = new TimingAudit(2, list.Id, DateTimeOffset.UnixEpoch.AddSeconds(1), "op", "Undo correction", after, before, 1);
        var restored = TimingReportProjection.FromTiming(new(list, [], [], [correction, undo]), snapshot with { Observations = electronic, Audit = [correction, undo] });
        Assert.Equal(results[1].Bib, restored.BestBib); Assert.True(restored.AllResultsA); Assert.Empty(restored.MissedA);
        var classified = snapshot with { Observations = electronic, Results = [results[0], results[1] with { Status = TimingStatus.DSQ, Hundredths = null }] };
        var dsq = TimingReportProjection.FromTiming(new(list, [], [], []), classified);
        Assert.Equal(results[1].Bib, dsq.First.Bib); Assert.Equal(8000, dsq.First.NetHundredths);
        Assert.Equal(results[0].Bib, dsq.BestBib); Assert.True(dsq.AllResultsA);
        var correctedDsq = TimingReportProjection.FromTiming(new(list, [], [], [correction]), classified with { Audit = [correction] });
        Assert.Equal(7000, correctedDsq.First.NetHundredths); Assert.False(correctedDsq.AllResultsA);
        Assert.Equal(results[1].Bib, Assert.Single(correctedDsq.MissedA).Bib);
        Assert.Null(classified.Results[1].Hundredths); // Sampling never restores an official DSQ time.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportImagesAuditAndApprovedBytesSurviveBackupAndRejectStaleApproval(bool simulatedCapture)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-timing-report", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 10, 3);
            var series = await workspace.CreateAsync(Path.Combine(folder, "race.ost"), new("Test", "Ruka", "Club", date, date, "FIN", "2026-27"),
                [new("Downhill", "DH", date, Discipline.Downhill, RaceType.Fis, 1, 0, "9991")]);
            var competition = series.Competitions[0];
            var athlete = new CompetitorValues("SYNTHETIC", "Racer", 2000, "900001", "FIN", "Club", Gender.Male);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, series.Revision);
            var plan = FisStartOrder.FirstRun(competition.Id, competition.Values, Gender.Male, [new(saved.Value.Id, athlete, 10)],
                new("1327", date, date), new(), "test");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, saved.Revision, "op", "test", DateTimeOffset.UtcNow))).Revisions);
            await workspace.Timing!.SelectRunAsync(list.Id);
            var simulator = new SimulatorTimingSource();
            // This synthetic protocol fixture exercises the real-source approval path without hardware.
            await workspace.Timing.StartAsync(simulator, new("Synthetic fixture", "Test", date, Simulation: simulatedCapture), "op");
            var auxiliarySources = new Dictionary<AuxiliaryTimingRole, SimulatorTimingSource>();
            foreach (var role in Enum.GetValues<AuxiliaryTimingRole>())
            {
                var source = new SimulatorTimingSource(); auxiliarySources.Add(role, source);
                await workspace.Auxiliary!.StartAsync(list.Id, role, source, new("Synthetic auxiliary fixture", "Test " + role, date), "op");
            }
            var image = new TimingReportImage(Guid.NewGuid(), competition.Id, TimingReportImageRole.B, "synthetic.png", "image/png", [1, 2, 3], "12:00:00.1234", "test", DateTimeOffset.UtcNow)
                { RunNumber = 1, DeviceLabel = "Backup" };
            var readyImage = image with { Id = Guid.NewGuid(), FileName = "ready.png" };
            await workspace.SaveTimingReportImagesAsync([readyImage], (await workspace.ReadAsync()).Revision);
            Assert.Equal(readyImage.Id, Assert.Single(await workspace.ReadTimingReportImagesAsync(competition.Id)).Id);
            Assert.Equal(TimingStatus.Ready, workspace.Timing.Snapshot!.Results[0].Status);
            await workspace.Timing.ArmAsync(list.Plan.Entries[0].Bib, null);
            await simulator.PulseAsync(0, TimeSpan.FromHours(12).Ticks);
            await Until(() => workspace.Timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
            await workspace.Timing.ArmAsync(null, list.Plan.Entries[0].Bib);
            var onCourseImage = image with { Id = Guid.NewGuid(), FileName = "on-course.png", Bytes = new byte[1_000_000] };
            var archival = workspace.SaveTimingReportImagesAsync([onCourseImage], (await workspace.ReadAsync()).Revision);
            // Receive a finish impulse while a moderate image batch is being archived.
            await simulator.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.TicksPerMinute);
            await archival;
            await Until(() => workspace.Timing.Snapshot!.Results[0].Status == TimingStatus.Finished);
            await workspace.SaveTimingReportImagesAsync([image], (await workspace.ReadAsync()).Revision);
            Assert.Equal(3, (await workspace.ReadTimingReportImagesAsync(competition.Id)).Count);
            Assert.True(workspace.Timing.IsActive);
            Assert.All(auxiliarySources.Keys, role => Assert.True(workspace.Auxiliary!.State(role).IsActive));
            // Archival must not disconnect or replace any source. New packets remain durable.
            await simulator.PulseAsync(0, TimeSpan.FromHours(12).Ticks + 2 * TimeSpan.TicksPerMinute);
            foreach (var (role, source) in auxiliarySources)
            {
                await source.PulseAsync(role == AuxiliaryTimingRole.HandFinish ? 1 : 0, TimeSpan.FromHours(12).Ticks + 2 * TimeSpan.TicksPerMinute);
            }
            await Until(() => workspace.Timing.SavedPackets == 3 && auxiliarySources.Keys.All(role => workspace.Auxiliary!.State(role).SavedPackets == 1));
            Assert.Equal(3, (await workspace.ReadTimingAsync(list.Id)).Packets.Count);
            Assert.Equal(3, (await workspace.Auxiliary!.ReadAsync(list.Id)).Packets.Count);
            Assert.Equal(6000, workspace.Timing.Snapshot!.Results[0].Hundredths);
            var postRacePulse = Assert.Single(workspace.Timing.Snapshot.Observations, x => x.State == "Unassigned");
            await workspace.Timing.CorrectAsync(new(DecisionKind.Assignment, postRacePulse.Observation.Key, Ignored: true), "op", "Verified post-race test pulse");
            var data = await workspace.ReadTimingAsync(list.Id);
            Assert.Equal(Encoding.ASCII.GetBytes(" 0002 C1 12:01:00.0000000\r"), data.Packets[1].Bytes);
            var draft = ValidDraft() with { CompetitionId = competition.Id, SourceFingerprint = TimingReportProjection.Fingerprint([data]) };
            draft = draft with { Header = draft.Header with { Discipline = "DH" } };
            var projected = TimingReportProjection.FromTiming(data, TimingReplay.Restore(data, new AlgeDecoderFactory()));
            var backup = draft.Runs[0].First;
            TimingReportBib Sample(TimingReportBib value) => value with
            {
                BStart = backup.BStart! with { SourceReference = "manual" }, BFinish = backup.BFinish! with { SourceReference = "manual" },
                HandStart = backup.HandStart! with { SourceReference = "manual" }, HandFinish = backup.HandFinish! with { SourceReference = "manual" }
            };
            projected = projected with { First = Sample(projected.First), Last = Sample(projected.Last) };
            draft = draft with { Runs = [projected], Associations = [
                new(1, projected.First.Bib!.Value, 0, TimingReportImageRole.B, projected.First.BStart!),
                new(1, projected.First.Bib.Value, 1, TimingReportImageRole.B, projected.First.BFinish!),
                new(1, projected.First.Bib.Value, 0, TimingReportImageRole.HandStart, projected.First.HandStart!),
                new(1, projected.First.Bib.Value, 1, TimingReportImageRole.HandFinish, projected.First.HandFinish!)] };
            var imageStamp = new TimingReportStamp(date.ToDateTime(TimeOnly.MinValue).Ticks + TimeSpan.FromHours(12).Ticks + 1_234_000, 4, $"image:{image.Id:D}:0:0");
            projected = projected with { First = projected.First with { BStart = imageStamp }, Last = projected.Last with { BStart = imageStamp } };
            draft = draft with { Runs = [projected], Associations = draft.Associations.Select(x => x.Role == TimingReportImageRole.B && x.Channel == 0 ? x with { Stamp = imageStamp } : x).ToArray() };
            var revision = (await workspace.ReadAsync()).Revision;
            revision = await workspace.SaveTimingReportAsync(draft, revision, "op", "Verified source image", DateTimeOffset.UtcNow);
            var stored = (await workspace.ReadTimingReportAsync(competition.Id))!;
            var request = new ApproveTimingReportRequest(competition.Id, stored.Revision, revision, "op", DateTimeOffset.UtcNow, "test");
            if (simulatedCapture)
            {
                var failure = await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveTimingReportAsync(request));
                Assert.Contains("Simulation", failure.Message, StringComparison.Ordinal); return;
            }
            var approved = await workspace.ApproveTimingReportAsync(request);
            Assert.True(workspace.Timing.IsActive);
            Assert.All(auxiliarySources.Keys, role => Assert.True(workspace.Auxiliary.State(role).IsActive));
            Assert.Equal(TimingReportXml.Create(draft, "test"), approved.Xml);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApproveTimingReportAsync(request));
            var submission = new TimingReportSubmission(approved.Id, Guid.NewGuid(), "TEST accepted", "{\"testMode\":true}", DateTimeOffset.UtcNow);
            revision = await workspace.SaveTimingReportSubmissionAsync(submission, (await workspace.ReadAsync()).Revision);
            revision = await workspace.SaveTimingReportAsync(draft with { Reviewed = false }, revision, "op", "Review source again", DateTimeOffset.UtcNow);
            var history = await workspace.ReadTimingReportHistoryAsync(competition.Id);
            Assert.Equal(2, history.Count); Assert.True(history[0].Values.Reviewed); Assert.False(history[1].Values.Reviewed);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveTimingReportAsync(request with { ExpectedSeriesRevision = revision }));
            revision = await workspace.SaveTimingReportAsync(history[0].Values, revision, "op", "Restore revision 1", DateTimeOffset.UtcNow);
            Assert.Equal(3, (await workspace.ReadTimingReportHistoryAsync(competition.Id)).Count);
            revision = await workspace.SaveTimingReportAsync(draft with { SourceFingerprint = new string('0', 64) }, revision, "op", "Synthetic stale source", DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveTimingReportAsync(request with { DraftRevision = 4, ExpectedSeriesRevision = revision }));
            revision = await workspace.SaveTimingReportAsync(draft, revision, "op", "Restore reviewed source", DateTimeOffset.UtcNow);
            var historyCount = (await workspace.ReadTimingReportHistoryAsync(competition.Id)).Count;
            var beforeImportRevision = revision;
            revision = await workspace.ApplyTimingReportImportAsync(
                draft with { SourceFingerprint = new string('0', 64) }, revision, "op", "Accepted displayed snapshot", DateTimeOffset.UtcNow);
            Assert.Equal(historyCount + 1, (await workspace.ReadTimingReportHistoryAsync(competition.Id)).Count);
            await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApplyTimingReportImportAsync(
                draft, beforeImportRevision, "op", "Conflicting report edit", DateTimeOffset.UtcNow));
            Assert.Equal(historyCount + 1, (await workspace.ReadTimingReportHistoryAsync(competition.Id)).Count);
            async Task RejectForged(TimingReportDraft forged)
            {
                revision = await workspace.SaveTimingReportAsync(forged, revision, "op", "Synthetic forged draft validation", DateTimeOffset.UtcNow);
                var current = (await workspace.ReadTimingReportAsync(competition.Id))!;
                await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveTimingReportAsync(request with { DraftRevision = current.Revision, ExpectedSeriesRevision = revision }));
            }
            await RejectForged(draft with { Runs = [projected with { First = projected.First with { NetHundredths = 5999 } }] });
            await RejectForged(draft with { Runs = [projected with { First = projected.First with { AStart = projected.First.AStart! with { Ticks = projected.First.AStart.Ticks + 1000 } } }] });
            var foreign = projected.First.BStart! with { SourceReference = $"image:{Guid.NewGuid():D}:0:0" };
            await RejectForged(draft with { Associations = draft.Associations.Select(x => x.Role == TimingReportImageRole.B && x.Channel == 0 ? x with { Stamp = foreign } : x).ToArray(),
                Runs = [projected with { First = projected.First with { BStart = foreign }, Last = projected.Last with { BStart = foreign } }] });
            await RejectForged(draft with { Associations = draft.Associations.Where(x => x.Role != TimingReportImageRole.HandStart).ToArray() });
            revision = await workspace.SaveTimingReportAsync(draft, revision, "op", "Restore verified draft", DateTimeOffset.UtcNow);
            await workspace.Timing.StopAsync();
            await workspace.Auxiliary.StopAllAsync();
            var backupPath = Path.Combine(folder, "backup.ost");
            await workspace.BackupAsync(backupPath);
            await workspace.CloseAsync(); await workspace.OpenAsync(backupPath);
            Assert.Equal(approved.Xml, Assert.Single(await workspace.ReadApprovedTimingReportsAsync(competition.Id)).Xml);
            Assert.Equal(submission, await workspace.ReadTimingReportSubmissionAsync(approved.Id));
            var reopenedImages = await workspace.ReadTimingReportImagesAsync(competition.Id);
            Assert.Equal(3, reopenedImages.Count);
            var reopenedImage = Assert.Single(reopenedImages, x => x.Id == image.Id);
            Assert.Equal(image.Bytes, reopenedImage.Bytes); Assert.Equal("Backup", reopenedImage.DeviceLabel);
            Assert.Equal(4, (await workspace.ReadTimingReportAsync(competition.Id))!.Values.Associations.Length);
            await using var sql = new SqliteConnection($"Data Source={backupPath};Pooling=False"); await sql.OpenAsync();
            foreach (var table in new[] { "TimingReports", "TimingReportImages", "ApprovedTimingReports", "TimingReportSubmissions" })
            {
                await using var command = sql.CreateCommand(); command.CommandText = "DELETE FROM " + table;
                await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            }
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task ConnectedImageArchiveWorksBetweenRunsBeforeTheFinalRunExists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-report-final-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var date = new DateOnly(2026, 10, 3);
            var series = await workspace.CreateAsync(Path.Combine(folder, "race.ost"), new("Test", "Test slope", "Club", date, date, "FIN", "2026-27"),
                [new("Giant slalom", "GS", date, Discipline.GiantSlalom, RaceType.Fis, 2, 0, "9991")]);
            var competition = series.Competitions[0];
            var athlete = new CompetitorValues("SYNTHETIC", "Racer", 2000, "900001", "FIN", "Club", Gender.Male);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, series.Revision);
            var plan = FisStartOrder.FirstRun(competition.Id, competition.Values, Gender.Male, [new(saved.Value.Id, athlete, 10)],
                new("1327", date, date), new(), "final-run-test");
            var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, saved.Revision, "op", "test", DateTimeOffset.UtcNow))).Revisions);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new("Synthetic", "Test", date, Simulation: true), "op");
            await timing.ArmAsync(plan.Entries[0].Bib, null);
            await source.PulseAsync(0, TimeSpan.FromHours(12).Ticks);
            await Until(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
            await timing.ArmAsync(null, plan.Entries[0].Bib);
            await source.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.TicksPerMinute);
            await Until(() => timing.Snapshot!.Complete);
            var image = new TimingReportImage(Guid.NewGuid(), competition.Id, TimingReportImageRole.B, "synthetic.png", "image/png",
                [1, 2, 3], "", "test", DateTimeOffset.UtcNow) { RunNumber = 1 };
            var before = (await workspace.ReadAsync()).Revision;
            // Report preparation between runs never requires the final run or disconnection.
            var after = await workspace.SaveTimingReportImagesAsync([image], before);
            Assert.True(after > before);
            Assert.Equal(image.Id, Assert.Single(await workspace.ReadTimingReportImagesAsync(competition.Id)).Id);
            Assert.True(timing.IsActive);
            Assert.Equal(6000, timing.Snapshot!.Results[0].Hundredths);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task Until(Func<bool> condition)
        => await TimingStorageTests.UntilAsync(condition);

    private static TimingReportDraft ValidDraft()
    {
        var device = new TimingReportDevice("Synthetic", "Test", "SYNTH-1", "TEST.001");
        var start = new TimingReportStamp(TimeSpan.FromHours(12).Ticks + 1_234_567, 7, "synthetic");
        var finish = start with { Ticks = start.Ticks + TimeSpan.TicksPerMinute };
        var bib = new TimingReportBib { Bib = 1, AStart = start, AFinish = finish, BStart = start, BFinish = finish,
            HandStart = start with { Precision = 2, Ticks = TimeSpan.FromHours(12).Ticks + 1_200_000 },
            HandFinish = finish with { Precision = 2, Ticks = TimeSpan.FromHours(12).Ticks + TimeSpan.TicksPerMinute + 1_200_000 }, NetHundredths = 6000 };
        return new() { CompetitionId = Guid.NewGuid(), Header = new(2027, "9991", "FIN", "SL", "FIS", "M", "Synthetic", "Ruka", new(2026, 10, 3)) { TimingLevel = 3 },
            TechnicalDelegate = new("Test", "Delegate", "FIN", Number: "123"),
            Defaults = new() { TimerA = device, TimerB = device, StartDevice = device, FinishCellsA = device, FinishCellsB = device,
                Timekeeper = new("Test", "Keeper", "FIN", "test@example.invalid", "000") },
            Sync = new(TimeSpan.FromHours(11).Ticks, 4), HandSync = new(TimeSpan.FromHours(11).Ticks, 2),
            SyncCheckA = new(TimeSpan.FromHours(11).Ticks + TimeSpan.TicksPerMinute, 4),
            SyncCheckB = new(TimeSpan.FromHours(11).Ticks + TimeSpan.TicksPerMinute, 4),
            Runs = [new() { Run = 1, First = bib, Last = bib, BestBib = 1, BestHundredths = 6000 }], Reviewed = true, CertifyFis = true };
    }
}
