using System.IO;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE LINUX HALF: the two things that touch this machine — the system-bus call and the probes that decide whether
// there is anything to ask for. Both live here rather than in CardwireGpuAccess.cs because this file is compiled
// into the Linux build ONLY (AcerHelper.csproj <Compile Remove="**/*.Linux.cs"> under the Windows TFM), and the
// parts worth testing were kept out of it on purpose: the rule, the argument list and the port are in the
// un-suffixed file beside this one, where the suite can reach them.
//
// NOTHING HERE READS OR WRITES A FILE OF CARDWIRE'S, and that is the point of the whole feature rather than a
// detail of this file: the request is a D-Bus call, cardwire's own configuration (/etc/cardwire, its SQLite
// app_policies table under /var/lib/cardwire) is untouched, and the daemon is never restarted. The one call made
// here — verified against cardwire 0.12.1 on the owner's machine — is described in
// CardwireGpuAccess.RequestArguments.

internal static partial class CardwireGpuAccessHost
{
    /// <summary>cardwire's PCI vendor id: NVIDIA. Chosen as the "discrete GPU" rule because on an x86 laptop it
    /// is unambiguous — there is no integrated NVIDIA GPU — while an AMD or Intel display device id cannot be
    /// told from an integrated one by its vendor alone. A machine with an AMD discrete GPU would therefore not be
    /// offered the grant; that is a deliberate narrowing (offering a grant for a GPU this app cannot identify is
    /// worse than not offering it), and the daemon's own inventory — the <c>Gpu/N</c> objects — is where a
    /// broader rule would read it from.</summary>
    private const string NvidiaVendorPci = "10de";   // as /proc/bus/pci/devices spells it (vendor+device, hex)

    /// <summary>Where the kernel lists every PCI function it has, TAB-separated: bus/device/function, then the
    /// vendor+device pair as one 8-digit hex field, then the IRQ, the BARs, and the bound driver last. Read for
    /// PRESENCE only, and it is read rather than /sys because of what the hiding actually covers: cardwire's hooks
    /// sit on the gated devices' sysfs inodes, so a hidden GPU is missing from /sys/bus/pci/devices while this
    /// file still names it (the study measured 44 entries here against 42 there).</summary>
    private const string ProcPciDevices = "/proc/bus/pci/devices";

    /// <summary>Where the kernel exposes the devices this process can see. Read for VISIBILITY: if an NVIDIA
    /// device is listed here, the GPU is not hidden from us and there is nothing to ask for.</summary>
    private const string SysPciDevices = "/sys/bus/pci/devices";

    /// <summary>The system's port: busctl for the call, the two inventories above for the facts.</summary>
    internal static partial CardwireGpuAccessPort Create()
        => new(Probe, Busctl.Call, static () => (uint)Environment.ProcessId);

    /// <summary>This machine, as far as the app can see it — see <see cref="CardwireGpuAccessFacts"/> for why
    /// presence and visibility are two separate readings.
    ///
    /// ONE busctl CALL PER READING, and it is the cheapest honest question: <c>busctl --system status NAME</c>
    /// asks the bus whether the name is owned right now (exit 0) rather than whether the software is installed
    /// (a file in /usr/share/dbus-1 would answer that, and would answer it wrongly for a daemon that has
    /// crashed). Measured on the owner's machine: exit 0 with cardwire running, exit 1 for a name nobody owns.
    /// No timeout of its own is needed — <c>Busctl.Call</c> waits 4 s and reports a stuck process as a failure,
    /// which reads here as "not present", i.e. the same answer as a daemon that is not there.</summary>
    private static CardwireGpuAccessFacts Probe()
    {
        var (status, _) = Busctl.Call("status", CardwireGpuAccess.Service);
        var present = NvidiaPresentInProc();
        return new CardwireGpuAccessFacts(
            IsLinux: OperatingSystem.IsLinux(),
            CardwirePresent: status == 0,
            DgpuPresent: present,
            DgpuVisible: present && NvidiaVisibleInSysfs());
    }

    /// <summary>Whether an NVIDIA display device is on the machine at all, hidden or not.</summary>
    private static bool NvidiaPresentInProc()
    {
        try
        {
            foreach (var line in File.ReadLines(ProcPciDevices))
            {
                // Field 1 is the vendor+device pair, e.g. "10de2f58" for the dGPU and "10de2f80" for the audio
                // function on the same board. The AUDIO function is deliberately not excluded: it is hidden by
                // the same block, so it answers the same question, and a display-class test would need a third
                // field /proc/bus/pci/devices does not carry (it has no class column).
                var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1 && fields[1].StartsWith(NvidiaVendorPci, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* no /proc entry (not Linux, restricted): answer "nothing there", which offers nothing */ }
        return false;
    }

    /// <summary>Whether this process can actually see an NVIDIA device — the half that the block removes.
    ///
    /// A THROWING ENUMERATION ANSWERS FALSE, and that is the honest direction: the hook may make the directory
    /// itself unreadable (it returns -ENOENT for the gated inodes and their entries), and "I cannot see one" is
    /// exactly what the gate needs to hear. Only reached when presence was already established, so a machine with
    /// no NVIDIA hardware never pays for the walk.</summary>
    private static bool NvidiaVisibleInSysfs()
    {
        try
        {
            foreach (var device in Directory.EnumerateDirectories(SysPciDevices))
            {
                var vendor = File.ReadAllText(Path.Combine(device, "vendor")).Trim();   // "0x10de"
                if (vendor.Equals("0x" + NvidiaVendorPci, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }
}
