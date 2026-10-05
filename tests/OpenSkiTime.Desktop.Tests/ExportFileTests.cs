using System.Text;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

// An export replaces the destination only with a completely written file and leaves no temporary files behind.
public sealed class ExportFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("openskitime-export-").FullName;
    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task ReplacesTheDestinationWithTheCompleteContent()
    {
        var path = Path.Combine(_folder, "FIN9991.xml");
        await File.WriteAllTextAsync(path, "previous export that is longer than the new one");
        await ExportFile.WriteTextAsync(path, "<Fisresults/>\n");
        Assert.Equal("<Fisresults/>\n"u8.ToArray(), await File.ReadAllBytesAsync(path)); // UTF-8 without a byte order mark
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AFailedReplacementLeavesTheDestinationAndNoTemporaryFile()
    {
        // The destination cannot be replaced (here: a folder of that name), as when it is locked or protected.
        var path = Path.Combine(_folder, "start-list.tsv");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "kept.txt"), "kept");
        var error = await Record.ExceptionAsync(() => ExportFile.WriteAsync(path, Encoding.UTF8.GetBytes("new")));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(path, "kept.txt")));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
