using BGPLite.Configuration;
using BGPLite.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BGPLite.Tests;

public class FilePrefixProviderTests
{
    [Fact]
    public async Task ReadsCidrFile()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "# comment\n1.2.3.0/24\n10.0.0.0/8\n");
        try
        {
            var provider = new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance);
            var result = await provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "file", Path = path });
            Assert.Equal(2, result.Prefixes.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFileThrows()
    {
        var provider = new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "file", Path = "/no/such/file.txt" }));
    }

    [Fact]
    public async Task EmptyPathThrowsInvalidOperation()
    {
        var provider = new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "file", Path = null }));
    }
}
