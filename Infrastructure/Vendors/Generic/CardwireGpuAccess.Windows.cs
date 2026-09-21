namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE WINDOWS HALF, and it exists so the rule stays in one place rather than two. cardwire is a Linux-side
// daemon: it gates the discrete GPU through eBPF LSM hooks, which are a Linux facility, and it owns a name on the
// system bus of that OS. There is nothing to ask for on Windows, and this file says so by handing back a port
// whose FACTS say it — <see cref="CardwireGpuAccessFacts.IsLinux"/> false — rather than by returning null and
// letting composition decide the same thing a second time.
//
// WHAT THAT BUYS: the "not Linux, nothing offered, nothing done" branch is the TESTED policy
// (CardwireGpuAccess.DutyFor), and it is also the branch this build actually runs, so the test project — which
// targets net10.0-windows and therefore compiles THIS file — exercises production code rather than a stand-in.
//
// THE RUNNER BELOW IS UNREACHABLE BY CONSTRUCTION, not by convention: DutyFor answers None for a port whose facts
// say it is not Linux, and Request checks the rule before touching the runner. It answers with a failure rather
// than throwing so that a future caller reaching it in a mistake gets a sentence instead of an exception.

internal static partial class CardwireGpuAccessHost
{
    /// <summary>The port this OS has: a capability that can never be offered, and a call that is never made.</summary>
    internal static partial CardwireGpuAccessPort Create()
        => new(static () => new CardwireGpuAccessFacts(IsLinux: false, CardwirePresent: false, DgpuPresent: false, DgpuVisible: false),
               static _ => (-1, CardwireGpuAccess.NotOnThisSystem),
               static () => (uint)Environment.ProcessId);
}
