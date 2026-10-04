namespace OpenSkiTime.LiveTiming.Client;

/// <summary>Credential Manager location of a server's publisher key, per server origin and Windows user.</summary>
public static class PublisherKeyCredential
{
    public static string Target(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        { throw new LiveValidationException("Enter an HTTP or HTTPS server address without credentials in the URL."); }
        return "OpenSkiTime.LiveTiming.PublisherKey:" + uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
    public static void Validate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is < 32 or > 512 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        { throw new ArgumentException("Enter the complete publisher key you received from the server operator (at least 32 characters, no spaces).", nameof(key)); }
    }
}
