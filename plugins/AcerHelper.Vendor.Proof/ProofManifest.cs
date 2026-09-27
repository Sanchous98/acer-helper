using System.Text.Json;
using AcerHelper.Infrastructure.Plugins;

namespace AcerHelper.Vendor.Proof;

/// <summary>
/// THE PROOF PLUGIN'S FIXED PROBE MANIFEST (docs/vendor-plugins.md §3.4, §6 Phase 0 item 4) — the one thing
/// <c>ah_create</c> returns, and deliberately tiny: ONE performance profile, ONE fan capability, and NO declared
/// settings. pluginId "proof", vendor "proof", vendorName "Proof Plugin".
///
/// IT IS BUILT THROUGH THE SHARED GENERATED CONTEXT, not a hand-typed JSON literal. <c>PluginManifest</c> and
/// <c>PluginJsonContext</c> are source-included into this plugin from <c>Infrastructure/Plugins/</c> because they
/// are DEPENDENCY-FREE (they reference only each other and System.Text.Json's source generator), so the proof
/// plugin emits exactly the wire shape the host's generated context decodes (§3.3). A literal would be a second
/// copy of the schema free to drift from §3.4 the moment a member moved. The only literal kept in this plugin is
/// the two-field <c>ah_matches</c> body (see <c>ProofPlugin.BuildMatchResponse</c>), which is not a manifest.
/// </summary>
internal static class ProofManifest
{
    /// <summary>Serialises the fixed manifest to UTF-8. The caller (<c>ah_create</c>) owns the returned byte
    /// array and copies it into a native buffer the host frees with <c>ah_free</c>.</summary>
    internal static byte[] Build()
    {
        var manifest = new PluginManifest
        {
            // Echoed from the generated const so the manifest's abi and ah_abi_version() can never disagree; the
            // host treats the export as authoritative and this field as a cross-check (§3.4).
            Abi = $"{ProofApi.Major}.{ProofApi.Minor}",
            PluginId = ProofPlugin.PluginId,
            Vendor = "proof",
            VendorName = "Proof Plugin",
            Capabilities = new CapabilitiesManifest
            {
                PowerProfiles = new PowerProfilesManifest
                {
                    All = [new ProfileManifest { Id = "1", DisplayName = "proof.profile" }],
                    CurrentId = "1",
                },
                Fan = new FanManifest
                {
                    Capability = new FanCapabilityManifest { HasMax = true, HasCustom = false, HasGpuFan = false },
                },
                // Everything else stays null: null means "this machine does not have it" (§3.4), and the proof
                // plugin declares no sensors, rgb, hotkeys, gpuMux, battery properties or settings.
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(manifest, PluginJsonContext.Default.PluginManifest);
    }
}
