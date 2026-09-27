using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// The sensors adapter (docs/vendor-plugins.md §3.5, §4.2): implements <see cref="ISensors"/> over
/// <c>ah_invoke(Capability.Sensors, Operation.Sensors.Read)</c>, mirroring the in-host <c>SensorsPort</c>
/// (Infrastructure/Plugins/Sdk/DelegatePorts.cs:93-96).
///
/// Its PRESENCE is the override (§3.4): a non-null <c>sensors</c> object in the manifest is the whole declaration
/// "this plugin replaces the generic read", which is how <c>AcerDevice.Linux.cs:222-223</c> wraps the generic
/// sensors today. The adapter therefore exists only when the manifest declared it — the device assembler checks,
/// not this class.
///
/// <see cref="Read"/> marshals the §3.5 snapshot <c>{"cpuTempC":41,"gpuTempC":40,"fans":[{"label":"CPU","rpm":2100}]}</c>
/// onto the host <see cref="SensorSnapshot"/>. The port returns a snapshot with no error channel, so the only
/// thing to decide on a refusal or a malformed body is the host's own "unreadable" default: temperatures of -1
/// and no fans — exactly the value <see cref="SensorSnapshot"/> is initialised to (Domain/Models.cs:71-76), so a
/// failed read hides the readings rather than inventing one.
/// </summary>
internal sealed class PluginSensors : ISensors
{
    private readonly IPluginSession _session;

    public PluginSensors(IPluginSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary><c>Op.Read</c> (§3.5). A non-<c>Ok</c> status, or a body that does not parse, degrades to the
    /// default <see cref="SensorSnapshot"/> — the port shape has no room for a reason, and the caller (the
    /// background pass) treats "-1 everywhere" as "nothing to show".</summary>
    public SensorSnapshot Read()
    {
        var result = _session.Invoke(Capability.Sensors, Operation.Sensors.Read, PluginBodies.Empty);
        if (result.Status != AbiStatus.Ok) return new SensorSnapshot();

        var fanList = ReadFans(result.Body);
        return new SensorSnapshot
        {
            CpuTempC = PluginBodies.IntProperty(result.Body, "cpuTempC", -1),
            GpuTempC = PluginBodies.IntProperty(result.Body, "gpuTempC", -1),
            Fans = fanList,
        };
    }

    /// <summary>The <c>fans</c> array as <see cref="FanReading"/>s. A label-less entry is kept with an empty
    /// label rather than dropped — the RPM is still a reading — and a missing <c>rpm</c> reads as -1, the
    /// domain's own "speed unreadable" (Domain/Models.cs:66).</summary>
    private static IReadOnlyList<FanReading> ReadFans(string? body)
    {
        if (string.IsNullOrEmpty(body)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("fans", out var fans)
                || fans.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            var list = new List<FanReading>(fans.GetArrayLength());
            foreach (var fan in fans.EnumerateArray())
            {
                var label = fan.ValueKind == System.Text.Json.JsonValueKind.Object
                    && fan.TryGetProperty("label", out var l)
                    && l.ValueKind == System.Text.Json.JsonValueKind.String
                    ? l.GetString() ?? ""
                    : "";
                var rpm = fan.ValueKind == System.Text.Json.JsonValueKind.Object
                    && fan.TryGetProperty("rpm", out var r)
                    && r.ValueKind == System.Text.Json.JsonValueKind.Number
                    && r.TryGetInt32(out var v)
                    ? v
                    : -1;
                list.Add(new FanReading(label, rpm));
            }
            return list;
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
