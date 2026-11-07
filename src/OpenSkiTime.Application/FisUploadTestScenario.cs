using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

/// <summary>
/// The outcomes FIS can simulate in test mode. FIS decides them by the race codex of the file: 9991 is a valid race, 9992 a
/// race the account may not upload for, and any other codex an unknown race.
/// </summary>
public enum FisUploadTestScenario
{
    /// <summary>The XML is sent as it is; a real codex is an unknown race in test mode but the XML structure is validated.</summary>
    ValidateOnly,
    /// <summary>The codex is replaced by 9991: FIS accepts the file and reports it processed about a minute later.</summary>
    SimulateSuccess,
    /// <summary>The codex is replaced by 9992: FIS rejects the file for missing permission.</summary>
    SimulateNoPermission,
}

public sealed record FisTestPayload(string FileName, byte[] Xml, string? ReplacedCodex, string? SimulatedCodex);

public static class FisTestPayloads
{
    public const string SuccessCodex = "9991";
    public const string NoPermissionCodex = "9992";

    public static string? CodexOf(FisUploadTestScenario scenario) => scenario switch
    {
        FisUploadTestScenario.SimulateSuccess => SuccessCodex,
        FisUploadTestScenario.SimulateNoPermission => NoPermissionCodex,
        _ => null,
    };

    /// <summary>
    /// The file to send for a scenario. A simulation sends a copy with the codex of the race header and of the file name
    /// replaced; the saved XML revision is never changed.
    /// </summary>
    public static FisTestPayload Prepare(string fileName, byte[] xml, FisUploadTestScenario scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(xml);
        if (CodexOf(scenario) is not { } codex) { return new(fileName, xml, null, null); }
        XDocument document;
        try { document = XDocument.Load(new MemoryStream(xml)); }
        catch (XmlException ex) { throw new DomainValidationException("The FIS XML is invalid: " + ex.Message); }
        var element = document.Root?.Element("Raceheader")?.Element("Codex")
            ?? throw new DomainValidationException("The FIS XML has no race codex to replace for the test.");
        var original = element.Value;
        element.Value = codex;
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        { document.Save(writer); }
        // FIS file names carry the codex (FIN0592.xml): the last occurrence of the header's codex follows it.
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var at = original.Length > 0 ? stem.LastIndexOf(original, StringComparison.Ordinal) : -1;
        var renamed = at >= 0 ? stem[..at] + codex + stem[(at + original.Length)..] + Path.GetExtension(fileName) : stem + "-" + codex + ".xml";
        return new(renamed, output.ToArray(), original, codex);
    }
}
