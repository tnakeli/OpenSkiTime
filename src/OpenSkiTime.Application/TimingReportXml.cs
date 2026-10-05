using System.Globalization;
using System.Text;
using System.Xml;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

/// <summary>Alpine TDTR XML 1.17, standard individual DH, SG, GS and SL.</summary>
public static class TimingReportXml
{
    public static string FileName(TimingReportDraft report)
    { ArgumentNullException.ThrowIfNull(report); return report.Header.Nation + report.Header.Codex + ".xml"; }
    public static IReadOnlyList<string> Validate(TimingReportDraft report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = new List<string>();
        void Required(string value, string label) { if (string.IsNullOrWhiteSpace(value)) { errors.Add(label + " is required."); } }
        void Person(TimingReportPerson p, string label, bool contact = false)
        {
            Required(p.FirstName, label + " first name"); Required(p.LastName, label + " last name");
            if (!Code(p.Nation)) { errors.Add(label + " nation must be a three-letter code."); }
            if (contact) { Required(p.Email, label + " email"); Required(p.Phone, label + " phone"); }
        }
        void Device(TimingReportDevice d, string label)
        {
            Required(d.Brand, label + " brand"); Required(d.Model, label + " model"); Required(d.Serial, label + " serial"); Required(d.Homologation, label + " homologation");
            if (d.ValidUntilSeason is { } validUntil && validUntil < report.Header.Season)
            { errors.Add(label + $" homologation expired after season {validUntil}; choose equipment valid for this race season."); }
        }
        void Stamp(TimingReportStamp? s, string label)
        {
            if (s is null) { errors.Add(label + " is missing."); }
            else if (s.Ticks < 0 || s.Precision is < 0 or > 7 || s.Ticks > DateTime.MaxValue.Ticks) { errors.Add(label + " is invalid."); }
            else
            {
                long scale = 1; for (var i = s.Precision; i < 7; i++) { scale *= 10; }
                if (s.Ticks % scale != 0) { errors.Add(label + " contains more precision than its source declares."); }
                if (!s.Verified) { errors.Add(label + " must be verified against its source."); }
            }
        }
        void Bib(TimingReportBib b, string label)
        {
            if (b.Bib is not > 0) { errors.Add(label + " bib is missing."); }
            Stamp(b.AStart, label + " A start"); Stamp(b.AFinish, label + " A finish");
            Stamp(b.BStart, label + " B start"); Stamp(b.BFinish, label + " B finish");
            Stamp(b.HandStart, label + " hand start"); Stamp(b.HandFinish, label + " hand finish");
            if (b.NetHundredths is not > 0) { errors.Add(label + " net time is missing."); }
        }
        var h = report.Header;
        if (h.Season is < 1900 or > 2999 || h.Date == default) { errors.Add("Race season and date are required."); }
        if (h.Codex.Length != 4 || !h.Codex.All(char.IsAsciiDigit)) { errors.Add("Race codex must have four digits."); }
        if (!Code(h.Nation)) { errors.Add("Race nation must be a three-letter code."); }
        if (h.Discipline is not ("DH" or "SG" or "GS" or "SL")) { errors.Add("Only DH, SG, GS and SL timing reports are supported."); }
        if (h.Gender is not ("M" or "W" or "A")) { errors.Add("Race gender is required."); }
        if (h.TimingLevel is null or < 0 or > 4) { errors.Add("Choose the FIS timing level (0–4)."); }
        Required(h.Category, "Race category"); Required(h.Place, "Race place");
        Person(report.TechnicalDelegate, "Technical delegate"); Required(report.TechnicalDelegate.Number, "TD number");
        Person(report.Defaults.Timekeeper, "Timekeeper", true);
        var chief = report.Defaults.ChiefOfTiming;
        if (chief.FirstName.Length + chief.LastName.Length > 0) { Person(chief, "Chief of timing"); }
        var d = report.Defaults;
        Device(d.TimerA, "Timer A"); Device(d.TimerB, "Timer B"); Device(d.StartDevice, "Start device");
        if (d.TimerStartA is { } startA) { Device(startA, "Start timer A"); Stamp(report.SyncCheckAStart, "Start timer A synchronization check"); }
        if (d.TimerStartB is { } startB) { Device(startB, "Start timer B"); Stamp(report.SyncCheckBStart, "Start timer B synchronization check"); }
        Device(d.FinishCellsA, "Finish cells A"); Device(d.FinishCellsB, "Finish cells B");
        // Start-clock declaration is required for speed/giant-slalom categories at levels 0–2.
        if (h.Discipline != "SL" && h.TimingLevel is >= 0 and <= 2)
        { Device(d.StartClock, "Start clock"); }
        else if (d.StartClock.Brand.Length + d.StartClock.Model.Length > 0) { Device(d.StartClock, "Start clock"); }
        string[] modes = ["Cable", "Radio", "LAN", "WLAN", "Mobile", "USB", "Other"];
        if (!modes.Contains(d.ConnectionA) || !modes.Contains(d.ConnectionB) || !modes.Contains(d.Voice))
        { errors.Add("Choose timing A/B and voice connection modes."); }
        Stamp(report.Sync, "Electronic synchronization"); Stamp(report.HandSync, "Hand synchronization");
        Stamp(report.SyncCheckA, "A synchronization check"); Stamp(report.SyncCheckB, "B synchronization check");
        var checks = new[] { report.SyncCheckA, report.SyncCheckB,
            d.TimerStartA is null ? null : report.SyncCheckAStart, d.TimerStartB is null ? null : report.SyncCheckBStart }
            .Where(x => x is not null).Select(x => x!.Ticks % TimeSpan.TicksPerDay).ToArray();
        if (checks.Any(a => checks.Any(b => Math.Min(Math.Abs(a - b), TimeSpan.TicksPerDay - Math.Abs(a - b)) > TimeSpan.TicksPerMillisecond)))
        { errors.Add("Electronic synchronization checks must agree within 0.001 seconds."); }
        if (report.Sync is { } sync && checks.Any(x => (x - sync.Ticks % TimeSpan.TicksPerDay + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay < TimeSpan.TicksPerMinute))
        { errors.Add("Perform the synchronization check at least one minute after synchronization."); }
        if (report.Runs.Length is < 1 or > 3 || report.Runs.Select(x => x.Run).Distinct().Count() != report.Runs.Length
            || !report.Runs.Select(x => x.Run).Order().SequenceEqual(Enumerable.Range(1, report.Runs.Length))
            || (h.Discipline != "SL" && report.Runs.Length > 2))
        { errors.Add("Include the supported race runs exactly once."); }
        foreach (var r in report.Runs)
        {
            Bib(r.First, $"Run {r.Run} first"); Bib(r.Last, $"Run {r.Run} last");
            if (r.BestBib is not > 0 || r.BestHundredths is not > 0) { errors.Add($"Run {r.Run} best A time is missing."); }
            if (r.AllResultsA && r.MissedA.Length > 0 || !r.AllResultsA && r.MissedA.Length == 0)
            { errors.Add($"Run {r.Run} missed A declarations are inconsistent."); }
            if (r.MissedA.GroupBy(m => m.Bib).Any(g => g.Count() > 1))
            { errors.Add($"Run {r.Run} contains duplicate replacement bibs."); }
            foreach (var m in r.MissedA)
            {
                if (m.Bib <= 0) { errors.Add($"Run {r.Run} missed A bib is invalid."); }
                Required(m.Reason, $"Run {r.Run} bib {m.Bib} missed A reason"); Required(m.TimeFrom, $"Run {r.Run} bib {m.Bib} backup source");
            }
        }
        if (report.Associations.GroupBy(x => (x.Run, x.Bib, x.Channel, x.Role)).Any(x => x.Count() > 1))
        { errors.Add("Resolve duplicate B/hand timestamp associations."); }
        foreach (var a in report.Associations)
        {
            if (!report.Runs.Any(x => x.Run == a.Run) || a.Bib <= 0 || a.Channel is < 0 or > 1 || !Enum.IsDefined(a.Role)
                || a.Role == TimingReportImageRole.HandStart && a.Channel != 0 || a.Role == TimingReportImageRole.HandFinish && a.Channel != 1)
            { errors.Add("A B/hand timestamp association has an invalid run, bib or channel."); }
            Stamp(a.Stamp, $"Run {a.Run} bib {a.Bib} {a.Role} evidence");
        }
        if (!report.Reviewed) { errors.Add("Review and verify the complete report before approval."); }
        if (!report.CertifyFis) { errors.Add("Confirm that timing and calculations adhered to FIS rules."); }
        return errors;
    }

    public static byte[] Create(TimingReportDraft report, string softwareVersion)
    {
        var errors = Validate(report);
        if (errors.Count > 0) { throw new DomainValidationException(string.Join("\n", errors)); }
        if (string.IsNullOrWhiteSpace(softwareVersion)) { throw new DomainValidationException("Results software version is required."); }
        using var stream = new MemoryStream();
        // Text that bypassed validation must not crash the application: XmlWriter rejects control characters.
        try { Write(); }
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        { throw new DomainValidationException(FisResultXml.XmlTextError); }
        return stream.ToArray();

        void Write()
        {
            // Fixed line endings, as in the result XML: the approved bytes must not depend on the operating system.
            using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" });
            void E(string name, string value) => w.WriteElementString(name, value);
            void Optional(string name, string value) { if (!string.IsNullOrWhiteSpace(value)) { E(name, value); } }
            void Person(string element, TimingReportPerson p, string? function = null, bool timekeeper = false)
            {
                w.WriteStartElement(element); if (function is not null) { w.WriteAttributeString("Function", function); }
                if (timekeeper) { Optional("Company", p.Company); }
                else if (function == "TECHNICALDELEGATE") { Optional("Number", p.Number); }
                E("Lastname", p.LastName); E("Firstname", p.FirstName); E("Nation", p.Nation);
                Optional("Email", p.Email); Optional("Phonenbr", p.Phone); w.WriteEndElement();
            }
            void Device(string element, TimingReportDevice d, string? system = null)
            {
                w.WriteStartElement(element); if (system is not null) { w.WriteAttributeString("System", system); }
                if (element == "Timer") { w.WriteAttributeString("used", "yes"); }
                if (element == "Startdevice") { w.WriteAttributeString("Type", "10"); }
                E("Brand", d.Brand); E("Model", d.Model); E("Serial", d.Serial); E("Homologation", d.Homologation); w.WriteEndElement();
            }
            void Stamp(string element, TimingReportStamp? s, string? system = null)
            { w.WriteStartElement(element); if (system is not null) { w.WriteAttributeString("System", system); } w.WriteString(FormatStamp(s!)); w.WriteEndElement(); }
            void Bib(string element, TimingReportBib b)
            {
                w.WriteStartElement(element); w.WriteAttributeString("no", b.Bib!.Value.ToString(CultureInfo.InvariantCulture));
                Stamp("Start", b.AStart, "A"); Stamp("Start", b.BStart, "B"); Stamp("Start", b.HandStart, "Hand");
                Stamp("Finish", b.AFinish, "A"); Stamp("Finish", b.BFinish, "B"); Stamp("Finish", b.HandFinish, "Hand");
                E("Net", FormatNet(b.NetHundredths!.Value)); w.WriteEndElement();
            }
            var h = report.Header; var d = report.Defaults;
            w.WriteStartDocument(); w.WriteStartElement("Fisresults"); E("XMLversion", "1.17");
            w.WriteStartElement("Raceheader"); w.WriteAttributeString("Sector", "AL"); w.WriteAttributeString("Gender", h.Gender);
            E("Season", h.Season.ToString(CultureInfo.InvariantCulture)); E("Codex", h.Codex); E("Nation", h.Nation);
            E("Discipline", h.Discipline); E("Category", h.Category); E("Type", "TR"); Optional("Eventname", h.EventName); E("Place", h.Place);
            w.WriteStartElement("Racedate"); E("Day", h.Date.Day.ToString(CultureInfo.InvariantCulture)); E("Month", h.Date.Month.ToString(CultureInfo.InvariantCulture)); E("Year", h.Date.Year.ToString(CultureInfo.InvariantCulture)); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("AL_race"); Person("Jury", report.TechnicalDelegate, "TECHNICALDELEGATE");
            if (d.ChiefOfTiming.LastName.Length > 0) { Person("Jury", d.ChiefOfTiming, "CHIEFOFTIMING"); } w.WriteEndElement();
            w.WriteStartElement("AL_timingreport"); Person("Timekeeper", d.Timekeeper, timekeeper: true);
            w.WriteStartElement("Devices"); Device("Timer", d.TimerA, "A"); Device("Timer", d.TimerB, "B"); Device("Startdevice", d.StartDevice);
            if (d.TimerStartA is { } startA) { Device("Timer_start", startA, "A"); }
            if (d.TimerStartB is { } startB) { Device("Timer_start", startB, "B"); }
            if (d.StartClock.Model.Length > 0) { Device("Startclock", d.StartClock); }
            Device("Finishcells", d.FinishCellsA, "A"); Device("Finishcells", d.FinishCellsB, "B");
            w.WriteStartElement("Software"); E("Brand", "OpenSkiTime"); E("Version", softwareVersion); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("Connections"); w.WriteStartElement("Mode"); w.WriteAttributeString("System", "A"); w.WriteString(d.ConnectionA); w.WriteEndElement();
            w.WriteStartElement("Mode"); w.WriteAttributeString("System", "B"); w.WriteString(d.ConnectionB); w.WriteEndElement(); E("Voice", d.Voice); w.WriteEndElement();
            w.WriteStartElement("Timing"); w.WriteAttributeString("Runs", report.Runs.Length.ToString(CultureInfo.InvariantCulture));
            w.WriteStartElement("Synchronisation"); Stamp("Sync", report.Sync); Stamp("Handsync", report.HandSync); Stamp("Synccheck", report.SyncCheckA, "A"); Stamp("Synccheck", report.SyncCheckB, "B");
            if (d.TimerStartA is not null) { Stamp("Synccheck", report.SyncCheckAStart, "AStart"); }
            if (d.TimerStartB is not null) { Stamp("Synccheck", report.SyncCheckBStart, "BStart"); } w.WriteEndElement();
            foreach (var r in report.Runs.OrderBy(x => x.Run))
            {
                w.WriteStartElement("Times"); w.WriteAttributeString("Run", r.Run.ToString(CultureInfo.InvariantCulture)); Bib("Bibfirst", r.First); Bib("Biblast", r.Last);
                w.WriteStartElement("BestA"); E("Bib", r.BestBib!.Value.ToString(CultureInfo.InvariantCulture)); E("Time", FormatNet(r.BestHundredths!.Value)); w.WriteEndElement();
                // The v1.17 example and established FIS TDTR format use SystemA (the attribute table says System).
                w.WriteStartElement("Allresults"); w.WriteAttributeString("SystemA", r.AllResultsA ? "yes" : "no");
                foreach (var m in r.MissedA) { w.WriteStartElement("MissedA"); E("Bib", m.Bib.ToString(CultureInfo.InvariantCulture)); E("Reason", m.Reason); E("Timefrom", m.TimeFrom); w.WriteEndElement(); }
                w.WriteEndElement(); Optional("Comment", r.Comment); w.WriteEndElement();
            }
            E("Delayedstartdoor", "no"); E("CertifyFIS", "yes"); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndDocument();
        }
    }
    public static string FormatStamp(TimingReportStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var time = new DateTime(stamp.Ticks);
        return time.ToString(stamp.Precision == 0 ? "HH:mm:ss" : "HH:mm:ss." + new string('f', stamp.Precision), CultureInfo.InvariantCulture);
    }
    public static string FormatNet(long hundredths) => string.Create(CultureInfo.InvariantCulture, $"{hundredths / 6000}:{hundredths / 100 % 60:00}.{hundredths % 100:00}");
    private static bool Code(string value) => value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z');
}
