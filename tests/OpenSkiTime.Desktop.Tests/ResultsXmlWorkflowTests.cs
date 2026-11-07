using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// The Results "FIS XML" tab: the XML is created without a separate approval step, a real send is confirmed and stored by
// FIS, test mode simulates the reserved codices on a copy, and every answer is read as a verdict, not as raw text.
public partial class DesktopWorkflowTests
{
    private const string FisMemberToken = "synthetic-member-token";
    private static readonly Guid s_fisSubmission = new("00000000-0000-0000-0000-0000000000a1");

    private sealed record FisCall(string Url, string? Authorization, string? TestMode, string? FileName, byte[]? File);

    private sealed class FisHandler(Func<FisCall, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<FisCall> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? testMode = null, fileName = null; byte[]? file = null;
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "testMode") { testMode = await part.ReadAsStringAsync(cancellationToken); }
                    else if (name == "files[]") { fileName = part.Headers.ContentDisposition.FileName!.Trim('"'); file = await part.ReadAsByteArrayAsync(cancellationToken); }
                }
            }
            var call = new FisCall(request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter, testMode, fileName, file);
            Calls.Add(call);
            return answer(call);
        }
    }

    private static HttpResponseMessage FisJson(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object FisAnswer(string fileName, string fileStatus, string? comment, bool test, string codex = "0592", Guid? uuid = null)
    {
        var id = uuid ?? s_fisSubmission;
        var processed = fileStatus == "processed"; var rejected = fileStatus == "rejected";
        var file = new { index = 0, fileName, detectedType = "XML", status = fileStatus, sectorCode = "AL", seasonCode = 2027, raceCodex = codex, comments = comment, updatedAt = "2026-11-07T10:38:53+01:00" };
        var summary = new { total = 1, accepted = fileStatus == "accepted" ? 1 : 0, processed = processed ? 1 : 0, rejected = rejected ? 1 : 0, isComplete = processed || rejected };
        var outcome = rejected ? "rejected" : "success";
        var message = rejected ? "All competition files were rejected." : "Competition files uploaded successfully.";
        return test
            ? new { uuid = id, outcome, summary, message, files = new[] { file }, testMode = (bool?)true }
            : new { uuid = id, outcome, summary, message, files = new[] { file }, testMode = (bool?)null };
    }

    // A complete one-run FIS race with results, a penalty and full race information, ready for the XML.
    private static async Task<(string Folder, string XmlPath, SeriesWorkspace Workspace, MainViewModel Vm, Guid Competition)> FisXmlRaceAsync(
        FisHandler handler, bool withToken = true, bool secondRace = false, bool legacyInformation = false, bool simulated = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-fisxml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "race.ost"); var date = new DateOnly(2026, 9, 28);
        var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Club", date, date, "FIN", "2026/27"));
        var race = new CompetitionValues("Synthetic race", "SG1 W", date, Discipline.SuperG, RaceType.Fis, 1, 0, "0592",
            CourseName: "Slope", StartAltitudeMeters: 500, FinishAltitudeMeters: 300, VerticalDropMeters: 200, HomologationNumber: "12345/01/26",
            Calendar: new(2027, "Test", "FIN", "TEST", "W", new("TEST", "Delegate", "FIN", "1047")));
        series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
        var competition = series.Competitions[0].Id; var revision = series.Revision;
        if (secondRace)
        {
            series = await workspace.SaveCompetitionAsync(null, race with { Name = "Other race", ShortLabel = "SG2 W", FisCode = "0593" }, series.Revision);
            revision = series.Revision;
        }
        var entrants = new List<DrawEntrant>();
        for (var i = 1; i <= 6; i++)
        {
            var athlete = new CompetitorValues("TEST" + i, "Synthetic", 2002, (990000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "FIN", "Club", Gender.Female);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, competition, true, null, revision);
            revision = saved.Revision; entrants.Add(new(saved.Value.Id, athlete, i * 10m));
        }
        var rules = new FisPenaltyListRules(2027, [new("TEST", 3, 29m, 888m)], [new("SG", Gender.Female, 731, 166m, 0m, [0m, 1m, 2m, 7m, 9m])]);
        var plan = FisStartOrder.FirstRun(competition, race, Gender.Female, entrants, new("1327", new(2026, 9, 24), new(2026, 9, 30), rules), new(), "synthetic-seed");
        var list = Assert.Single((await workspace.SaveStartListAsync(new(plan, revision, "Test", "Synthetic draw", DateTimeOffset.UnixEpoch))).Revisions);
        await workspace.Timing!.SelectRunAsync(list.Id);
        await workspace.Timing.StartAsync(new SimulatorTimingSource(), new("Test", "Synthetic", date, Simulation: simulated), "Test");
        await workspace.Timing.StopAsync();
        foreach (var entry in list.Plan.Entries)
        {
            await workspace.Timing.CorrectAsync(entry.Position == 6
                ? new(DecisionKind.Status, CompetitorId: entry.Entrant.CompetitorId, Status: TimingStatus.DNF)
                : new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 5000 + entry.Position * 100), "Test", "Synthetic result");
        }
        var information = RaceInformation.Empty(race);
        information = information with
        {
            // Race information saved before the chief of timing existed has no such entry.
            Jury = information.Jury.Where(x => !legacyInformation || x.Function != "ChiefTiming")
                .Select(x => x.Function == "TechnicalDelegate" ? x : x with { Person = new("Synthetic", x.Function.ToUpperInvariant(), "FIN") }).ToArray(),
            Runs = information.Runs.Select(r => r with { CourseSetter = new("Course", "SETTER", "FIN"), Gates = 40, TurningGates = 39, StartTime = "10:00" }).ToArray(),
        };
        await workspace.SaveRaceInformationAsync(competition, information, (await workspace.ReadAsync()).Revision, DateTimeOffset.UnixEpoch);
        await workspace.CloseAsync();
        var xmlPath = Path.Combine(folder, "FIN0592-export.xml");
        var token = new ReportSettingsCredential(); if (withToken) { token.Save(FisMemberToken); }
        var http = new HttpClient(handler);
        var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak", ExportPath = xmlPath },
            fisStore: new FisLocalStore(folder, new ReportSettingsCredential(), token), recentSeriesStore: new RecentSeriesStore(folder), submissionHttp: http);
        await vm.OpenSeriesCommand.ExecuteAsync(null); await vm.ShowResultsCommand.ExecuteAsync(null); await vm.LoadResultsCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.True(vm.ResultsReady, vm.ResultsRuleSource);
        return (folder, xmlPath, workspace, vm, competition);
    }

    [AvaloniaFact]
    public async Task ResultsXmlIsCreatedWithoutApprovalConfirmedBeforeARealSendAndReusedOnResend()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, xmlPath, workspace, vm, competition) = await FisXmlRaceAsync(handler);
        try
        {
            // Normal mode is the default; the file name is known before anything is saved.
            Assert.False(vm.FisUploadTestMode);
            Assert.Equal("Send to FIS", vm.SendToFisLabel);
            Assert.Equal("FIN0592.xml", vm.ResultsXmlFileName);
            Assert.Empty(await workspace.ReadApprovedResultsAsync(competition));

            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var approval = Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
            Assert.Equal(approval.Xml, await File.ReadAllBytesAsync(xmlPath));
            Assert.Contains("revision 1", vm.ResultsXmlSavedNote);

            // A real send is confirmed first and nothing leaves before that.
            await vm.SendToFisCommand.ExecuteAsync(null);
            Assert.True(vm.IsSendConfirmationOpen); Assert.Empty(handler.Calls);
            vm.CancelSendToFisCommand.Execute(null);
            Assert.False(vm.IsSendConfirmationOpen); Assert.Empty(handler.Calls);
            await vm.SendToFisCommand.ExecuteAsync(null);
            await vm.ConfirmSendToFisCommand.ExecuteAsync(null);

            // The exact saved bytes go out as a real upload: no testMode field, bearer token, no new revision.
            var call = Assert.Single(handler.Calls);
            Assert.Null(call.TestMode); Assert.Equal(FisMemberToken, call.Authorization);
            Assert.Equal("FIN0592.xml", call.FileName); Assert.Equal(approval.Xml, call.File);
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsSuccess); Assert.False(outcome.IsTest); Assert.Equal("LIVE", outcome.Mode);
            Assert.Equal("Results accepted by FIS", outcome.Title);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionId);
            var file = Assert.Single(outcome.Files);
            Assert.Equal("Processed", file.StatusLabel); Assert.Equal("AL 2027 · codex 0592", file.Race);

            // The raw FIS answer is only shown on request.
            Assert.False(vm.IsFisResponseVisible); Assert.Equal("View FIS response", vm.FisResponseButtonLabel);
            vm.ToggleFisResponseCommand.Execute(null);
            Assert.True(vm.IsFisResponseVisible); Assert.Equal("Hide FIS response", vm.FisResponseButtonLabel);
            Assert.Contains(s_fisSubmission.ToString("D"), outcome.RawResponse);

            // Sending the same results again reuses the revision.
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            Assert.Equal(2, handler.Calls.Count);
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ChangingTheUploadModeCancelsAPendingRealSendAndAConfirmationNeverBecomesARealSendInTestMode()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null);
            Assert.True(vm.IsSendConfirmationOpen);
            vm.FisUploadTestMode = true;
            Assert.False(vm.IsSendConfirmationOpen);
            // Even a confirmation that is somehow open again must not send for real once test mode is on.
            vm.IsSendConfirmationOpen = true;
            await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            Assert.False(vm.IsSendConfirmationOpen); Assert.Empty(handler.Calls);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ANewAttemptNeverShowsTheSubmissionIdOfAnEarlierOne()
    {
        // A simulated upload is answered; the real upload that follows loses its response.
        using var handler = new FisHandler(call => call.TestMode == "true"
            ? FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: true, codex: "9991"))
            : throw new HttpRequestException("The connection was lost."));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            vm.FisUploadTestMode = true;
            await vm.SendToFisCommand.ExecuteAsync(null);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionId);

            vm.FisUploadTestMode = false;
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.False(outcome.IsTest); Assert.Equal("Could not reach FIS", outcome.Title);
            // Check status must not lead back to the test submission.
            Assert.Equal("", outcome.SubmissionId); Assert.Equal("", vm.ResultSubmissionId); Assert.False(vm.CanCheckResultsSubmission);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TheFisAnswerStaysWithItsRaceAndAPastedSubmissionIdIsShownButNotRemembered()
    {
        var pasted = new Guid("00000000-0000-0000-0000-0000000000f7");
        using var handler = new FisHandler(call => call.Url.Contains("/status/", StringComparison.Ordinal)
            ? FisJson(FisAnswer("FIN0593.xml", "processed", "File successfully saved!", test: false, codex: "0593", uuid: Guid.Parse(call.Url[^36..])))
            : FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler, secondRace: true);
        try
        {
            var first = vm.ResultsCompetition!; var other = vm.Competitions.Single(x => x.Values.ShortLabel == "SG2 W");
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            Assert.Equal("Results accepted by FIS", vm.ResultSubmissionOutcome!.Title);

            // Another race starts without the first race's answer or submission id.
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(other);
            Assert.Null(vm.ResultSubmissionOutcome); Assert.Equal("", vm.ResultSubmissionId); Assert.Equal("", vm.ResultsXmlFileName);
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(first);
            Assert.Equal("Results accepted by FIS", vm.ResultSubmissionOutcome!.Title);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionId);

            // An id pasted from elsewhere is looked up and shown, but this revision still remembers its own submission.
            vm.ResultSubmissionId = pasted.ToString("D");
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            Assert.Equal(pasted.ToString("D"), vm.ResultSubmissionOutcome!.SubmissionId);
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(other);
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(first);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionId);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionOutcome!.SubmissionId);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ChangedCommittedTimingIsNeverSentOrReusedAsCurrent()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));

            // Timing is corrected elsewhere after Results was loaded: the loaded results are no longer current.
            var list = Assert.Single((await workspace.ReadStartListsAsync(competition)).Revisions);
            await workspace.Timing!.SelectRunAsync(list.Id);
            await workspace.Timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: list.Plan.Entries[0].Entrant.CompetitorId, Hundredths: 4999), "Test", "Late correction");

            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.Equal("XML not created", outcome.Title);
            Assert.Contains("changed", outcome.Detail, StringComparison.Ordinal); Assert.Contains("Nothing was sent", outcome.Hint, StringComparison.Ordinal);
            Assert.Empty(handler.Calls); Assert.Single(await workspace.ReadApprovedResultsAsync(competition));

            // After Refresh timing the new results are the current ones and get their own revision.
            await vm.LoadResultsCommand.ExecuteAsync(null);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, (await workspace.ReadApprovedResultsAsync(competition)).Count);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ChangedCompetitionDetailsAreNeverSentAsCurrentMetadata()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));

            // The codex is corrected elsewhere after Results was loaded: the cached details are obsolete although timing is not.
            var series = await workspace.ReadAsync();
            var details = series.Competitions.Single(x => x.Id == competition).Values;
            await workspace.SaveCompetitionAsync(competition, details with { FisCode = "0599" }, series.Revision);

            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.Equal("XML not created", outcome.Title);
            Assert.Contains("details changed", outcome.Detail, StringComparison.Ordinal);
            Assert.Empty(handler.Calls); Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AStatusAnswerThatChangesFromTestToRealIsNeverShownAsPublishedResults()
    {
        // The test upload is accepted and still processing; the status answer then drops the test flag and reports success.
        using var handler = new FisHandler(call => call.Url.Contains("/status/", StringComparison.Ordinal)
            ? FisJson(FisAnswer("FIN9991.xml", "processed", "File successfully saved!", test: false, codex: "9991"))
            : FisJson(FisAnswer(call.FileName!, "accepted", "Queued for processing.", test: true, codex: "9991")));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            vm.FisUploadTestMode = true;
            await vm.SendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.NotEqual("Results accepted by FIS", outcome.Title);
            Assert.Equal("FIS gave no usable answer", outcome.Title);
            Assert.Equal(2, handler.Calls.Count);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task AConnectionCutWhileTheAnswerIsReadIsNotReportedAsNothingSent()
    {
        using var handler = new FisHandler(_ => throw new IOException("The response ended prematurely."));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError);
            Assert.DoesNotContain("Nothing was sent", outcome.Hint, StringComparison.Ordinal);
            Assert.Contains("may have reached FIS", outcome.Hint, StringComparison.Ordinal);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task SimulationTimingIsNeverSentAsOfficialResultsButMayBeSentAsATest()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName ?? "FIN9991.xml", "processed", "File successfully saved!", test: call.TestMode == "true", codex: "9991")));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler, simulated: true);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var blocked = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(blocked.IsError); Assert.Equal("Simulation timing", blocked.Title);
            Assert.Contains("Nothing was sent", blocked.Hint, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
            // The XML itself and a test upload remain possible.
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
            vm.FisUploadTestMode = true;
            await vm.SendToFisCommand.ExecuteAsync(null);
            var test = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.False(test.IsError); Assert.True(test.IsTest);
            Assert.Single(handler.Calls); Assert.Equal("true", handler.Calls[0].TestMode);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task TheModeOfASubmissionSurvivesAnotherRaceAndARejectedAnswer()
    {
        // The test upload completes at once. Later the status answer for the same id claims a real submission: it is rejected.
        // The mode is kept with the submission id, so a further check is rejected again and never shows published results.
        var statusAsReal = true;
        var answers = 0;
        using var handler = new FisHandler(call =>
        {
            answers++;
            return FisJson(FisAnswer(call.FileName ?? "FIN9991.xml", "processed", "File successfully saved!", test: !(statusAsReal && call.Url.Contains("/status/", StringComparison.Ordinal)), codex: "9991"));
        });
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler, secondRace: true);
        try
        {
            vm.FisUploadTestMode = true;
            await vm.SendToFisCommand.ExecuteAsync(null);
            Assert.False(vm.ResultSubmissionOutcome!.IsError);
            var id = vm.ResultSubmissionId;
            // A rejected answer is on screen now; it must not become the remembered mode.
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            Assert.True(vm.ResultSubmissionOutcome!.IsError);
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            Assert.True(vm.ResultSubmissionOutcome!.IsError); Assert.Equal("FIS gave no usable answer", vm.ResultSubmissionOutcome.Title);
            Assert.Equal(id, vm.ResultSubmissionId);
            Assert.True(answers >= 3);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ARememberedTestSubmissionIsNeverReportedAsPublishedByTheFirstStatusAnswer()
    {
        // The test upload completes at once; later the status answer for the same id drops the test flag and reports success.
        var statusAsReal = false;
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName ?? "FIN9991.xml", "processed", "File successfully saved!", test: !statusAsReal, codex: "9991")));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            vm.FisUploadTestMode = true;
            await vm.SendToFisCommand.ExecuteAsync(null);
            var sent = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.False(sent.IsError); Assert.True(sent.IsTest);

            statusAsReal = true;
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            var checkedAgain = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(checkedAgain.IsError); Assert.Equal("FIS gave no usable answer", checkedAgain.Title);
            Assert.True(checkedAgain.IsTest);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task APastedLookupOnARaceWithoutXmlNeverStaysOnAnotherRace()
    {
        var pasted = new Guid("00000000-0000-0000-0000-0000000000e5");
        using var handler = new FisHandler(call => FisJson(FisAnswer("FIN0592.xml", "processed", "File successfully saved!", test: false, uuid: Guid.Parse(call.Url[^36..]))));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler, secondRace: true);
        try
        {
            var first = vm.ResultsCompetition!; var other = vm.Competitions.Single(x => x.Values.ShortLabel == "SG2 W");
            // Neither race has a saved XML revision: the selected revision stays null while switching.
            vm.ResultSubmissionId = pasted.ToString("D");
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            Assert.Equal(pasted.ToString("D"), vm.ResultSubmissionOutcome!.SubmissionId);
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(other);
            Assert.Null(vm.ResultSubmissionOutcome); Assert.Equal("", vm.ResultSubmissionId);
            await vm.SelectActiveCompetitionCommand.ExecuteAsync(first);
            Assert.Null(vm.ResultSubmissionOutcome); Assert.Equal("", vm.ResultSubmissionId);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ChiefOfTimingIsEnteredWithTheRaceInformationAndWrittenAsAMember()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler, legacyInformation: true);
        try
        {
            // Information saved without the entry still opens, with an empty row for the chief of timing.
            var row = vm.ResultsJury.Single(x => x.Function == "ChiefTiming");
            Assert.Equal("Chief of timing", row.Label); Assert.Equal("", row.LastName);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            var first = Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
            Assert.DoesNotContain(XDocument.Load(new MemoryStream(first.Xml)).Descendants("Member"), x => x.Attribute("Function")!.Value == "ChiefTiming");

            row.LastName = "Timer"; row.FirstName = "Tina"; row.Nation = "fin";
            Assert.True(await vm.FlushRaceInformationAsync(), vm.StatusMessage);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            var revisions = await workspace.ReadApprovedResultsAsync(competition);
            Assert.Equal(2, revisions.Count);
            var member = XDocument.Load(new MemoryStream(revisions[1].Xml)).Descendants("Member").Single(x => x.Attribute("Function")!.Value == "ChiefTiming");
            Assert.Equal("TIMER", member.Element("Lastname")!.Value); Assert.Equal("Tina", member.Element("Firstname")!.Value); Assert.Equal("FIN", member.Element("Nation")!.Value);
            Assert.Equal(["ChiefCourse", "Startreferee", "Finishreferee", "ChiefTiming"],
                XDocument.Load(new MemoryStream(revisions[1].Xml)).Descendants("Member").Select(x => x.Attribute("Function")!.Value));
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ARevisionIsReusedOnlyWhileTheRaceInformationSnapshotIsTheSame()
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.Single(await workspace.ReadApprovedResultsAsync(competition));

            // The weather source is race information that is not part of the XML: the XML bytes stay the same, but the
            // snapshot differs, so a new revision is needed for the Official Results PDF to count it as current.
            vm.ResultsRuns[0].WeatherSource = "Measured by the course crew";
            Assert.True(await vm.FlushRaceInformationAsync(), vm.StatusMessage);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            var revisions = await workspace.ReadApprovedResultsAsync(competition);
            Assert.Equal(2, revisions.Count);
            Assert.Equal(revisions[0].Xml, revisions[1].Xml);
            Assert.NotEqual(revisions[0].Information, revisions[1].Information);
            await vm.SaveFisXmlCommand.ExecuteAsync(null);
            Assert.Equal(2, (await workspace.ReadApprovedResultsAsync(competition)).Count);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaTheory]
    [InlineData(FisUploadTestScenario.SimulateSuccess, "9991", "processed", "File successfully saved!", true, "Test passed")]
    [InlineData(FisUploadTestScenario.SimulateNoPermission, "9992", "rejected", "Not allowed to upload this file for this race", false, "simulated permission failure")]
    [InlineData(FisUploadTestScenario.ValidateOnly, "0592", "rejected", "Race with codex 0592 not found for AL", false, "reserved codices")]
    public async Task TestModeSimulationsSendACopyWithTheReservedCodexAndNeverChangeTheSavedXml(
        FisUploadTestScenario scenario, string sentCodex, string status, string comment, bool success, string expected)
    {
        using var handler = new FisHandler(call => FisJson(FisAnswer(call.FileName!, status, comment, test: true, codex: sentCodex)));
        var (folder, _, workspace, vm, competition) = await FisXmlRaceAsync(handler);
        try
        {
            vm.FisUploadTestMode = true;
            vm.SelectedFisTestScenario = vm.FisTestScenarios.Single(x => x.Scenario == scenario);
            Assert.Equal("Send to FIS · test", vm.SendToFisLabel);
            await vm.SendToFisCommand.ExecuteAsync(null);
            // A test needs no confirmation.
            Assert.False(vm.IsSendConfirmationOpen);

            var call = Assert.Single(handler.Calls);
            Assert.Equal("true", call.TestMode);
            Assert.Equal("FIN" + sentCodex + ".xml", call.FileName);
            Assert.Equal(sentCodex, XDocument.Load(new MemoryStream(call.File!)).Root!.Element("Raceheader")!.Element("Codex")!.Value);
            // The saved revision keeps the real codex whatever was simulated.
            var saved = Assert.Single(await workspace.ReadApprovedResultsAsync(competition));
            Assert.Equal("FIN0592.xml", saved.XmlFileName);
            Assert.Equal("0592", XDocument.Load(new MemoryStream(saved.Xml)).Root!.Element("Raceheader")!.Element("Codex")!.Value);

            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsTest); Assert.StartsWith("TEST", outcome.Mode);
            Assert.Equal(success, outcome.IsSuccess); Assert.Equal(!success, outcome.IsError);
            Assert.Contains(expected, outcome.Title + " " + outcome.Hint, StringComparison.Ordinal);
            Assert.Equal(comment, Assert.Single(outcome.Files).Comment);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaTheory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"Unauthenticated.\"}", "FIS did not accept the token", "Settings")]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"Invalid scope(s) provided.\"}", "The token lacks permission", "both scopes")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Invalid\",\"errors\":{\"files.0\":[\"The file name is invalid.\"]}}", "FIS rejected the file", "Fix them")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"message\":\"Try later\"}", "FIS gave no usable answer", "may have reached FIS")]
    public async Task FailuresAreShownAsVerdictsWithTheNextStepAndTheRawAnswerBehindTheButton(HttpStatusCode status, string body, string title, string hint)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var handler = new FisHandler(_ => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(handler);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.Equal(title, outcome.Title);
            Assert.Contains(hint, outcome.Hint + outcome.Detail, StringComparison.Ordinal);
            Assert.Contains(body.Split('"')[3], outcome.RawResponse, StringComparison.Ordinal);
            if (status == HttpStatusCode.UnprocessableEntity) { Assert.Equal("The file name is invalid.", Assert.Single(outcome.Problems)); }
            Assert.False(vm.IsSubmissionBusy);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task DuplicateUploadPointsToTheExistingSubmissionAndMissingTokenSendsNothing()
    {
        using var duplicate = new FisHandler(call => call.Url.EndsWith("/upload", StringComparison.Ordinal)
            ? FisJson(new { message = "Duplicate upload detected.", uuid = s_fisSubmission, statusUrl = "https://example.invalid/status" }, HttpStatusCode.TooManyRequests)
            : FisJson(FisAnswer("FIN0592.xml", "processed", "File successfully saved!", test: false)));
        var (folder, _, workspace, vm, _) = await FisXmlRaceAsync(duplicate);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsWarning); Assert.Equal("Already sent", outcome.Title);
            Assert.Equal(s_fisSubmission.ToString("D"), vm.ResultSubmissionId); Assert.True(vm.CanCheckResultsSubmission);
            // Check status follows the earlier upload to its result.
            await vm.CheckResultsSubmissionCommand.ExecuteAsync(null);
            Assert.True(vm.ResultSubmissionOutcome!.IsSuccess);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }

        using var silent = new FisHandler(_ => throw new InvalidOperationException("Nothing may be sent without a token."));
        (folder, _, workspace, vm, _) = await FisXmlRaceAsync(silent, withToken: false);
        try
        {
            await vm.SendToFisCommand.ExecuteAsync(null); await vm.ConfirmSendToFisCommand.ExecuteAsync(null);
            Assert.Empty(silent.Calls);
            var outcome = Assert.IsType<FisUploadOutcome>(vm.ResultSubmissionOutcome);
            Assert.True(outcome.IsError); Assert.Contains("personal access token", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { vm.Dispose(); await workspace.DisposeAsync(); Directory.Delete(folder, true); }
    }
}
