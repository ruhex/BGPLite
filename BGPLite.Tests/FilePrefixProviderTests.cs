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

    [Fact]
    public async Task OversizedFileThrows()
    {
        // #487: cap parity with the HTTP paths — a file over HttpPrefixProvider.MaxResponseBytes
        // is never a legitimate prefix list and must not be read into memory whole.
        var path = Path.GetTempFileName();
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            await fs.WriteAsync(new byte[HttpPrefixProvider.MaxResponseBytes + 1]);
        try
        {
            var provider = new FilePrefixProvider(NullLogger<FilePrefixProvider>.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.LoadAsync(new PrefixSourceConfig { Name = "t", Kind = "file", Path = path }));
            Assert.Contains("cap", ex.Message);
        }
        finally { File.Delete(path); }
    }
}
