using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Vendors.Generic;
// The port's own member is named Capability (IFanControl.Capability), which shadows the ABI class of the same
// name inside this type; the alias keeps both readable at the call site.
using AbiCapability = AcerHelper.Infrastructure.Plugins.Abi.Capability;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// The fan-control adapter (docs/vendor-plugins.md §3.5, §4.2): implements <see cref="IFanControl"/> over
/// <c>ah_invoke(Capability.Fan, …)</c>, mirroring the in-host <c>FanPort</c> (Infrastructure/Vendors/Generic/
/// DelegatePorts.cs:43-51).
///
/// <see cref="Capability"/> comes from the MANIFEST, not a call (§4.2), because which fan controls the machine
/// offers is a property of the machine, not of the current moment. <see cref="SetMode"/> and
/// <see cref="SetCustomSpeeds"/> forward as <c>Op.SetMode</c>/<c>Op.SetCustomSpeeds</c> (§3.5) and report both
/// halves of the write through <see cref="LastError"/> — null on success, the refusal's own words otherwise —
/// preserving the "reason belongs to THIS call" rule (§4.2, LaptopService.cs:121-149).
///
/// The ORDER (behaviour before speeds) is kept by the CALLER: <c>LaptopService.ApplyFan</c> calls
/// <see cref="SetMode"/> then <see cref="SetCustomSpeeds"/> and the plugin executes each write exactly as the
/// vendor's own port did (§3.7). The adapter adds no orchestration of its own.
///
/// <see cref="FanMode"/>'s names are the wire strings (§3.5 <c>{"mode":"Custom"}</c>) — the enum's own spelling,
/// which is also the persisted form (Domain/Models.cs:26). Mapping the name rather than the numeric value keeps
/// the boundary readable and lets a future major renumber the enum without changing the payload.
/// </summary>
internal sealed class PluginFanControl : IFanControl
{
    private readonly IPluginSession _session;

    public PluginFanControl(IPluginSession session, FanManifest manifest)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(manifest);

        var capability = manifest.Capability;
        Capability = new FanCapability(
            HasMax: capability?.HasMax ?? false,
            HasCustom: capability?.HasCustom ?? false,
            HasGpuFan: capability?.HasGpuFan ?? false);
    }

    public string? LastError { get; private set; }

    /// <summary>Which fan controls the machine offers, from the manifest (§4.2). Auto is always implied, so it
    /// is not a flag here.</summary>
    public FanCapability Capability { get; }

    /// <summary><c>Op.SetMode</c> (§3.5): <c>{"mode":"Custom"}</c>. The field is "mode", not "id", so it is built
    /// here rather than through the shared <c>PluginBodies.Id</c> helper — each capability's §3.5 column stays
    /// readable at its own call site.</summary>
    public bool SetMode(FanMode mode)
    {
        var request = new JsonObject { ["mode"] = mode.ToString() }.ToJsonString();
        return Invoke(Operation.Fan.SetMode, request).Status == AbiStatus.Ok;
    }

    /// <summary><c>Op.SetCustomSpeeds</c> (§3.5): <c>{"cpu":70,"gpu":70}</c>. The percentages are already bytes
    /// in the port contract, so they cross as the numbers §3.5 shows.</summary>
    public bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent)
    {
        var request = new JsonObject
        {
            ["cpu"] = (int)cpuPercent,
            ["gpu"] = (int)gpuPercent,
        }.ToJsonString();
        return Invoke(Operation.Fan.SetCustomSpeeds, request).Status == AbiStatus.Ok;
    }

    /// <summary>Run one Fan op and fold its status into <see cref="LastError"/>: null on Ok, the refusal's own
    /// words otherwise. The reason is parsed from THIS result, never read off a shared field afterwards.</summary>
    private PluginCallResult Invoke(uint op, string request)
    {
        var result = _session.Invoke(AbiCapability.Fan, op, request);
        LastError = result.Status == AbiStatus.Ok ? null : PluginBodies.ErrorOf(result);
        return result;
    }
}
