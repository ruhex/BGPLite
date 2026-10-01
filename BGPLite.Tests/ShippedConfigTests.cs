using BGPLite.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace BGPLite.Tests;

/// <summary>
/// Every configuration file the repository ships (or CI runs) must survive <c>Validate()</c>.
/// A narrowed validation range that breaks a checked-in config is a startup failure in the
/// compose integration stand, where the server exits before the API comes up and the run fails
/// with an opaque "timeout waiting for /api/server".
/// </summary>
public sealed class ShippedConfigTests
{
    private readonly ITestOutputHelper _out;

    public ShippedConfigTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string> ShippedConfigs() => new()
    {
        "appsettings.Example.yml",
        "docker/integration/server-appsettings.yml",
    };

    /// <summary>
    /// The operator's live config is untracked, so it is not in the data above — but it is the
    /// config the running route server actually loads, so a narrowed validation range that
    /// rejects it is a local outage. Opt in via BGPLITE_VALIDATE_LIVE_CONFIG=1.
    /// </summary>
    [Fact]
    public void LiveConfig_PassesValidation_WhenPresent()
    {
        var full = Path.Combine(RepoRoot(), "appsettings.yml");
        if (!File.Exists(full))
            return;                                  // not an operator checkout
        if (Environment.GetEnvironmentVariable("BGPLITE_VALIDATE_LIVE_CONFIG") != "1")
            return;                                  // do not touch a machine's live config implicitly

        var ex = Record.Exception(ConfigLoader.Load(full).Validate);
        _out.WriteLine("appsettings.yml: " + (ex is null ? "OK" : ex.Message));
        Assert.Null(ex);
    }

    [Theory]
    [MemberData(nameof(ShippedConfigs))]
    public void ShippedConfig_PassesValidation(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(full), $"config not found: {full}");

        var config = ConfigLoader.Load(full);
        var ex = Record.Exception(config.Validate);

        _out.WriteLine($"{relativePath}: {(ex is null ? "OK" : ex.Message)}");
        Assert.Null(ex);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (bin/Debug/net10.0 -> repo).
    /// </summary>
    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "BGPLite.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("repository root (BGPLite.sln) not found above " + AppContext.BaseDirectory);
    }
}
