namespace OpenSkiTime.Application;

/// <summary>
/// The flag of a nation, as the live view shows it next to the three-letter FIS code. Racers carry the FIS nation code
/// (GER, SUI, NED); the flag graphic is that of the matching ISO 3166 country (Twemoji, CC-BY 4.0, see
/// THIRD-PARTY-NOTICES.md), embedded as a small PNG so no download or system emoji font is needed. A code without a flag
/// shows under its own code only. The codes match <c>live-nations.js</c> of the live view.
/// </summary>
public static class NationFlags
{
    private static readonly Dictionary<string, string> s_iso = new(StringComparer.Ordinal)
    {
        ["AFG"] = "AF", ["ALB"] = "AL", ["ALG"] = "DZ", ["AND"] = "AD", ["ANG"] = "AO", ["ARG"] = "AR",
        ["ARM"] = "AM", ["ASA"] = "AS", ["AUS"] = "AU", ["AUT"] = "AT", ["AZE"] = "AZ", ["BAH"] = "BS",
        ["BAN"] = "BD", ["BAR"] = "BB", ["BEL"] = "BE", ["BER"] = "BM", ["BHU"] = "BT", ["BIH"] = "BA",
        ["BLR"] = "BY", ["BOL"] = "BO", ["BRA"] = "BR", ["BUL"] = "BG", ["CAN"] = "CA", ["CAY"] = "KY",
        ["CHI"] = "CL", ["CHN"] = "CN", ["CMR"] = "CM", ["COL"] = "CO", ["CRC"] = "CR", ["CRO"] = "HR",
        ["CYP"] = "CY", ["CZE"] = "CZ", ["DEN"] = "DK", ["DOM"] = "DO", ["ECU"] = "EC", ["EGY"] = "EG",
        ["ERI"] = "ER", ["ESA"] = "SV", ["ESP"] = "ES", ["EST"] = "EE", ["ETH"] = "ET", ["FIJ"] = "FJ",
        ["FIN"] = "FI", ["FRA"] = "FR", ["GBR"] = "GB", ["GEO"] = "GE", ["GER"] = "DE", ["GHA"] = "GH",
        ["GRE"] = "GR", ["GUA"] = "GT", ["GUM"] = "GU", ["HAI"] = "HT", ["HKG"] = "HK", ["HON"] = "HN",
        ["HUN"] = "HU", ["INA"] = "ID", ["IND"] = "IN", ["IRI"] = "IR", ["IRL"] = "IE", ["IRQ"] = "IQ",
        ["ISL"] = "IS", ["ISR"] = "IL", ["ISV"] = "VI", ["ITA"] = "IT", ["IVB"] = "VG", ["JAM"] = "JM",
        ["JOR"] = "JO", ["JPN"] = "JP", ["KAZ"] = "KZ", ["KEN"] = "KE", ["KGZ"] = "KG", ["KOR"] = "KR",
        ["KOS"] = "XK", ["KSA"] = "SA", ["KUW"] = "KW", ["LAT"] = "LV", ["LBN"] = "LB", ["LIE"] = "LI",
        ["LTU"] = "LT", ["LUX"] = "LU", ["MAD"] = "MG", ["MAR"] = "MA", ["MAS"] = "MY", ["MDA"] = "MD",
        ["MEX"] = "MX", ["MGL"] = "MN", ["MKD"] = "MK", ["MLT"] = "MT", ["MNE"] = "ME", ["MON"] = "MC",
        ["NED"] = "NL", ["NEP"] = "NP", ["NGR"] = "NG", ["NOR"] = "NO", ["NZL"] = "NZ", ["PAK"] = "PK",
        ["PER"] = "PE", ["PHI"] = "PH", ["POL"] = "PL", ["POR"] = "PT", ["PRK"] = "KP", ["PUR"] = "PR",
        ["QAT"] = "QA", ["ROU"] = "RO", ["RSA"] = "ZA", ["RUS"] = "RU", ["SEN"] = "SN", ["SGP"] = "SG",
        ["SLO"] = "SI", ["SMR"] = "SM", ["SRB"] = "RS", ["SRI"] = "LK", ["SUI"] = "CH", ["SVK"] = "SK",
        ["SWE"] = "SE", ["THA"] = "TH", ["TJK"] = "TJ", ["TLS"] = "TL", ["TGA"] = "TO", ["TPE"] = "TW",
        ["TTO"] = "TT", ["TUN"] = "TN", ["TUR"] = "TR", ["UAE"] = "AE", ["UKR"] = "UA", ["URU"] = "UY",
        ["USA"] = "US", ["UZB"] = "UZ", ["VEN"] = "VE", ["VIE"] = "VN", ["ZIM"] = "ZW",
    };
    private static readonly Dictionary<string, byte[]?> s_images = new(StringComparer.Ordinal);

    /// <summary>The nation codes that have a flag.</summary>
    public static IReadOnlyCollection<string> Codes => s_iso.Keys;

    /// <summary>The upper-case, trimmed code, or an empty string.</summary>
    public static string Normalize(string? code) => code?.Trim().ToUpperInvariant() ?? "";

    /// <summary>The ISO 3166 code of the flag drawn for a FIS nation code, or null when there is no flag.</summary>
    public static string? IsoCode(string? code) => s_iso.GetValueOrDefault(Normalize(code));

    /// <summary>The PNG of the flag of a nation code, or null when the code is blank or has no flag.</summary>
    public static byte[]? Png(string? code)
    {
        var iso = IsoCode(code);
        if (iso is null) { return null; }
        lock (s_images)
        {
            if (!s_images.TryGetValue(iso, out var png))
            {
                using var stream = typeof(NationFlags).Assembly.GetManifestResourceStream("Flags." + iso + ".png");
                if (stream is null) { png = null; }
                else { using var copy = new MemoryStream(); stream.CopyTo(copy); png = copy.ToArray(); }
                s_images[iso] = png;
            }
            return png;
        }
    }
}
