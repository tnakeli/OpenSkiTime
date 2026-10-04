using System.Reflection;

namespace OpenSkiTime.Application;

/// <summary>
/// Product identity shown in the UI and reports. The software version follows Semantic Versioning
/// (MAJOR.MINOR.PATCH, optional pre-release label) from <c>VersionPrefix</c> in Directory.Build.props;
/// release builds override it with the version tag.
/// </summary>
public static class ProductInfo
{
    public const string Name = "OpenSkiTime";
    /// <summary>FIS rule season whose reviewed booklets the timing, points and report rules implement.</summary>
    public const string FisRulesSeason = "2026-27";
    public const string RepositoryUrl = "https://github.com/tnakeli/OpenSkiTime";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string SecurityReportUrl = RepositoryUrl + "/security/advisories/new";
    public const string LicenseUrl = RepositoryUrl + "/blob/master/LICENSE";
    public const string ThirdPartyNoticesUrl = RepositoryUrl + "/blob/master/THIRD-PARTY-NOTICES.md";
    public const string PrivacyUrl = "https://openskiti.me/privacy/";
    public const string WebsiteUrl = "https://openskiti.me/";
    public static string Version { get; } = FromInformationalVersion(
        typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    /// <summary>Name and version, for example "OpenSkiTime 0.1.0".</summary>
    public static string DisplayName => Name + " " + Version;
    public static string FisRulesLabel => "FIS rules " + FisRulesSeason;
    /// <summary>Drops SemVer build metadata such as the source revision ("+abc123").</summary>
    public static string FromInformationalVersion(string? value)
    {
        var version = (value ?? "").Split('+', 2)[0].Trim();
        return version.Length == 0 ? "0.0.0" : version;
    }
}
