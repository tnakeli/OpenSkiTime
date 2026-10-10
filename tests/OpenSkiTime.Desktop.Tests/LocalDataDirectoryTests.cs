using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

// Changing a process-wide environment variable must not overlap tests whose stores read the default directory.
[CollectionDefinition(nameof(LocalDataDirectoryTests), DisableParallelization = true)]
public sealed class LocalDataEnvironmentGroup;

[Collection(nameof(LocalDataDirectoryTests))]
public sealed class LocalDataDirectoryTests
{
    [Fact]
    public async Task OverrideRedirectsDesktopLocalFilesAwayFromTheOperatorProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-local-data", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(LocalDataDirectory.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalDataDirectory.OverrideVariable, root);
            Assert.Equal(Path.GetFullPath(root), LocalDataDirectory.Path);
            await new FisLocalStore(apiCredential: new NoCredential()).SaveListAsync([1, 2, 3]);
            Assert.True(File.Exists(Path.Combine(root, "fis-points-list.zip")));
            Environment.SetEnvironmentVariable(LocalDataDirectory.OverrideVariable, null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"),
                LocalDataDirectory.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalDataDirectory.OverrideVariable, previous);
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private sealed class NoCredential : ICredentialStore
    {
        public bool Exists() => false;
        public string? Read() => null;
        public void Save(string key) { }
        public void Remove() { }
    }
}
