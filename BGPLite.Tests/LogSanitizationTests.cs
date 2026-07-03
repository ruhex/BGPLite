using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// Tests for <see cref="ManagementApi.SanitizeForLog"/> (#120): user-controlled strings logged
/// via structured logging are also stripped of control characters so they cannot forge log lines.
/// </summary>
public class LogSanitizationTests
{
    [Fact]
    public void Strips_Newlines_And_Other_Control_Chars()
    {
        // A newline-injected value must not be able to break the log line into a forged entry.
        var result = ManagementApi.SanitizeForLog("1.2.3.4\nFAKE[ERROR] something\r\n\tbad");
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\t', result);
        Assert.Contains("1.2.3.4", result);
    }

    [Fact]
    public void Empty_Or_Null_Returns_Empty()
    {
        Assert.Equal(string.Empty, ManagementApi.SanitizeForLog(null));
        Assert.Equal(string.Empty, ManagementApi.SanitizeForLog(""));
    }

    [Fact]
    public void Truncates_Long_Values()
    {
        var result = ManagementApi.SanitizeForLog(new string('a', 1000), maxLength: 50);
        Assert.True(result.Length <= 51); // 50 + ellipsis
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Truncates_All_Control_Char_Input()
    {
        // Regression: control chars must also count toward the length limit (no unbounded spaces).
        var result = ManagementApi.SanitizeForLog(new string('\n', 1000), maxLength: 50);
        Assert.DoesNotContain('\n', result);
        Assert.True(result.Length <= 51);
    }

    [Fact]
    public void Preserves_Normal_Content()
    {
        Assert.Equal("198.51.100.5", ManagementApi.SanitizeForLog("198.51.100.5"));
        Assert.Equal("/api/peers/abc-123", ManagementApi.SanitizeForLog("/api/peers/abc-123"));
    }
}
