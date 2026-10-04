using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenSkiTime.Application;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed partial class ProductInfoTests
{
    [Theory]
    [InlineData("0.1.0+3f2a9c1", "0.1.0")]
    [InlineData("1.2.3-preview.4+abc", "1.2.3-preview.4")]
    [InlineData("2.0.0", "2.0.0")]
    [InlineData("", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void InformationalVersionDropsBuildMetadata(string? informational, string expected)
        => Assert.Equal(expected, ProductInfo.FromInformationalVersion(informational));

    [Fact]
    public void VersionIsSemanticAndComesFromTheDeclaredVersionPrefix()
    {
        Assert.Matches(SemVer(), ProductInfo.Version);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        var prefix = XDocument.Load(Path.Combine(directory.FullName, "Directory.Build.props")).Descendants("VersionPrefix").Single().Value;
        // Local and CI builds use the declared prefix; release builds pass the matching tag version.
        Assert.StartsWith(prefix, ProductInfo.Version, StringComparison.Ordinal);
        Assert.Equal("OpenSkiTime " + ProductInfo.Version, ProductInfo.DisplayName);
        Assert.Equal("FIS rules 2026-27", ProductInfo.FisRulesLabel);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();
}
