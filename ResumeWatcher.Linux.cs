using System.Diagnostics;

namespace AcerHelper;

// Linux resume hook: systemd-logind broadcasts `PrepareForSleep(false)` on the system bus when the machine
// wakes from suspend/hibernate. Unlike the one-shot method calls the Busctl helper makes, a signal must be
// STREAMED, so we tail `gdbus monitor` — a normal unprivileged D-Bus client (receiving a broadcast signal needs
// only an AddMatch, no root/eavesdrop, unlike `busctl monitor`). gdbus (glib) is present on essentially all
// desktop Linux; if it's missing or the spawn fails this degrades to a no-op and the RGB is re-applied on the
// next profile switch / app restart. We deliberately don't use Avalonia's transitive Tmds.DBus directly (the
// csproj warns it must stay purely transitive). See ResumeWatcher.cs.
internal sealed partial class ResumeWatcher
{
    private Process? _monitor;
    private volatile bool _stopped;

    private partial void Subscribe()
    {
        try
        {
            var psi = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "monitor", "--system", "--dest", "org.freedesktop.login1",
                                      "--object-path", "/org/freedesktop/login1" })
                psi.ArgumentList.Add(a);

            _monitor = Process.Start(psi);
            if (_monitor == null) return;
            _monitor.OutputDataReceived += OnLine;
            _monitor.ErrorDataReceived += (_, _) => { };   // drain stderr so a full pipe can't block gdbus
            _monitor.BeginOutputReadLine();
            _monitor.BeginErrorReadLine();
        }
        catch { _monitor = null; }   // no gdbus / no logind -> stay a no-op
    }

    private partial void Unsubscribe()
    {
        _stopped = true;
        try { if (_monitor is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { _monitor?.Dispose(); } catch { /* best-effort */ }
        _monitor = null;
    }

    // gdbus prints one line per signal, e.g.:
    //   /org/freedesktop/login1: org.freedesktop.login1.Manager.PrepareForSleep (false)
    // The boolean is `false` on resume (`true` is the pre-sleep edge, which we ignore).
    private void OnLine(object? sender, DataReceivedEventArgs e)
    {
        if (_stopped || e.Data is not { } line) return;
        if (line.Contains("PrepareForSleep") && line.Contains("(false)")) _onResume();
    }
}
