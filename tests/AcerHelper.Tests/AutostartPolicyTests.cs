using AcerHelper.Application;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="AutostartPolicy"/> — whether the Windows autostart scheduled task should be re-pointed at this
/// build. The defect this file pins: the old <c>EnsureCurrent</c> refused to touch a task naming a DIFFERENT
/// exe, so a machine whose task pointed at a portable/dev tree (<c>dist\windows-selfcontained</c>) kept
/// launching that old copy after the MSI install took over — the installed 0.36.2 exited and 0.35.2 came back.
///
/// WHY IT IS PURE. Every OS fact (installed vs portable, task command, whether its target still exists) is a
/// parameter, so the whole decision runs here on any host — the real code only shells out to schtasks and reads
/// a file. The migration is the assertion that matters: the INSTALLED build claims the entry from any path.
/// </summary>
public class AutostartPolicyTests
{
    private const string InstalledExe = @"C:\Program Files\AcerHelper\AcerHelper.exe";
    private const string PortableExe  = @"C:\Users\Alexander\CLionProjects\acer-helper\dist\windows-selfcontained\AcerHelper.exe";

    /// <summary>THE FIX: the installed build takes over a task the portable build registered, even while the
    /// portable copy still exists on disk. Without this the watchdog keeps relaunching the old version.
    /// MUTATION: restore the old "only if the task already names this exe" guard (<c>sameExe &amp;&amp; stale args</c>)
    /// — this reads false and goes red.</summary>
    [Fact]
    public void AnInstalledBuildClaimsATaskThatStillPointsAtAPortableCopy()
    {
        Assert.True(AutostartPolicy.ShouldReRegister(
            InstalledExe, installed: true,
            taskExe: PortableExe, taskArgs: AppArgs.Startup, taskExeExists: true));
    }

    /// <summary>The installed build also heals an entry whose target is gone (an uninstalled portable that left
    /// the task behind).</summary>
    [Fact]
    public void AnInstalledBuildClaimsATaskWhoseTargetIsGone()
    {
        Assert.True(AutostartPolicy.ShouldReRegister(
            InstalledExe, installed: true,
            taskExe: PortableExe, taskArgs: AppArgs.Startup, taskExeExists: false));
    }

    /// <summary>A portable/dev build does NOT hijack an installed build's live autostart — the guard the old code
    /// was built around, kept for the direction where it is right.
    /// MUTATION: drop the <c>installed ||</c> branch and return true for any different path — this goes red.</summary>
    [Fact]
    public void APortableBuildDoesNotStealALiveInstallsTask()
    {
        Assert.False(AutostartPolicy.ShouldReRegister(
            PortableExe, installed: false,
            taskExe: InstalledExe, taskArgs: AppArgs.Startup, taskExeExists: true));
    }

    /// <summary>But a portable build DOES adopt a task whose target is gone: a stale path left by a removed
    /// install should heal rather than point at nothing forever.</summary>
    [Fact]
    public void APortableBuildAdoptsATaskWhoseTargetIsGone()
    {
        Assert.True(AutostartPolicy.ShouldReRegister(
            PortableExe, installed: false,
            taskExe: InstalledExe, taskArgs: AppArgs.Startup, taskExeExists: false));
    }

    /// <summary>Already current — same exe, same switch — is not re-registered: a no-op at every startup that
    /// would otherwise rewrite the task (and, when elevated, needlessly touch Program Files).</summary>
    [Fact]
    public void AnAlreadyCurrentEntryIsLeftAlone()
    {
        Assert.False(AutostartPolicy.ShouldReRegister(
            InstalledExe, installed: true,
            taskExe: InstalledExe, taskArgs: AppArgs.Startup, taskExeExists: true));
    }

    /// <summary>The original heal still works: the same exe carrying an older launch command (the removed
    /// <c>--watch</c> watcher) is re-registered. Arguments are matched case-insensitively.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("--watch")]
    [InlineData("--WATCH")]
    public void ASameExeWithAStaleCommandIsHealed(string? staleArgs)
    {
        Assert.True(AutostartPolicy.ShouldReRegister(
            InstalledExe, installed: true,
            taskExe: InstalledExe, taskArgs: staleArgs, taskExeExists: true));
    }

    /// <summary>An unreadable task shape (no command parsed) is never re-registered — a policy that invented
    /// entries from a parse failure could fight a task we do not understand.</summary>
    [Fact]
    public void AnUnreadableTaskIsNotReRegistered()
    {
        Assert.False(AutostartPolicy.ShouldReRegister(
            InstalledExe, installed: true,
            taskExe: null, taskArgs: null, taskExeExists: false));
    }

    /// <summary>The task XML the <c>schtasks /query /tn ... /xml</c> output carries is parsed to its command and
    /// arguments — the language-neutral read that replaced the localised <c>/fo LIST /v</c> column text. The
    /// namespace and the blank lines schtasks emits are both present here because both are present in the real
    /// output.</summary>
    [Fact]
    public void ReadTaskCommandReadsTheExecActionFromTaskXml()
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>

            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>{PortableExe}</Command>
                  <Arguments>{AppArgs.Startup}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var (exe, args) = AutostartPolicy.ReadTaskCommand(xml);

        Assert.Equal(PortableExe, exe);
        Assert.Equal(AppArgs.Startup, args);
    }

    /// <summary>Absent or malformed XML reads as "nothing" rather than throwing — best-effort healing must not
    /// crash startup.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Task><Actions></Actions></Task>")]
    public void ReadTaskCommandIsSafeOnMissingOrMalformedInput(string? xml)
    {
        Assert.Equal((null, null), AutostartPolicy.ReadTaskCommand(xml));
    }
}
