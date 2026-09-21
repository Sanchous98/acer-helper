using System.Globalization;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// The app's own request to USE the machine's discrete GPU, made of the daemon that was hiding it from us
// (cardwire, https://github.com/OpenGamingCollective/cardwire — see docs/cardwire-gpu-access.md).
//
// WHY THIS FILE IS UN-SUFFIXED, in the words of the rule the exemplars state (AcerFanPort.cs, AcerProfilePorts.cs):
// the test project targets net10.0-windows while AcerHelper.csproj excludes **/*.Linux.cs from that TFM, so
// anything left in a Linux file cannot be reached by the suite at all. Everything that DECIDES lives here — when
// the capability may be offered, what the request is, and what a refusal means — and the two things that TOUCH
// the machine (the busctl call and the filesystem probe) arrive as delegates or from the per-OS host, so a test
// drives every branch without a daemon, a GPU or a filesystem.
//
// THE MECHANISM, verified read-only against cardwire 0.12.1 and re-verified on hardware (the study and the
// commands are in docs/cardwire-gpu-access.md):
//   * cardwired hides the discrete GPU from clients that have not been allowed to use it, with an eBPF LSM hook
//     that answers -ENOENT for syscalls touching the device's inodes and its sysfs directory entries. The block
//     is per-CLIENT (by pid), not per-uid: the study measured 44 PCI devices in /proc/bus/pci/devices against 42
//     in /sys/bus/pci/devices, and root saw the same 42 as the user did.
//   * RequestProcessAccess(pid u, policy s, gpu_id u) with policy "Allow_dGPU" inserts an ALREADY-RUNNING pid
//     into the daemon's eBPF allow-map immediately: no config file, no database row, no daemon restart. The
//     accepted policy strings are case-sensitive; "Allow_dGPU" is the one runtime grant (the others are
//     "Default" — a documented no-op — and the two "Force_*" forms).
//   * Children inherit: the hook also accepts a pid whose REAL PARENT is in the allow-map, so a probe or helper
//     the app spawns is allowed with it.
//   * THERE IS NO REVOKE METHOD. The grant ends when the process execs or exits, and not before — which is why
//     the consent prompt says so rather than promising a switch that takes it back.
//
// WHAT IS DELIBERATELY NOT USED: SetAppPolicy(app_id s, policy i), the persistent form. It writes a row into
// cardwire's SQLite app_policies table keyed by the LOWERCASE BINARY NAME — system state changed for every
// future run and for every other binary of the same name, on a key any process can spoof by renaming itself. The
// owner's rule is that permissions are consented to and minimal, so the runtime grant is the one this app asks
// for, and the consent prompt states exactly that.

/// <summary>What the app's GPU-access request is, when it may be made at all, and what a refusal reads like.
///
/// THE WHOLE RULE IS ONE FUNCTION. <see cref="DutyFor"/> answers "what does this run owe the user's remembered
/// choice", and everything else here is a reading of it: <see cref="ShouldOffer"/> is the same decision asked as
/// a yes/no question for the Options row, and <see cref="Request"/> asks it once more before touching the bus, so
/// a gate cannot be enforced in one place and forgotten in another.</summary>
internal static class CardwireGpuAccess
{
    /// <summary>The daemon's well-known name on the SYSTEM bus. The shipped D-Bus policy grants group
    /// <c>wheel</c> send/receive access to it and there is no polkit action, so a normal user in that group can
    /// call it with no password prompt — verified on the owner's machine, where the call succeeds as
    /// <c>alexander</c> without elevation.</summary>
    internal const string Service = "org.opengamingcollective.cardwire";

    /// <summary>The daemon's object path.</summary>
    internal const string Object = "/org/opengamingcollective/cardwire";

    /// <summary>The interface the grant lives on. The method is NOT on the daemon's Config or Gpu interfaces,
    /// whose properties are the persistent side of cardwire rather than the per-process one.</summary>
    internal const string Interface = "org.opengamingcollective.cardwire.SmartPolicy";

    /// <summary>The method, whose signature is <c>usu</c>: the pid, the policy word, the gpu id.</summary>
    internal const string Method = "RequestProcessAccess";

    /// <summary>The policy word, byte-for-byte: the strings are case-sensitive, and this is the ONLY value that
    /// grants a running process access without writing anything down. <c>"Default"</c> is a documented no-op and
    /// the <c>"Force_*"</c> forms choose an offload mode this app has no opinion about.</summary>
    internal const string Policy = "Allow_dGPU";

    /// <summary>The gpu id this app asks about. 0 is the daemon's own "every GPU it gates" value — the hook's
    /// allow-map lookup keys on the pid alone, and its per-GPU comparison treats 0 as a match — so the app asks
    /// for the discrete GPU without naming an index, which is what a machine whose GPU numbering the app does
    /// not own should do. (The daemon numbers them: on the AN18-61 the NVIDIA dGPU is cardwire's GPU 1, which is
    /// also the index its own log lines name.)</summary>
    internal const uint EveryGpu = 0;

    /// <summary>Why there is nothing to ask for, when the machine is not one cardwire can gate at all.</summary>
    internal const string NotOnThisSystem = "cardwire is a Linux-side daemon";

    /// <summary>Why there is nothing to ask for when the daemon the user consented to is no longer running.
    /// SHARED, because two places say it: the row's refusal when the world changed between the UI being built
    /// and the click, and the startup report when the remembered choice cannot be honoured.</summary>
    internal const string MissingService = "cardwire is not on the system bus";

    /// <summary>Why there is nothing to ask for when the GPU is already ours: the block is what the request
    /// would lift, so on a machine that can already see the device the request is not a permission being asked
    /// for but a no-op.</summary>
    internal const string AlreadyVisible = "the discrete GPU is already visible to this process";

    /// <summary>Why there is nothing to ask for on a machine with no discrete NVIDIA GPU: there is nothing
    /// hidden to reveal. (The probe can tell the difference — see <see cref="CardwireGpuAccessFacts.DgpuPresent"/>
    /// — so the app does not offer a GPU it does not have.)</summary>
    internal const string NoDiscreteGpu = "this machine has no discrete NVIDIA GPU";

    /// <summary>What this run owes the user's REMEMBERED choice.
    ///
    /// THE FOUR BRANCHES, and each is a decision rather than a filter:
    /// <list type="bullet">
    /// <item>The choice is OFF — the default, and the only case in which the app touches nothing at all. No
    /// probe is acted on, no call is made, no message is shown.</item>
    /// <item>Not Linux: cardwire is a Linux-side daemon, so the capability does not exist here and the
    /// preference (which may have travelled in a copied settings file) is not a request this OS can honour.</item>
    /// <item>The discrete GPU is already VISIBLE to this process: there is nothing to ask for. That is not a
    /// failure and is not reported — it is the ordinary state of a machine whose cardwire is in its Hybrid mode,
    /// and telling the user about it every start would be noise about nothing.</item>
    /// <item>No discrete GPU at all: nothing to ask about, same silence.</item>
    /// <item><see cref="ReportMissingService"/> — the user asked and the daemon is GONE. This is the one shut
    /// gate that is worth saying out loud: a permission the user granted themselves has stopped being honoured,
    /// and a silent start would leave them believing the app can see a GPU it cannot.</item>
    /// </list>
    ///
    /// NOTHING HERE RETRIES. The duty is consulted at startup and when the user flips the switch, never from the
    /// refresh loop, so a daemon that is down (or an interface that changed under us) produces ONE message and no
    /// traffic.</summary>
    internal static CardwireGpuAccessDuty DutyFor(in CardwireGpuAccessFacts facts, bool enabled)
        => !enabled || !facts.IsLinux ? CardwireGpuAccessDuty.None
         : facts.DgpuVisible          ? CardwireGpuAccessDuty.None                // nothing to ask for
         : !facts.CardwirePresent     ? CardwireGpuAccessDuty.ReportMissingService  // asked for, service gone
         : !facts.DgpuPresent         ? CardwireGpuAccessDuty.None                  // nothing to ask about
         : CardwireGpuAccessDuty.Ask;

    /// <summary>Whether there is something to ask for — the same decision as <see cref="DutyFor"/>, read as the
    /// boolean the OFFER turns on. One rule, two readings: a second expression here is how a gate ends up
    /// enforced in the row but not in the call.</summary>
    internal static bool ShouldOffer(in CardwireGpuAccessFacts facts)
        => DutyFor(facts, enabled: true) == CardwireGpuAccessDuty.Ask;

    /// <summary>Whether the Options row belongs on screen: where there is something to ask for, OR where the user
    /// has already asked.
    ///
    /// THE SECOND HALF IS NOT A LOOPHOLE. A choice made while the GPU was hidden stays in the settings file after
    /// the machine stops hiding it — cardwire switched to Hybrid, or the daemon was removed — and a row that
    /// vanished with the offer would leave that choice with no switch to turn it off. What must not appear on a
    /// machine with nothing to ask for is the OFFER (the consent prompt and the call), and that is
    /// <see cref="ShouldOffer"/>.</summary>
    internal static bool RowBelongs(in CardwireGpuAccessFacts facts, bool enabled)
        => enabled || ShouldOffer(facts);

    /// <summary>Why the gate is shut, in this port's own words — for the row's refusal and for the startup
    /// report. Null when there is something to ask for. The reasons are English, like every other transport's
    /// (<c>AcerFanPort</c>, the WMI layer): the UI composes "«row label» failed: «reason»" from them and
    /// translates only the frame, because a daemon's refusal arrives in its own words and cannot be a lookup
    /// key.</summary>
    internal static string? ShutReason(in CardwireGpuAccessFacts facts)
        => ShouldOffer(facts) ? null
         : !facts.IsLinux      ? NotOnThisSystem
         : facts.DgpuVisible   ? AlreadyVisible
         : !facts.DgpuPresent  ? NoDiscreteGpu
         : MissingService;

    /// <summary>The exact argument list handed to <c>busctl</c> — the ONE call this feature makes.
    ///
    /// THE SIGNATURE IS PASSED EXPLICITLY ("usu"), and that is a measured requirement rather than tidiness:
    /// <c>busctl</c> infers a signature from bare arguments only for the simplest shapes, and the inferred call
    /// for this method is refused with "Too many parameters for signature" — verified on the owner's machine,
    /// where the inline form failed and the explicit one succeeded. The <c>--system</c> prefix is <c>Busctl</c>'s
    /// own (PowerProfiles.Linux.cs): the daemon is on the system bus, not the session's.
    ///
    /// THE PID IS THIS PROCESS'S, and it has to be: the grant is per-process and the daemon inserts exactly the
    /// pid it is given. It is a parameter rather than <c>Environment.ProcessId</c> read here so a test can hold
    /// the argument list to the pid it asked for.</summary>
    internal static string[] RequestArguments(uint pid) =>
    [
        "call", Service, Object, Interface, Method, "usu",
        pid.ToString(CultureInfo.InvariantCulture),
        Policy,
        EveryGpu.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>The daemon's refusal, as the port reports it. <c>busctl</c> writes the D-Bus error to stderr and
    /// <c>Busctl.Call</c> folds both streams into one string, so this trims it to a single line for the status
    /// line. A refusal is the DAEMON's sentence (cardwire answers "process doesn't exist" for a pid it cannot
    /// see) or the bus's, and neither is ours to reword — what this file owns is the frame around them.</summary>
    internal static string Describe(int code, string output)
    {
        var text = string.Join("; ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? $"busctl exited with {code}" : text;
    }
}

/// <summary>What the app can see of this machine's cardwire/GPU situation — the gate's ENTIRE input, so the
/// decision above is a pure function of four facts a test can write down.
///
/// THE TWO GPU FACTS ARE NOT ONE. <see cref="DgpuPresent"/> is "the machine has a discrete NVIDIA GPU" and
/// <see cref="DgpuVisible"/> is "this process can see it". They come from two different inventories on purpose,
/// because the hiding is exactly the difference between them: cardwire's hooks cover the gated devices' sysfs
/// nodes, so a hidden GPU is absent from /sys/bus/pci/devices while /proc/bus/pci/devices still names it (the
/// study's 44-vs-42 measurement). With one fact the app could not tell "hidden" from "not there", and would
/// offer the grant on laptops with no discrete GPU at all.
///
/// <para><see cref="IsLinux"/> is here for the same reason the GPU vendor is: the preference round-trips in a
/// settings file, so a run can hold it on a platform where the capability does not exist, and the decision
/// belongs in the rule rather than in the file that happens to be compiled.</para></summary>
internal readonly record struct CardwireGpuAccessFacts(bool IsLinux, bool CardwirePresent, bool DgpuPresent, bool DgpuVisible);

/// <summary>What a run owes the user's choice. See <see cref="CardwireGpuAccess.DutyFor"/> for the branches —
/// <see cref="None"/> is the ordinary answer and means "touch nothing", including the two cases where the choice
/// is on but there is nothing to ask.</summary>
internal enum CardwireGpuAccessDuty
{
    None,
    Ask,
    ReportMissingService,
}

/// <summary>
/// The capability, with both of its I/O edges handed in: the facts about this machine, the runner that shells
/// out to busctl, and this process's pid. Nothing here is static state, so a test builds a port over whatever
/// world it wants to describe and drives the whole feature without a daemon, a GPU or a filesystem.
///
/// THE REAL PORT IS BUILT BY THE OS (see <see cref="CardwireGpuAccessHost"/>), and that split is the repo's usual
/// one: the portable TFM compiles exactly one of <c>*.Linux.cs</c>/<c>*.Windows.cs</c>, so the file that names
/// the machine is the file that may only exist on one of them.</summary>
internal sealed class CardwireGpuAccessPort(Func<CardwireGpuAccessFacts> facts,
                                           Func<string[], (int code, string output)> busctl,
                                           Func<uint> ownPid)
{
    /// <summary>Whether the Options row belongs on screen. See <see cref="CardwireGpuAccess.RowBelongs"/>.
    ///
    /// EVERY READER PROBES AGAIN rather than caching the facts it was built with, because the world moves under
    /// the app: cardwire switches modes, leaves the bus and comes back, and the GPU can appear without either (a
    /// driver bind) — and the two moments that matter (a UI being built, a click) are minutes apart.</summary>
    internal bool RowBelongs(bool enabled) => CardwireGpuAccess.RowBelongs(facts(), enabled);

    /// <summary>What a run with this choice owes. See <see cref="CardwireGpuAccess.DutyFor"/>.</summary>
    internal CardwireGpuAccessDuty Duty(bool enabled) => CardwireGpuAccess.DutyFor(facts(), enabled);

    /// <summary>
    /// Ask cardwire to let THIS process see the discrete GPU — the one call this feature makes, and the only
    /// place in the app that touches the daemon.
    ///
    /// IT RE-CHECKS THE GATE, so the promise "nothing is asked for where there is nothing to ask for" holds at
    /// the moment of the call and not only at the moment the row was built: the world can change between the two
    /// (the user switches cardwire to Hybrid while the Options drawer is open), and the answer then is a refusal
    /// with the reason, not a call the gate would have refused.
    ///
    /// A FAILURE IS A RETURNED REASON, never an exception: the daemon may be gone, the bus may refuse the call
    /// (not in group wheel), or the interface may have changed under a newer cardwire — all three arrive here as
    /// a non-zero exit with text, which the caller shows. Nothing is retried; the next moment the duty is
    /// consulted is the next startup or the user's next click.
    ///
    /// THERE IS NO UN-DO, and this method is where that stops being a surprise: cardwire offers no revoke, so a
    /// successful call is in force until this process exits. The consent prompt says exactly that.</summary>
    internal (bool ok, string? error) Request()
    {
        var now = facts();
        switch (CardwireGpuAccess.DutyFor(now, enabled: true))
        {
            case CardwireGpuAccessDuty.None:
                return (false, CardwireGpuAccess.ShutReason(now));
            case CardwireGpuAccessDuty.ReportMissingService:
                return (false, CardwireGpuAccess.MissingService);
        }

        var (code, output) = busctl(CardwireGpuAccess.RequestArguments(ownPid()));
        return code == 0 ? (true, null) : (false, CardwireGpuAccess.Describe(code, output));
    }
}

/// <summary>
/// The machine's own port, built by the file that knows the OS (<c>CardwireGpuAccess.Linux.cs</c> /
/// <c>CardwireGpuAccess.Windows.cs</c>). Declared here so the composition root and the Options assembler name
/// one member for both builds — the same shape as <c>MachineInfo</c>, which is a partial static class whose
/// implementation each OS supplies.
///
/// IT RETURNS A PORT ON EVERY PLATFORM RATHER THAN NULL, deliberately: on Windows there is no daemon to ask,
/// and that fact is one of the four the RULE takes (<see cref="CardwireGpuAccessFacts.IsLinux"/>), so the
/// refusal lives in the tested policy instead of being a second, untestable statement of the same rule in
/// composition.</summary>
internal static partial class CardwireGpuAccessHost
{
    /// <summary>This OS's port. Building one touches nothing — the facts are read when they are asked for.</summary>
    internal static partial CardwireGpuAccessPort Create();
}
