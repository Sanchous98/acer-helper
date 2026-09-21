using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace AcerHelper.Tests;

/// <summary>
/// THE TRAY RULE MUST NOT REACH THE SESSION MANAGER.
///
/// The flyout hides instead of closing when the window manager closes it — that is the tray behaviour — and the
/// window used to implement it by cancelling EVERY close, whatever asked for it. On this desktop that refusal is
/// not private to the window: Avalonia's X11 backend is an XSMP client (measured on the owner's machine — the
/// process holds an ICE connection to ksmeserver and loads <c>libICE.so.6</c>), so KDE's logout asks the app to
/// close its windows and reads a cancelled close as the application refusing to end the session. The owner's
/// report, verbatim: "выплывало сообщение, что завершение сеанса отменено (безымянное приложение). Пришлось
/// отключить acer helper сначала" — the machine could only be switched off after quitting the app by hand.
///
/// The same request arrives on Windows as WM_QUERYENDSESSION, where cancelling is the classic way to stall a
/// shutdown, so <see cref="AcerHelper.UI.FlyoutClosePolicy"/> is deliberately cross-platform (it lives in an
/// un-suffixed file and this project's TFM compiles it, which is why the decision is a pure function of the
/// reason rather than something read off a window).
///
/// THE REASONS ARE AVALONIA 12.1.2'S OWN, read out of the installed assembly rather than from memory:
/// <c>Undefined=0, WindowClosing=1, OwnerWindowClosing=2, ApplicationShutdown=3, OSShutdown=4</c>. That is what
/// the two groups below partition: the window's own close hides, everything that means "the app is on its way
/// out" goes through.
///
/// MUTATION-VERIFIED, one at a time. Reducing the policy to <c>!destroying</c> — hiding on every reason, which is
/// the original defect in effect — reddens <see cref="ASessionEndIsNeverRefused"/> and leaves both hiding rows
/// green; deleting the <c>FlyoutClosePolicy.HideInsteadOfClose</c> call from <c>MainWindow</c> (back to a blanket
/// <c>e.Cancel = true</c>) reddens <see cref="TheWindowDecidesThroughThePolicyAndNeverCancelsBlindly"/>; and
/// dropping the <c>SessionEnding</c> subscription in <c>AppController</c> reddens
/// <see cref="TheAppExitsWhenTheDesktopEndsTheSession"/>. Each was run and watched.
/// </summary>
public class FlyoutClosePolicyTests
{
    /// <summary>The repository root, from the COMPILER's path: the test host's working directory is its output
    /// folder, where a relative path finds nothing.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path), $"expected a source file at {relative} — a guard that reads nothing passes for the wrong reason");
        return File.ReadAllText(path);
    }

    /// <summary>A close the WINDOW initiated, with the app staying up: hide it, the tray rule. <c>Undefined</c>
    /// carries no information, and hiding is the behaviour this window has always had, so an unlabelled close
    /// keeps it.</summary>
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing)]
    [InlineData(WindowCloseReason.Undefined)]
    public void ACloseTheWindowInitiatedOnlyHidesTheFlyout(WindowCloseReason reason)
        => Assert.True(UI.FlyoutClosePolicy.HideInsteadOfClose(destroying: false, reason));

    /// <summary>The defect itself: these three say the app is going away — the lifetime shutting down, the desktop
    /// ending the session (KDE's logout, Windows' WM_QUERYENDSESSION), or our owner going — and cancelling one of
    /// them is what produced "session end cancelled by an unnamed application".</summary>
    [Theory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.OwnerWindowClosing)]
    public void ASessionEndIsNeverRefused(WindowCloseReason reason)
        => Assert.False(UI.FlyoutClosePolicy.HideInsteadOfClose(destroying: false, reason));

    /// <summary>Our own teardown (a live language switch calls <c>Destroy()</c>, which sets the flag and closes)
    /// must really close whichever reason the toolkit attaches to it — hiding there would leave the app with the
    /// UI it was tearing down.</summary>
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public void OurOwnTeardownReallyCloses(WindowCloseReason reason)
        => Assert.False(UI.FlyoutClosePolicy.HideInsteadOfClose(destroying: true, reason));

    /// <summary>The window must ASK the policy instead of cancelling every close by hand — the shape that blocked
    /// the session end. Both halves are pinned: the decision is reached through the policy, and the blanket
    /// cancel-then-hide pair is gone from the file. The reverse pattern (hiding the session end) is what the
    /// mutation above restores, and it is not spelled here because it would freeze the handler's exact wording;
    /// what must not come back is a cancel that no reason guards.</summary>
    [Fact]
    public void TheWindowDecidesThroughThePolicyAndNeverCancelsBlindly()
    {
        var source = Source("UI/MainWindow.axaml.cs");
        Assert.Contains("FlyoutClosePolicy.HideInsteadOfClose", source, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Cancel = true; CloseFlyout();", source, StringComparison.Ordinal);
    }

    /// <summary>A session end must reach the app's teardown. Without this subscription nothing exits: the lifetime
    /// is <c>OnExplicitShutdown</c> and the tray keeps the process alive with no window at all, so the app would
    /// merely stop showing a window and the desktop would still be waiting for it.</summary>
    [Fact]
    public void TheAppExitsWhenTheDesktopEndsTheSession()
    {
        Assert.Contains("windows.SessionEnding += ExitApp", Source("UI/AppController.cs"), StringComparison.Ordinal);
        Assert.Contains("public event Action? SessionEnding", Source("UI/FlyoutCoordinator.cs"), StringComparison.Ordinal);
    }
}
