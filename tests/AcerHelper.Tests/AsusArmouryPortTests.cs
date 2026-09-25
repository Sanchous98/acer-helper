using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The AsusArmoury port over a recorded busctl runner: capability enumeration, the CONSENT gate, the
/// REFRESH-then-validate order, the queued-GPU behaviour (never applied immediately), single-flight, and the
/// refusal paths. No asusd, no D-Bus and no ASUS hardware.
///
/// The single-flight test blocks inside the consent delegate, which sits INSIDE the port's write lock — so a
/// second write that entered the critical section would show up as concurrency, and the assertion is
/// deterministic rather than timing-based.
/// </summary>
public class AsusArmouryPortTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
        public bool CalledApply => Calls.Any(a => a.Length > 0 && a[0] == "call");
        public string[]? LastSet => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property");
    }

    private static FakeBus Bus(string name = "ppt_pl1_spl",
                               string available = "as 1 \"current_value\"",
                               string min = "i 15", string max = "i 90", string inc = "i 5",
                               string possible = "ai 0", string current = "i 45", string queued = "i -1")
    {
        var attrPath = $"/xyz/ljones/asus_armoury/{name}";
        return new FakeBus
        {
            Answer = args => args switch
            {
                ["tree", _] => (0, $"xyz.ljones.Asusd\n└─/xyz/ljones\n  └─/xyz/ljones/asus_armoury\n    └─{attrPath}\n"),
                ["introspect", _, _] => (0, "xyz.ljones.AsusArmoury interface - - -\n"),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "name"] => (0, $"s \"{name}\""),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "available_attrs"] => (0, available),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "min_value"] => (0, min),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "max_value"] => (0, max),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "scalar_increment"] => (0, inc),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "possible_values"] => (0, possible),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "current_value"] => (0, current),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "default_value"] => (0, "i 15"),
                ["get-property", _, _, "xyz.ljones.AsusArmoury", "queued_gpu_value"] => (0, queued),
                _ => (0, ""),
            },
        };
    }

    private static AsusdArmouryPort Port(FakeBus bus, AsusWriteConsent? consent = null)
        => AsusdArmouryPort.TryCreate(bus.Call, consent ?? (_ => true))!;

    // ---- enumeration ----

    [Fact]
    public void ThePortReadsTheAttributeDescriptors()
    {
        var port = Port(Bus());

        var attr = Assert.Single(port.Attributes);
        Assert.Equal("ppt_pl1_spl", attr.Name);
        Assert.Equal(AsusAttributeKind.Ppt, attr.Kind);
        Assert.True(attr.Available);
        Assert.Equal(15, attr.MinValue);
        Assert.Equal(90, attr.MaxValue);
        Assert.Equal(5, attr.ScalarIncrement);
        Assert.Equal(45, attr.CurrentValue);
    }

    [Fact]
    public void NoAttributesMeansNoPort()
    {
        var bus = new FakeBus
        {
            Answer = args => args switch
            {
                ["tree", _] => (0, "xyz.ljones.Asusd\n└─/xyz/ljones\n"),
                _ => (0, ""),
            },
        };

        Assert.Null(AsusdArmouryPort.TryCreate(bus.Call, _ => true));
    }

    [Fact]
    public void AFailedTreeMeansNoPort()
    {
        var bus = new FakeBus { Answer = _ => (1, "no such service") };
        Assert.Null(AsusdArmouryPort.TryCreate(bus.Call, _ => true));
    }

    /// <summary>An object under the root whose introspection does NOT carry the interface is not an attribute —
    /// the same exact-row presence test the Aura path uses.</summary>
    [Fact]
    public void AnObjectWithoutTheInterfaceIsNotAnAttribute()
    {
        var bus = Bus();
        bus.Answer = args => args switch
        {
            ["tree", _] => (0, "xyz.ljones.Asusd\n└─/xyz/ljones/asus_armoury/ppt_pl1_spl\n"),
            ["introspect", _, _] => (0, "xyz.ljones.SomethingElse interface - - -\n"),
            _ => (0, ""),
        };

        Assert.Null(AsusdArmouryPort.TryCreate(bus.Call, _ => true));
    }

    // ---- the valid write ----

    [Fact]
    public void AValidWriteSetsCurrentValue()
    {
        var bus = Bus();
        string? warning = null;
        var port = Port(bus, w => { warning = w; return true; });

        var outcome = port.Write(port.Find("ppt_pl1_spl")!, 45);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Queued);
        Assert.Null(outcome.Message);
        Assert.Equal(AsusArmouryMessages.PptWarning, warning);
        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", "/xyz/ljones/asus_armoury/ppt_pl1_spl",
             "xyz.ljones.AsusArmoury", "current_value", "i", "45"],
            set);
    }

    /// <summary>THE LIVE LIMITS WIN: the port re-reads them on every write, so a range that moved after the port
    /// was built (PPT bounds move with the profile) is the one applied. The stale, permissive descriptor must
    /// not let 50 through once the device reports a 0..10 range.</summary>
    [Fact]
    public void AWriteValidatesAgainstTheLiveRangeNotTheCachedDescriptor()
    {
        var bus = Bus(min: "i 0", max: "i 100", inc: "i 1");
        var port = Port(bus);
        var attr = port.Find("ppt_pl1_spl")!;

        // The device now reports a much tighter range.
        var tight = Bus(min: "i 0", max: "i 10", inc: "i 1");
        bus.Answer = tight.Answer;

        var outcome = port.Write(attr, 50);

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.OutOfRange, outcome.Message);
        Assert.Null(bus.LastSet);
    }

    // ---- refusals before the bus ----

    [Fact]
    public void AReadOnlyAttributeIsRefusedAndNeverWritten()
    {
        var bus = Bus(name: "nv_base_tgp", min: "i 0", max: "i 100", inc: "i 1");
        var port = Port(bus);

        var outcome = port.Write(port.Find("nv_base_tgp")!, 20);

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.ReadOnly, outcome.Message);
        Assert.Null(bus.LastSet);
    }

    [Fact]
    public void AnUnknownAttributeIsRefusedAndNeverWritten()
    {
        var bus = Bus(name: "future_knob", min: "i 0", max: "i 100", inc: "i 1");
        var port = Port(bus);

        var outcome = port.Write(port.Find("future_knob")!, 20);

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.UnknownAttribute, outcome.Message);
        Assert.Null(bus.LastSet);
    }

    [Fact]
    public void AnAttributeWithNoReportedRangeIsRefused()
    {
        var bus = Bus(name: "dgpu_disable", min: "i -1", max: "i -1", inc: "i -1");
        var port = Port(bus);

        var outcome = port.Write(port.Find("dgpu_disable")!, 1);

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.NoRange, outcome.Message);
        Assert.Null(bus.LastSet);
    }

    [Fact]
    public void ADeclinedConsentWritesNothing()
    {
        var bus = Bus();
        var port = Port(bus, _ => false);

        var outcome = port.Write(port.Find("ppt_pl1_spl")!, 45);

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.Cancelled, outcome.Message);
        Assert.Null(bus.LastSet);
    }

    [Fact]
    public void ARefusedWriteKeepsTheDaemonsWords()
    {
        var bus = Bus();
        bus.Answer = args => args switch
        {
            ["set-property", ..] => (1, "Call failed: not supported"),
            _ => Bus().Answer(args),
        };
        var port = Port(bus);

        var outcome = port.Write(port.Find("ppt_pl1_spl")!, 45);

        Assert.False(outcome.Ok);
        Assert.Equal("Call failed: not supported", outcome.Message);
    }

    // ---- the queued GPU behaviour ----

    /// <summary>
    /// A GPU ATTRIBUTE IS QUEUED, NEVER APPLIED. The write goes to <c>current_value</c> (which asusd stores in
    /// its queue) and the app NEVER calls <c>apply_queued_gpu_value</c> — an immediate MUX apply is the highest
    /// risk operation in the feature, and asusd applies the queue at shutdown by design. The outcome carries the
    /// reboot warning so the user is told.
    /// </summary>
    [Fact]
    public void AGpuWriteIsQueuedAndNeverApplied()
    {
        var bus = Bus(name: "gpu_mux_mode", min: "i 0", max: "i 1", inc: "i 1");
        string? warning = null;
        var port = Port(bus, w => { warning = w; return true; });

        var outcome = port.Write(port.Find("gpu_mux_mode")!, 1);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Queued);
        Assert.Equal(AsusArmouryMessages.GpuQueued, outcome.Message);
        Assert.Equal(AsusArmouryMessages.GpuRebootWarning, warning);

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["i", "1"], set![^2..]);

        // The one thing that must NEVER happen here.
        Assert.False(bus.CalledApply, "the app must never call apply_queued_gpu_value");
    }

    [Fact]
    public void AQueuedGpuValueIsReadable()
    {
        var bus = Bus(name: "gpu_mux_mode", min: "i 0", max: "i 1", inc: "i 1", queued: "i 1");
        var port = Port(bus);

        Assert.Equal(1, port.QueuedGpuValue(port.Find("gpu_mux_mode")!));
    }

    [Fact]
    public void NoQueuedGpuValueReadsAsNull()
    {
        var bus = Bus(name: "gpu_mux_mode", min: "i 0", max: "i 1", inc: "i 1", queued: "i -1");
        var port = Port(bus);

        Assert.Null(port.QueuedGpuValue(port.Find("gpu_mux_mode")!));
        // ...and a non-GPU attribute never reports one.
        Assert.Null(port.QueuedGpuValue(port.Find("gpu_mux_mode")! with { Kind = AsusAttributeKind.Ppt }));
    }

    // ---- single-flight ----

    /// <summary>The write lock spans the consent and the bus call, so a second write cannot enter while the first
    /// is in progress. The consent delegate blocks, so this is deterministic: without the lock, the second write
    /// would observe concurrency.</summary>
    [Fact]
    public async Task WritesAreSingleFlight()
    {
        var bus = Bus(min: "i 0", max: "i 100", inc: "i 1");
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var inFlight = 0;
        var max = 0;

        var port = Port(bus, _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            max = Math.Max(max, now);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            Interlocked.Decrement(ref inFlight);
            return true;
        });
        var attr = port.Find("ppt_pl1_spl")!;

        var first = Task.Run(() => port.Write(attr, 45));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "the first write never entered");

        var second = Task.Run(() => port.Write(attr, 50));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref max));   // the second could not enter while the first held the lock

        release.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, Volatile.Read(ref max));
    }
}
