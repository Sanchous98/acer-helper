using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Abi.V1;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The session's invoke mapping and dispose lifetime (docs/vendor-plugins.md §4.2, §4.3). A
/// <see cref="FakeNativePluginBinding"/> stands in for the native exports and a fake
/// <see cref="IPluginAbiAdapter"/> proves the adapter is consulted on both sides of the boundary — the two things
/// that must hold before the capability adapters of T5 can be written against <see cref="IPluginSession"/>.
/// </summary>
public class PluginSessionTests
{
    private static readonly PluginManifest Manifest = new() { PluginId = "acer-nitro", Abi = "1.0" };

    /// <summary>A session over the given fake binding and the real V1 identity adapter (§3.8.2).</summary>
    private static PluginSession Session(FakeNativePluginBinding binding, ulong handle = 0x1234) =>
        new(binding, handle, new AbiV1Adapter(), Manifest);

    /// <summary>§4.2: <see cref="IPluginSession.Invoke"/> forwards the (capability, op, request) triple to
    /// <c>ah_invoke</c> unchanged (through the identity adapter) and returns the plugin's status and body.</summary>
    [Fact]
    public void InvokeForwardsCapabilityOpAndRequest()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Power, Operation.Power.Current, AbiStatus.Ok, """{"id":"1"}""");

        var session = Session(binding, handle: 0xABC);
        var result = session.Invoke(Capability.Power, Operation.Power.Current, "{}");

        Assert.Equal(AbiStatus.Ok, result.Status);
        Assert.Equal("""{"id":"1"}""", result.Body);

        var call = Assert.Single(binding.InvokeCalls);
        Assert.Equal(0xABCu, call.Handle);
        Assert.Equal(Capability.Power, call.Capability);
        Assert.Equal(Operation.Power.Current, call.Op);
        Assert.Equal("{}", call.RequestJson);
        Assert.Null(session.LastError);
    }

    /// <summary>§4.2: a non-<c>Ok</c> status is returned UNCHANGED with its <c>{"error":"…"}</c> body, and the
    /// error text is exposed on <see cref="PluginSession.LastError"/> (the port <c>LastError</c> shape T5 maps).
    /// The body is NOT run through the adapter — a refusal is already major-neutral.</summary>
    [Fact]
    public void NonOkStatusIsSurfacedVerbatimWithLastError()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Fan, Operation.Fan.SetMode, AbiStatus.Refused,
            """{"error":"EC locked"}""");

        var session = Session(binding);
        var result = session.Invoke(Capability.Fan, Operation.Fan.SetMode, """{"mode":"Custom"}""");

        Assert.Equal(AbiStatus.Refused, result.Status);
        Assert.Equal("""{"error":"EC locked"}""", result.Body);
        Assert.Equal("EC locked", session.LastError);
    }

    /// <summary>A later successful call clears <see cref="PluginSession.LastError"/> — the reason belongs to the
    /// last call, the same rule the in-host ports follow (LaptopService.cs:121-149, §4.2).</summary>
    [Fact]
    public void SuccessfulCallClearsLastError()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Power, Operation.Power.Set, AbiStatus.Refused, """{"error":"no"}""");

        var session = Session(binding);
        _ = session.Invoke(Capability.Power, Operation.Power.Set, "{}");
        Assert.Equal("no", session.LastError);

        binding.SetInvokeResult(Capability.Power, Operation.Power.Set, AbiStatus.Ok, "{}");
        _ = session.Invoke(Capability.Power, Operation.Power.Set, "{}");
        Assert.Null(session.LastError);
    }

    /// <summary>§4.3: <see cref="PluginSession.Dispose"/> disposes the binding, which is what runs
    /// <c>ah_dispose</c> (§6: "ah_dispose … called exactly once"). A second dispose must not double it.</summary>
    [Fact]
    public void DisposeDisposesTheBindingExactlyOnce()
    {
        var binding = new FakeNativePluginBinding();
        var session = Session(binding);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, binding.DisposeCount);
    }

    /// <summary>§3.8.2: the ADAPTER is consulted on both sides of the boundary — the request is encoded through
    /// <c>EncodeRequest</c> before the call and the response decoded through <c>DecodeResponse</c> on Ok. The fake
    /// adapter tags both so the test can prove each ran exactly once.</summary>
    [Fact]
    public void AdapterIsConsultedForRequestAndResponse()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Sensors, Operation.Sensors.Read, AbiStatus.Ok, "wire-response");

        var adapter = new RecordingAdapter();
        var session = new PluginSession(binding, 0x1234, adapter, Manifest);

        var result = session.Invoke(Capability.Sensors, Operation.Sensors.Read, "internal-request");

        Assert.Equal("internal-request->encoded", binding.InvokeCalls[0].RequestJson); // EncodeRequest ran
        Assert.Equal("decoded:wire-response", result.Body);                          // DecodeResponse ran

        Assert.Equal(1, adapter.EncodeCalls);
        Assert.Equal(1, adapter.DecodeCalls);
        Assert.Equal((Capability.Sensors, Operation.Sensors.Read), adapter.LastEncodePair);
    }

    /// <summary>On a NON-Ok status the adapter's <c>DecodeResponse</c> is NOT consulted (§4.2): the refusal body
    /// is already major-neutral and must survive verbatim.</summary>
    [Fact]
    public void AdapterResponseDecodeSkippedOnNonOk()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Sensors, Operation.Sensors.Read, AbiStatus.Refused, """{"error":"x"}""");

        var adapter = new RecordingAdapter();
        var session = new PluginSession(binding, 0x1234, adapter, Manifest);

        var result = session.Invoke(Capability.Sensors, Operation.Sensors.Read, "req");

        Assert.Equal("""{"error":"x"}""", result.Body);
        Assert.Equal(1, adapter.EncodeCalls);
        Assert.Equal(0, adapter.DecodeCalls);
    }

    /// <summary>A no-body Ok result stays null rather than becoming the string "null".</summary>
    [Fact]
    public void NoBodyOkStaysNull()
    {
        var binding = new FakeNativePluginBinding();
        binding.SetInvokeResult(Capability.Fan, Operation.Fan.SetMode, AbiStatus.Ok, null);

        var session = Session(binding);
        var result = session.Invoke(Capability.Fan, Operation.Fan.SetMode, "{}");

        Assert.Equal(AbiStatus.Ok, result.Status);
        Assert.Null(result.Body);
    }

    /// <summary>The session exposes the decoded manifest it was built with, immutable after construction, so the
    /// T5 adapters read <c>All</c>/<c>Traits</c>/presence flags without calling the plugin (§4.2).</summary>
    [Fact]
    public void ManifestIsExposedAsConstructed()
    {
        var session = Session(new FakeNativePluginBinding());
        Assert.Same(Manifest, session.Manifest);
    }

    /// <summary>An adapter whose request/response translate into a tagged form, so the test can prove the
    /// session routes through it rather than calling the binding directly. V1 is the identity and cannot show
    /// this, so this fake is the stand-in for a future translating major (§3.8.2).</summary>
    private sealed class RecordingAdapter : IPluginAbiAdapter
    {
        public int Major => 1;
        public bool Deprecated => false;

        public int EncodeCalls { get; private set; }
        public int DecodeCalls { get; private set; }
        public (uint Capability, uint Op) LastEncodePair { get; private set; }

        public PluginManifest DecodeManifest(ReadOnlySpan<byte> utf8Json) => Manifest;

        public string EncodeRequest(uint capability, uint op, string internalRequestJson)
        {
            EncodeCalls++;
            LastEncodePair = (capability, op);
            return internalRequestJson + "->encoded";
        }

        public string? DecodeResponse(uint capability, uint op, string? wireResponseJson)
        {
            DecodeCalls++;
            return wireResponseJson is null ? null : "decoded:" + wireResponseJson;
        }
    }
}
