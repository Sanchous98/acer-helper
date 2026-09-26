using System.Xml.Linq;

namespace AcerHelper.Application;

/// <summary>
/// The Windows run-at-logon scheduled task's two decisions, kept PURE so they can be tested on any host: what
/// command the EXISTING task holds (parsed from its language-neutral XML), and whether THIS build should
/// re-register it to point at this exe.
///
/// THE DEFECT THIS EXISTS FOR. <c>Autostart.EnsureCurrent</c> used to re-register only when the existing task
/// already named this exe but lacked the startup switch. It deliberately refused to touch a task pointing at a
/// DIFFERENT path ("so running a different build can't hijack autostart"). That guard was right about portables
/// and wrong about the MSI install: a machine whose autostart was enabled from a portable/dev tree
/// (<c>dist\windows-selfcontained</c>) kept that task after the user updated through the MSI, so the
/// MSI-installed build — the canonical one — never claimed the entry, and the old copy kept relaunching as the
/// watchdog. The rule below lets the INSTALLED build (under Program Files) migrate the entry from any other
/// path, while a portable/dev build may adopt it only once the old target is GONE — so a second portable run
/// still cannot hijack a live install's autostart.
/// </summary>
public static class AutostartPolicy
{
    /// <summary>Should the existing task be re-registered to point at <paramref name="currentExe"/>? Returns
    /// false when there is no task or its shape is unreadable: this decision only HEALS an entry that exists,
    /// it never invents one (the caller's <c>SetEnabled(true)</c> is what creates it from the user's toggle).</summary>
    /// <param name="currentExe">This build's running executable (single-file aware).</param>
    /// <param name="installed">Whether <paramref name="currentExe"/> is the MSI install (under Program Files).</param>
    /// <param name="taskExe">The task's current <c>&lt;Command&gt;</c>, or null if unparsed.</param>
    /// <param name="taskArgs">The task's current <c>&lt;Arguments&gt;</c>, or null.</param>
    /// <param name="taskExeExists">Whether the task's target executable still exists on disk.</param>
    public static bool ShouldReRegister(
        string currentExe, bool installed, string? taskExe, string? taskArgs, bool taskExeExists)
    {
        if (string.IsNullOrWhiteSpace(taskExe)) return false;   // unknown shape -> leave it alone

        if (string.Equals(taskExe, currentExe, StringComparison.OrdinalIgnoreCase))
            return !HasStartupArg(taskArgs);                    // same exe, stale args (--watch) -> heal

        // A DIFFERENT executable: only the canonical installed build may claim the entry outright; a portable
        // build may adopt it only once the old target is gone (a dead install), so a portable can never steal
        // a live install's autostart.
        return installed || !taskExeExists;
    }

    private static bool HasStartupArg(string? args)
        => args is not null && args.Contains(AppArgs.Startup, StringComparison.OrdinalIgnoreCase);

    /// <summary>Read the action out of a scheduled task's XML — the <c>&lt;Exec&gt;</c> command and arguments.
    /// Language-neutral, unlike the localised <c>schtasks /fo LIST /v</c> text (the output this replaced
    /// depended on a Russian/English column caption). Returns (null, null) when absent or unparseable.</summary>
    public static (string? exe, string? args) ReadTaskCommand(string? taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return (null, null);
        try
        {
            var exec = XDocument.Parse(taskXml)
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
            if (exec is null) return (null, null);
            var command = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
            var arguments = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value;
            return (command?.Trim(), arguments?.Trim());
        }
        catch { return (null, null); }
    }
}
