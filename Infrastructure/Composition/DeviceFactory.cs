using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Distribution;
using AcerHelper.Infrastructure.Vendors.Dell;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>
/// Composition root (docs/vendor-plugins.md §4.1). Its job is to identify the machine and pick the backend:
/// the best-matching DOWNLOADED/CACHED vendor plugin through <see cref="VendorPluginLoader"/>, or the
/// in-host <see cref="GenericDevice"/> when no plugin is present or a matched one cannot be used. A Dell
/// machine still gets its in-host <see cref="DellDevice"/> until Phase 3 moves Dell out too.
///
/// THE HOST SHIPS NO VENDOR PLUGIN (§5, owner decision). There is deliberately no Acer branch here any more:
/// Acer was moved into <c>plugins/AcerHelper.Vendor.acer-nitro/</c> in Phase 1 so the host binary carries no
/// Acer code at all. Locally that means the app reads as Generic until a matching plugin is present — accepted.
///
/// FAILURE POSTURE (§4.1.1, §4.3 — "never a crash"). The loader never throws on a plugin-shaped failure; it
/// returns a <see cref="PluginLoadReason"/>:
///   * <see cref="PluginLoadReason.NoCandidates"/>/<see cref="PluginLoadReason.NoMatch"/> are the ordinary
///     unknown-vendor case → <see cref="GenericDevice"/> SILENTLY (the historical fallback).
///   * <see cref="PluginLoadReason.UnsupportedAbi"/>/<see cref="PluginLoadReason.AbiMismatch"/>/
///     <see cref="PluginLoadReason.AmbiguousMatch"/> (and by extension any other non-loaded reason after a
///     match) are "matched but not usable" — a broken install the user downloaded — so the fallback carries
///     <c>status.plugin_incompatible</c> on <see cref="Device.StatusMessage"/> instead of being silent.
/// The manifest gate (§3.8.4 gate 1) is the DOWNLOAD path's job (§5.3, not built yet); here the candidates are
/// what the local cache/dev-override enumeration yields, so <see cref="PluginCatalogSelector"/> is not applied
/// — there is no catalog to filter at load time.
/// </summary>
public static class DeviceFactory
{
    /// <summary>The minimum <c>ah_matches</c> confidence that counts (§3.2 calibration: 50 = manufacturer only,
    /// 80 = product substring, 100 = product+board). 50 is the design's "matched but coarse" floor: a plugin
    /// claiming only the vendor is still this machine's line rather than a stranger, and the unique-maximum rule
    /// decides between competing claims.</summary>
    internal const int ConfidenceThreshold = 50;

    /// <summary>The machine: the model <see cref="Device"/> a backend filled while probing it, with the settings
    /// that backend declared travelling on it (<see cref="Device.DeclaredSettings"/>).
    ///
    /// The declared list is COMPLETE by the time the machine is returned, and that is load-bearing rather than
    /// merely true: the settings model copies the set when it is built (<c>Settings</c>'s constructor), so a
    /// backend that declared after this line would be declaring into nothing. It holds because a backend declares
    /// during its own construction (<c>InitVendor</c>), and <see cref="GenericDevice.FinalizeComposition"/> only
    /// adjusts ports.</summary>
    public static Device Create()
    {
        var identity = MachineInfo.ReadIdentity();

        // The loader is the selection rule (§4.1 steps 2-4): enumerate the §4.1.1 search paths, load each
        // candidate behind the production binding, gate on ah_abi_version, score ah_matches against the
        // descriptor and take the unique maximum. It never throws for a plugin-shaped failure.
        var device = LoadVendorPlugin(identity);

        // Now that the backend (if any) has finalized the port set — in particular whether the performance
        // profiles are a vendor port or the generic Windows overlay — let the device make the composition
        // decisions that depend on it (e.g. the overlay-CPU-power axis; see GenericDevice).
        FinalizeComposition(device);
        return device;
    }

    /// <summary>The post-composition decision, kept as today (the same call the old factory made). There is no
    /// <c>FinalizeComposition</c> on the <see cref="Device"/> model itself — it is the backend subclasses that
    /// expose it — so this dispatches on the concrete backend. Both the plugin device and the in-host generic
    /// (and therefore Dell) devices need it.</summary>
    private static void FinalizeComposition(Device device)
    {
        switch (device)
        {
            case PluginVendorDevice plugin: plugin.FinalizeComposition(); break;
            case GenericDevice generic: generic.FinalizeComposition(); break;
        }
    }

    /// <summary>Build the device from the best-matching local plugin, or fall back per the failure posture above.
    /// The Dell branch is a deliberate Phase-1 exception (it moves in Phase 3, §6): Dell still gets its in-host
    /// backend, and only everything else reaches the loader.</summary>
    private static Device LoadVendorPlugin(MachineIdentity identity)
    {
        // Dell stays in-host until Phase 3 (§6). It is checked BEFORE the plugin scan so an in-host Dell build
        // keeps working exactly as it did, and the Acer path (which used to be the other branch here) is gone.
        if (identity.Manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true)
            return new DellDevice(identity.Product);

        var candidates = EnumerateLocalPluginCandidates();
        var descriptor = new MachineDescriptor(identity.Manufacturer, identity.Product,
                                               identity.Board, identity.BoardProduct);

        var result = new VendorPluginLoader(
            candidates,
            path => NativePluginBinding.TryLoad(path, out var binding) ? binding : null,
            PluginAbiRegistry.CreateDefault(),
            descriptor,
            AppInfo.Version,
            ConfidenceThreshold).Load();

        if (result is { IsLoaded: true, Session: { } session })
            return new PluginVendorDevice(session);

        // Everything else is GenericDevice. A reason after a candidate matched is surfaced as the §4.1.1
        // "broken install" status line; the ordinary no-plugin/no-match case stays silent.
        var device = new GenericDevice();
        if (IsIncompatibleReason(result.Reason))
            device.StatusMessage = "status.plugin_incompatible";
        return device;
    }

    /// <summary>Whether a load reason means "a plugin was found but could not be used" — the §4.1.1 status-line
    /// case — as opposed to the silent unknown-vendor fallback. Exhaustive over the enum so a new reason forces a
    /// decision here rather than defaulting into invisibility.</summary>
    private static bool IsIncompatibleReason(PluginLoadReason reason) => reason switch
    {
        PluginLoadReason.NoCandidates or PluginLoadReason.NoMatch => false,
        PluginLoadReason.UnsupportedAbi or PluginLoadReason.AbiMismatch
            or PluginLoadReason.AmbiguousMatch => true,
        // A binding/manifest/create failure is a plugin that was present and could not be driven: surface it,
        // because a downloaded plugin that silently does nothing is the exact invisible-broken-install case.
        PluginLoadReason.BindingFailed or PluginLoadReason.CreateRefused
            or PluginLoadReason.ManifestInvalid => true,
        PluginLoadReason.Loaded => false,
        _ => true,
    };

    /// <summary>The local plugin files to consider: every <c>*.dll</c>/<c>*.so</c> under the §4.1.1 search
    /// paths, in a deterministic (sorted) order. The paths are the <c>%ACERHELPER_PLUGIN_DIR%</c> dev override
    /// first, then the per-user cache — never <c>AppContext.BaseDirectory</c> (the install is read-only, §4.1.1).
    ///
    /// The advisory manifest <c>matchHint</c> pre-filter is NOT applied here: it belongs to the DOWNLOAD path
    /// (§3.2) and needs the release manifest, which does not exist locally yet. The loader's own candidate set is
    /// the handful of files a user has actually cached, so the pre-filter's purpose (do not load the whole
    /// catalogue) is already served by the cache being small.</summary>
    private static IReadOnlyList<string> EnumerateLocalPluginCandidates()
    {
        var roots = VendorPluginLoader.EnumerateSearchPaths(
            Environment.GetEnvironmentVariable("ACERHELPER_PLUGIN_DIR"),
            PerUserPluginCacheDir());

        var candidates = new List<string>();
        foreach (var root in roots)
            CollectPluginFiles(root, candidates);

        // Deterministic across runs: the loader's selection is order-independent, but a stable list keeps
        // diagnostics and tests reproducible. Sorted last so the override/cache priority only affects ties.
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return candidates;
    }

    /// <summary>Recursively collect loadable plugin modules under one search-path root, ignoring a missing or
    /// unreadable directory (a machine with no cache is the ordinary case, not an error).</summary>
    private static void CollectPluginFiles(string root, List<string> into)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".so", StringComparison.OrdinalIgnoreCase))
                    into.Add(path);
            }
        }
        catch { /* an unreadable cache dir is "no candidates", not a fault */ }
    }

    /// <summary>The per-user plugin cache root (§4.1.1, §5.3.1). <c>Environment.SpecialFolder.ApplicationData</c>
    /// resolves to <c>%AppData%</c> on Windows and the XDG data dir on Linux, which are exactly the two roots
    /// §5.3.1 names, so one read covers both. Null when the OS gives no folder, which the enumerator treats as
    /// "no cache".</summary>
    private static string? PerUserPluginCacheDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "AcerHelper", "plugins");
    }
}
