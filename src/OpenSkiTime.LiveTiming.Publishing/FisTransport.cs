using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace OpenSkiTime.LiveTiming.Publishing;

public interface IFisLiveTimingTransport : IAsyncDisposable
{
    Task SendAsync(string xml, long sequence, CancellationToken ct);
    void Disconnect();
}
public static class FisProtocol
{
    public static string Redact(string xml, string? password = null)
    {
        try
        {
            var document = XDocument.Parse(xml);
            foreach (var attribute in document.Descendants().Attributes("passwd")) { attribute.Value = "[REDACTED]"; }
            if (!string.IsNullOrEmpty(password))
            {
                foreach (var text in document.DescendantNodes().OfType<XText>()) { text.Value = text.Value.Replace(password,"[REDACTED]",StringComparison.Ordinal); }
                foreach (var attribute in document.Descendants().Attributes()) { attribute.Value = attribute.Value.Replace(password,"[REDACTED]",StringComparison.Ordinal); }
            }
            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException) { return "[Malformed protocol response omitted]"; }
    }
    // Default diagnostic form: message structure, bibs and FIS error text only. Racer names, nations and FIS codes
    // stay out of routine logs; the full redacted XML is written only when wire tracing is explicitly enabled.
    public static string Summarize(string xml, string? password = null)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null) { return "[Empty protocol message]"; }
            var parts = new List<string>();
            foreach (var element in root.Elements())
            {
                if (element.Name == "error")
                {
                    var text = element.Value;
                    parts.Add("error=\"" + (text.Length > 200 ? text[..200] + "…" : text) + "\"");
                }
                else if (element.Name == "raceevent")
                {
                    parts.Add("raceevent[" + string.Join(",", element.Elements().Select(e =>
                        e.Name.LocalName + ((string?)e.Attribute("bib") is { } bib ? "#" + bib : "") + ((string?)e.Attribute("correction") == "y" ? "(correction)" : ""))) + "]");
                }
                else
                {
                    var run = (string?)element.Attribute("runno");
                    var children = element.Elements().Count();
                    parts.Add(element.Name.LocalName + (run is null ? "" : " run=" + run) + (children == 0 ? "" : $" items={children}"));
                }
            }
            var summary = $"sequence={(string?)root.Attribute("sequence") ?? "-"} " + (parts.Count == 0 ? "(empty)" : string.Join("; ", parts));
            // A rejected response may echo the password in any field; scrub the complete summary.
            return string.IsNullOrEmpty(password) ? summary : summary.Replace(password, "[REDACTED]", StringComparison.Ordinal);
        }
        catch (System.Xml.XmlException) { return "[Malformed protocol message omitted]"; }
    }
    internal static string Describe(string xml, string? password, bool wireTrace) => wireTrace ? Redact(xml, password) : Summarize(xml, password);
    public static void ValidateAck(string xml, long sequence)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root?.Name != "livetiming" || root.Element("error") is not null
                || (string?)root.Attribute("sequence") != sequence.ToString(CultureInfo.InvariantCulture))
            { throw new IOException("FIS did not acknowledge the transmitted sequence (authentication, protocol or sequence error)."); }
        }
        catch (System.Xml.XmlException ex) { throw new IOException("Invalid FIS XML acknowledgement.", ex); }
    }
}
public sealed class FisHttpsTransport : IFisLiveTimingTransport
{
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly bool _wireTrace;
    public FisHttpsTransport(string endpoint, Action<string> log, HttpMessageHandler? handler = null, bool wireTrace = false)
    {
        var uri = new Uri(endpoint);
        if (uri.Scheme != "https") { throw new LiveValidationException("FIS HTTPS requires TLS."); }
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 65536 };
        _log = log; _wireTrace = wireTrace;
    }
    public async Task SendAsync(string xml, long sequence, CancellationToken ct)
    {
        var password = (string?)XDocument.Parse(xml).Root?.Attribute("passwd");
        _log("FIS HTTPS TX " + FisProtocol.Describe(xml, null, _wireTrace));
        using var content = new StringContent(xml, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync("", content, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 65536) { throw new IOException("Oversized FIS response."); }
        var reply = Encoding.UTF8.GetString(bytes);
        _log($"FIS HTTPS {(int)response.StatusCode} RX " + FisProtocol.Describe(reply, password, _wireTrace));
        response.EnsureSuccessStatusCode(); FisProtocol.ValidateAck(reply, sequence);
    }
    public void Disconnect() { }
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}
public sealed class FisTcpTransport(string host, int port, Action<string> log, bool wireTrace = false) : IFisLiveTimingTransport
{
    private TcpClient? _tcp;
    private string _pending = "";
    public async Task SendAsync(string xml, long sequence, CancellationToken ct)
    {
        var password = (string?)XDocument.Parse(xml).Root?.Attribute("passwd");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (_tcp is null)
            {
                _tcp = new TcpClient { NoDelay = true };
                await _tcp.ConnectAsync(host, port, timeout.Token); log("FIS TCP connected");
            }
            log("FIS TCP TX " + FisProtocol.Describe(xml, null, wireTrace));
            var stream = _tcp.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(xml), timeout.Token);
            var bytes = new byte[4096];
            while (true)
            {
                var start = _pending.IndexOf("<livetiming", StringComparison.Ordinal);
                var end = start < 0 ? -1 : _pending.IndexOf("</livetiming>", start, StringComparison.Ordinal);
                var self = start < 0 ? -1 : _pending.IndexOf("/>", start, StringComparison.Ordinal);
                var openEnd = start < 0 ? -1 : _pending.IndexOf('>', start);
                if (self >= 0 && openEnd == self + 1) { end = self + 2; }
                else if (end >= 0) { end += "</livetiming>".Length; }
                if (start >= 0 && end > start)
                {
                    var reply = _pending[start..end]; _pending = _pending[end..];
                    log("FIS TCP RX " + FisProtocol.Describe(reply, password, wireTrace));
                    FisProtocol.ValidateAck(reply, sequence); return;
                }
                var count = await stream.ReadAsync(bytes, timeout.Token);
                if (count == 0) { throw new IOException("FIS TCP connection closed before acknowledgement."); }
                _pending += Encoding.UTF8.GetString(bytes, 0, count);
                if (_pending.Length > 65536) { throw new IOException("Oversized FIS acknowledgement."); }
            }
        }
        catch { Disconnect(); throw; }
    }
    public void Disconnect() { _tcp?.Dispose(); _tcp = null; _pending = ""; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
