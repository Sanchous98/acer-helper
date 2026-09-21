using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AcerHelper.Tests;

/// <summary>
/// EVERY WINDOW THAT ASKS NOT TO BE IN THE TASKBAR MUST RE-ASSERT IT ON THE LIVE WINDOW.
///
/// The XAML flag alone does not reach an X11 window: measured on the owner's session with the flyout open and
/// viewable, <c>xprop _NET_WM_STATE</c> reported <c>_NET_WM_STATE_FOCUSED</c> and nothing else — neither
/// <c>_NET_WM_STATE_SKIP_TASKBAR</c> nor <c>_NET_WM_STATE_ABOVE</c>, although MainWindow.axaml sets
/// <c>ShowInTaskbar="False"</c> and <c>Topmost="True"</c>. Avalonia has the machinery
/// (<c>IWindowImpl.ShowTaskbarIcon</c>, <c>Avalonia.X11.X11Window.ShowTaskbarIcon</c>, the atom in
/// <c>X11Atoms</c>); what is missing is the moment, because a styled property set from XAML is applied before the
/// window has a platform handle and the backend does not re-apply it. <see cref="AcerHelper.UI.WindowFlags"/>
/// supplies that moment from each window's <c>Opened</c>.
///
/// THE COST, in the owner's words: "приложение можно свернуть при нажатии на иконку в панели задач, а вот развернуть
/// потом будет уже нельзя" — the flyout took a taskbar entry, could be minimised there, and the app then hid it on
/// the focus change (the tray rule), so the entry disappeared with it and only the tray menu's own item brought it
/// back. The owner's requirement is "всегда должно сворачиваться в трей", which is what this guard protects: a NEW
/// window that declares <c>ShowInTaskbar="False"</c> without calling <see cref="AcerHelper.UI.WindowFlags.Apply"/>
/// would silently reintroduce exactly that, and nothing else in the suite can see it — the property is correct in
/// the XAML, in the object model, and in every test that never maps a window.
/// </summary>
public class WindowFlagsTests
{
    /// <summary>The repository root, from the COMPILER's path: the test host's working directory is its output
    /// folder, where a relative path finds nothing.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Every window markup that opts out of the taskbar, and where its code-behind sits. Only the
    /// DECLARATION is searched for, and the code-behind must re-assert the flags — the two ends that have to agree.
    /// A rewrite that replaced the helper with an inline assignment would redden this row even though it would work;
    /// that is intended, and the repair is one call at the site (the same trade ArchitectureMapTests makes).</summary>
    [Fact]
    public void EveryWindowThatAsksForNoTaskbarEntryReAppliesItOnTheLiveWindow()
    {
        var uiDir = Path.Combine(Root(), "UI");
        var offenders = new List<string>();
        var checkedFiles = 0;

        foreach (var axaml in Directory.EnumerateFiles(uiDir, "*.axaml", SearchOption.AllDirectories))
        {
            if (!File.ReadAllText(axaml).Contains("ShowInTaskbar=\"False\"", StringComparison.Ordinal)) continue;
            checkedFiles++;
            var codeBehind = axaml + ".cs";
            if (!File.Exists(codeBehind) || !File.ReadAllText(codeBehind).Contains("WindowFlags.Apply", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(Root(), axaml));
        }

        // An empty scan would pass for the wrong reason: the flyout and both dialogs declare it today.
        Assert.True(checkedFiles >= 3, $"expected at least 3 windows opting out of the taskbar, found {checkedFiles}");
        Assert.Empty(offenders);
    }

    /// <summary>THE NON-EWMH GUARD IS ONLY REACHABLE IF THE ATOMS ARE ASKED FOR, NOT CREATED. The Linux half
    /// documents a machine without these atoms as a case it must survive — "a machine without these atoms (a
    /// non-EWMH window manager) must still get its window" — and it guards the send with
    /// <c>if (wmState == IntPtr.Zero || …) return;</c>. <c>XInternAtom</c> with <c>onlyIfExists: false</c> can
    /// never answer <c>None</c>: it CREATES the name in the server's atom table and answers the new id, so that
    /// guard was dead code a reader could only find by knowing the Xlib contract — while the atoms it created
    /// stayed on the server for the rest of its life, put there by a client that only ever reads them. The EWMH
    /// names are interned by the window manager and the desktop shell on any session that implements EWMH, so
    /// asking for them to already exist is what a client that only SENDS can honestly do.
    ///
    /// WHAT THIS ROW IS WORTH, stated plainly: it pins the SOURCE, not the round trip — libX11 is not reachable
    /// from this suite (net10.0-windows), so "the guard can now fire" is a fact about the Xlib contract and the
    /// call sites, and this is the half of it that can be checked here. The COMMENTS are stripped first, because
    /// the file's own prose names the old spelling to explain why it was wrong, and an assertion that read the
    /// prose would forbid the explanation.
    ///
    /// MUTATION that reddens it: put <c>onlyIfExists: false</c> back on any of the three calls.</summary>
    [Fact]
    public void TheLinuxHalfAsksOnlyForAtomsThatAlreadyExist()
    {
        var linux = string.Join("\n", File.ReadAllLines(Path.Combine(Root(), "UI", "WindowFlags.Linux.cs"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("onlyIfExists: false", linux, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(linux, @"XInternAtom\([^)]*onlyIfExists: true\)").Count);
    }

    /// <summary>The helper must set BOTH flags — the taskbar entry is what the owner reported, and staying above
    /// other windows is the same XAML declaration's other half. Dropping either makes the guard above vacuous:
    /// the call would still be there while the flag it exists for is gone.
    ///
    /// THE TWO OS HALVES ARE PINNED TOGETHER BUT NOT TO THE SAME MECHANISM, because they must NOT use the same
    /// one: on X11 the Avalonia setters were measured to unmap the window (see WindowFlags), so the Linux half
    /// has to ask the window manager with the EWMH message, and the row for it therefore asserts BOTH that the
    /// two atoms are named AND that the Avalonia properties are not touched — the second half is the regression
    /// this pins.</summary>
    [Fact]
    public void TheLinuxHalfAsksTheWindowManagerAndTheWindowsHalfReassertsTheProperties()
    {
        var linux = File.ReadAllText(Path.Combine(Root(), "UI", "WindowFlags.Linux.cs"));
        Assert.Contains("_NET_WM_STATE_SKIP_TASKBAR", linux, StringComparison.Ordinal);
        Assert.Contains("_NET_WM_STATE_ABOVE", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowInTaskbar =", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost =", linux, StringComparison.Ordinal);

        var windows = File.ReadAllText(Path.Combine(Root(), "UI", "WindowFlags.Windows.cs"));
        Assert.Contains("ShowInTaskbar = false", windows, StringComparison.Ordinal);
        Assert.Contains("Topmost = true", windows, StringComparison.Ordinal);
    }
}
