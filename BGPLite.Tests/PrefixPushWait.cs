namespace BGPLite.Tests;

/// <summary>
/// The convergence push is handed to the thread pool and counted while it runs. A test awaits the
/// count of zero before asserting on how many pushes were started — and that no more were.
/// </summary>
internal static class PrefixPushWait
{
    public static Task IdleAsync(Func<int> pending, string what = "the fleet pushes to finish") =>
        WaitForAsync(() => pending() == 0, what);

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"timed out after {timeoutMilliseconds} ms waiting for {what}");
            await Task.Delay(10);
        }
    }
}
