using System.Diagnostics;

namespace AcerHelper.Tests;

/// <summary>
/// The RyzenSMU PawnIO module is fetched during the build by <c>build/fetch-ryzensmu.ps1</c> (invoked from the
/// FetchRyzenSmu target in AcerHelper.csproj). That script's release-asset selection, digest parsing and
/// module-export checks are exercised here through its <c>-SelfTest</c> mode, so the build step's decision logic
/// is covered <b>without touching the network</b> — the live fetch is deliberately not a unit test.
/// </summary>
public sealed class RyzenSmuFetchScriptTests
{
    [Fact]
    public void SelfTest_passes()
    {
        // Walk up from the test output folder to the checkout that holds the script.
        var script = FindUp(Path.Combine("build", "fetch-ryzensmu.ps1"));
        Assert.True(script is not null, "build/fetch-ryzensmu.ps1 was not found above the test output folder");

        // Windows-only script; on a host without Windows PowerShell, skip rather than fail.
        var powershell = WindowsPowerShellPath();
        if (powershell is null) return;

        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script!);
        psi.ArgumentList.Add("-SelfTest");

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0,
            $"fetch-ryzensmu.ps1 -SelfTest exited {process.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{stderr}");
        Assert.Contains("SELFTEST-PASS", stdout);
    }

    private static string? FindUp(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? WindowsPowerShellPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrEmpty(systemRoot)) return null;
        var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }
}
