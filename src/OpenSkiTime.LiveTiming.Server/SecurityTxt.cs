using System.Globalization;

namespace OpenSkiTime.LiveTiming.Server;

/// <summary>Category for session-creation audit records: publisher name and session id only, never keys or tokens.</summary>
public static partial class PublisherAudit
{
    public const string Category = "OpenSkiTime.LiveTiming.Server.PublisherAudit";
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Session {SessionId} created by publisher {Publisher}")]
    public static partial void SessionCreated(ILogger logger, Guid sessionId, string publisher);
}

/// <summary>RFC 9116 security contact. Self-hosted operators can point Contact at their own reporting channel.</summary>
public static class SecurityTxt
{
    public const string DefaultContact = "https://github.com/tnakeli/OpenSkiTime/security/advisories/new";
    public const string Policy = "https://github.com/tnakeli/OpenSkiTime/blob/master/SECURITY.md";
    public static string Create(string publicBase, IConfiguration config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicBase); ArgumentNullException.ThrowIfNull(config);
        var contact = config["LiveTiming:SecurityContact"] is { Length: > 0 } configured ? configured : DefaultContact;
        if (!Uri.TryCreate(contact, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "mailto") || contact.Any(char.IsControl))
        { throw new InvalidOperationException("LiveTiming security contact must be an https: or mailto: URI."); }
        // The service publishes this contact while it runs; the expiry renews daily and stays within the recommended year.
        var expires = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(180);
        return $"Contact: {contact}\nExpires: {expires.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\n"
            + $"Policy: {Policy}\nPreferred-Languages: en, fi\nCanonical: {publicBase.TrimEnd('/')}/.well-known/security.txt\n";
    }
}
