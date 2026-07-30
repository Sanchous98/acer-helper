using System.Diagnostics;
using Microsoft.Win32;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Detects and, on request, installs <b>PawnIO</b> (pawnio.eu) — the signed ring-0 gateway the CPU-undervolt
/// feature needs (see <see cref="RyzenCurveOptimizer"/>). It is a third-party kernel driver by namazso, not part
/// of this app, and it is installed ONLY after the user says yes.
///
/// Why the app ships the installer at all: the signed edition is proprietary freeware, but its own binary carries
/// an express grant — "This installer can be redistributed unmodified." — and the author's module documentation
/// states the same ("Official and unrestricted binary editions: Proprietary, however redistribution of installer
/// is allowed"). That permission covers exactly one thing: shipping <c>PawnIO_setup.exe</c> byte-for-byte.
/// Unpacking PawnIO.sys or PawnIOLib.dll out of it and laying them down as our own files is NOT covered, so the
/// payload is invoked, never opened. The author's stated preference is that apps merely point users at
/// pawnio.eu, so the offer names what it installs and where it comes from rather than burying it.
///
/// Three rules this class will not break, because PawnIO is a SHARED dependency — UXTU, ZenTimings, FanControl and
/// LibreHardwareMonitor use the same driver:
///   * install only when it is absent (the installer refuses over an existing install anyway, and silently),
///   * never upgrade — a newer driver under another tool's feet is not ours to swap,
///   * never uninstall, including when AcerHelper itself is removed.
///
/// Detection is the ARP registry key, NOT the device handle: opening \\?\GLOBALROOT\Device\PawnIO tells you the
/// driver is *usable right now* (it fails when not elevated, or when the node is stopped), which is a different
/// question from whether it is installed — and answering it wrongly would fire the installer at a machine that
/// already has PawnIO, where it fails with no UI at all.
/// </summary>
internal static class PawnIoInstaller
{
    // ARP subkey. The name is the literal string "PawnIO" (the installer is not an MSI, so this is not a product
    // GUID). Written to the 64-bit view; the 32-bit twin is checked too because older installers wrote there.
    private const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>The redistributed installer, shipped next to the app. Absent in a local dev build — CI fetches a
    /// pinned, checksum-verified copy into the publish tree.</summary>
    private const string SetupFile = "PawnIO_setup.exe";

    private const string Site = "https://pawnio.eu/";

    // -install -silent is the invocation the author's own winget manifest uses for unattended installs. NEVER add
    // -unrestricted: that installs the test-signed edition meant for module development, which needs test-signing
    // mode and a reboot.
    private const string InstallArgs = "-install -silent";

    /// <summary>Installed version as the driver's own ARP entry reports it (e.g. "2.2.0.0"), or null when PawnIO is
    /// not installed. Reads HKLM, so it works unelevated. Never throws.</summary>
    public static string? InstalledVersion
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hklm.OpenSubKey(ArpKey);
                    var v = key?.GetValue("DisplayVersion") as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                    if (key != null) return "";   // present but no version recorded — still "installed"
                }
                catch { /* denied or missing — try the other view */ }
            }
            return null;
        }
    }

    public static bool Installed => InstalledVersion != null;

    /// <summary>Whether this build actually carries the installer. False in a local build, so the offer is hidden
    /// rather than shown and then failing.</summary>
    public static bool PayloadAvailable => File.Exists(SetupPath);

    private static string SetupPath => Path.Combine(AppContext.BaseDirectory, SetupFile);

    /// <summary>Human-readable source, for the consent prompt.</summary>
    public static string SourceUrl => Site;

    /// <summary>Run the bundled installer and wait for it. Returns null on success, otherwise a message to show.
    /// Refuses to do anything if PawnIO is already installed (see the class remarks) or the payload is missing.
    /// Blocking — call it off the UI thread. Never throws.</summary>
    public static string? Install()
    {
        if (Installed) return null;                      // already there: success by definition, and never upgrade
        if (!PayloadAvailable) return $"the installer is not bundled in this build — get PawnIO from {Site}";

        try
        {
            // The setup is a GUI-subsystem binary that self-elevates; -silent suppresses ALL of its UI, including
            // its error message boxes, so the exit code is the only channel it has left. AcerHelper already runs
            // elevated, so no second UAC prompt appears.
            using var p = Process.Start(new ProcessStartInfo(SetupPath, InstallArgs) { UseShellExecute = false });
            if (p == null) return "could not start the PawnIO installer";
            p.WaitForExit();

            return p.ExitCode switch
            {
                0 => null,
                // ERROR_SUCCESS_REBOOT_REQUIRED. The driver is installed; the undervolt section appears after a
                // restart, so say that rather than reporting a failure.
                3010 => "PawnIO installed — restart Windows to finish",
                // The exit-code table is not documented anywhere upstream, and -silent discards the installer's own
                // diagnostics, so the number is all there is to report. Give it verbatim plus where to go.
                var code => $"the PawnIO installer failed (exit code {code}) — try installing it from {Site}",
            };
        }
        catch (Exception ex) { return $"could not run the PawnIO installer ({ex.GetType().Name})"; }
    }
}

/// <summary>The <see cref="IDriverSetup"/> port over <see cref="PawnIoInstaller"/>, so the cross-platform UI can
/// offer the install without knowing what PawnIO is. <see cref="TryCreate"/> returns null unless the driver is
/// actually relevant here — a CPU the undervolt supports — and the build carries the installer, so nobody is asked
/// to install a kernel driver they cannot use.</summary>
internal sealed class PawnIoSetup : IDriverSetup
{
    public string Name => "PawnIO";
    public string SourceUrl => PawnIoInstaller.SourceUrl;
    public string Purpose => "CPU undervolt";
    public bool Installed => PawnIoInstaller.Installed;
    public string? Install() => PawnIoInstaller.Install();

    public static PawnIoSetup? TryCreate()
        => RyzenCurveOptimizer.SupportedCpu && PawnIoInstaller.PayloadAvailable ? new PawnIoSetup() : null;
}
