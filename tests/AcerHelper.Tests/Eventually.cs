namespace AcerHelper.Tests;

/// <summary>
/// Wait for something a test cannot await, because the only handle on it is a control's public state.
///
/// WHY POLLING RATHER THAN A QUEUE DRAIN: a deferred read runs on a row's own serial worker (<c>HwSerial</c>,
/// a <c>ContinueWith</c> onto the thread pool), so even with a synchronous poster the correction lands a
/// moment later. The obvious alternative — driving the real UI dispatcher with
/// <c>Dispatcher.UIThread.RunJobs(null)</c> — was tried first and does not work here: the dispatcher is
/// thread-affine, and a bare xUnit process creates it from whichever thread first touches
/// <c>Dispatcher.UIThread</c>, which is the row's worker thread issuing the post, not the test thread. Every
/// later <c>RunJobs</c> from the test thread then throws <c>The calling thread cannot access this object</c>.
/// That is why the rows take an injectable poster; see <see cref="AcerHelper.UI.ViewModels.VerifiedHwValue{T}"/>.
/// </summary>
internal static class Eventually
{
    /// <summary>Poll until <paramref name="condition"/> holds or the budget runs out. Returns whether it ever
    /// held, so callers assert on the reason rather than on a bare timeout.</summary>
    public static bool Until(Func<bool> condition, int budgetMs = 3000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(5);   // an opaque call per iteration, so the loop cannot cache the field it reads
        }
    }

    /// <summary>A synchronous poster: the UI-thread marshaller the rows use by default, replaced by a direct
    /// call so a test can observe the correction without a dispatcher at all.</summary>
    public static void Sync(Action action) => action();
}
