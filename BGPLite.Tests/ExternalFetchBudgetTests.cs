namespace BGPLite.Tests;

/// <summary>
/// Architecture guard for the external-fetch budget: the creation point and the
/// partial-vs-503 degradation contract live in ONE helper
/// (<c>ManagementApi.CreateExternalFetchBudget</c>) because the per-endpoint era of this budget
/// is what let the same bug class resurface on every new surface. This test is the executable
/// grep — it fails when a hand-rolled linked source reappears anywhere in the API.
/// </summary>
public class ExternalFetchBudgetTests
{
    [Fact]
    public void ManagementApi_CreatesTheExternalFetchBudgetInExactlyOnePlace()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "BGPLite.Api", "ManagementApi.cs"));

        // The budget deadline may be armed exactly once — inside CreateExternalFetchBudget.
        // (If a second linked-to-shutdown source is ever added deliberately, reviewing its
        // shutdown/expiry semantics together with this count IS the point of the test.)
        Assert.Equal(1, CountOccurrences(source, "CancelAfter(ExternalFetchBudget)"));
        Assert.Equal(1, CountOccurrences(source, "CreateLinkedTokenSource(_shutdownCts"));
    }

    private static int CountOccurrences(string text, string needle) =>
        text.Split(needle, StringSplitOptions.None).Length - 1;

    /// <summary>Walks up from the test binary to the checkout root (the directory holding the
    /// solution file) so the source scan works both locally and on CI regardless of the
    /// bin/ configuration depth.</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BGPLite.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
