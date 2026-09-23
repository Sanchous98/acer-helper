using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The two Acer profile-port decorators — the pair that sits between <c>LaptopService</c> and whichever profile
/// port the machine actually has.
///
/// They are covered here rather than left to the hardware because they were moved out of
/// <c>AcerDevice.Linux.cs</c> for exactly this reason: the test project targets <c>net10.0-windows</c> while the
/// app excludes <c>**/*.Linux.cs</c> from that TFM, so policy left in the Linux file is unreachable by the suite
/// at all. What stayed behind there is the part that needs a Linux box — the hidraw controller — and it arrives
/// here as a delegate.
///
/// <see cref="EcSyncedProfiles"/> is the one that matters most: it is what makes a profile switch move the power
/// envelope on an EC-HID model. Before it wrapped the generic port too, that wiring sat behind the Linuwu-Sense
/// module gate, so a machine with mainline <c>acer-wmi</c> and no module opened the EC controller and then never
/// consulted it — the envelope stayed put through every profile change.
///
/// <see cref="AcerMappedProfiles"/> is the other half of the same job and the reason this file is worth having at
/// all on a mainline-only machine: the port underneath speaks the KERNEL's vocabulary (low-power / quiet /
/// balanced / balanced-performance / performance) while everything above it — presets, the tray, the lightbar
/// palette, <c>settings.json</c> — keys off the Acer EC byte. The decorator is where those two meet, so every
/// case below is about the translation being exact rather than about I/O.
/// </summary>
public class AcerProfilePortsTests
{
    // ---- EcSyncedProfiles: the envelope follows the profile switch ----

    /// <summary>The whole point of the decorator, in one assertion: the profile's <see cref="ProfileKind"/> — not
    /// its id, not its index — reaches the envelope operation, and the switch itself still reaches the inner
    /// port. The kind is what <c>AcerEcHidController.ModeFor</c> maps, so passing the wrong member here silently
    /// picks a different power limit.
    ///
    /// It arrives from the INNER PORT (<see cref="IProfileTraits"/>), which is where a profile's class lives since
    /// 2026-09-22 — the record no longer carries it. The fake below answers with the canonical table for its own
    /// ids, exactly as a real source answers out of its own.</summary>
    [Fact]
    public void TheEnvelopeIsDrivenWithTheProfileKind()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        var kinds = new List<ProfileKind>();
        var port = new EcSyncedProfiles(inner, k => { kinds.Add(k); return true; });

        Assert.True(port.Set(TestProfiles.Turbo));

        Assert.Equal([ProfileKind.Turbo], kinds);
        Assert.Equal(["turbo"], inner.SetCallIds);
    }

    /// <summary>Every kind, not just the one above — the delegate must not be skipped or short-circuited for any
    /// of them. A decorator that only forwarded, say, Turbo would pass the test above and quietly do nothing for
    /// the four profiles a user switches between in practice.</summary>
    [Fact]
    public void EveryProfileKindReachesTheEnvelope()
    {
        var kinds = new List<ProfileKind>();
        var port = new EcSyncedProfiles(new FakePowerProfiles(TestProfiles.All), k => { kinds.Add(k); return true; });

        foreach (var p in TestProfiles.All) port.Set(p);

        Assert.Equal(TestProfiles.All.Select(p => TestProfiles.TraitsOf(p).Kind), kinds);
    }

    /// <summary>A port that classifies NOTHING — the shape a source without a table has — sends no envelope
    /// rather than a guessed one: <see cref="ProfileTraits.Unknown"/>'s kind is <see cref="ProfileKind.Other"/>,
    /// which <c>AcerEcHidController.ModeFor</c> maps to no mode at all. This is the case that makes the lookup's
    /// refusal load-bearing on the envelope path: the old field was whatever the builder passed, so an
    /// unclassified-anything could not exist there and the refusal had nowhere to be expressed.</summary>
    [Fact]
    public void AnUnclassifiedProfileSendsNoEnvelope()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        inner.TraitsOf = _ => ProfileTraits.Unknown;
        var kinds = new List<ProfileKind>();
        var port = new EcSyncedProfiles(inner, k => { kinds.Add(k); return true; });

        Assert.True(port.Set(TestProfiles.Turbo));            // the switch still happens...
        Assert.Equal([ProfileKind.Other], kinds);             // ...and the envelope is told nothing it can map
        Assert.Null(AcerEcHidController.ModeFor(kinds[0]));
    }

    /// <summary>A failing inner write is forwarded as-is. The decorator must not report the envelope's fate
    /// instead of the profile's: the caller uses this bool to decide whether to show an error, and the envelope
    /// write is enqueue-only, so it can never be the authority on whether the profile switch worked.</summary>
    [Fact]
    public void TheInnerResultIsForwardedUnchanged_NotTheEnvelopes()
    {
        var inner = new FakePowerProfiles(TestProfiles.All) { SetResult = false };
        var port = new EcSyncedProfiles(inner, _ => true);   // envelope says yes, inner says no

        Assert.False(port.Set(TestProfiles.Eco));
        Assert.Equal(["eco"], inner.SetCallIds);             // and the inner port was still reached
    }

    /// <summary>Windows' semantics, kept on Linux: a controller that cannot take the mode must not fail the
    /// profile switch (see <c>AcerDevice.Windows.SetProfile</c> — the EC write is enqueued, and its result is
    /// discarded). Asserted because the tempting "fix" is to fail the switch when the envelope write fails, which
    /// would make every profile click on a model with an absent or wedged EC report an error.</summary>
    [Fact]
    public void ADecliningEnvelopeDoesNotFailTheSwitch()
    {
        var port = new EcSyncedProfiles(new FakePowerProfiles(TestProfiles.All), _ => false);

        Assert.True(port.Set(TestProfiles.Balanced));
    }

    /// <summary>No EC channel on this model: the port has to behave exactly as it would unwrapped. This is the
    /// case that catches an unconditional call — writing <c>applyEnvelope!(…)</c> instead of
    /// <c>applyEnvelope?.(…)</c> turns every profile switch on a model without the EC device into a
    /// <c>NullReferenceException</c>, which on a machine the owner does not have is invisible until a user
    /// reports it.</summary>
    [Fact]
    public void WithNoEcChannel_TheInnerPortBehavesAsIfUnwrapped()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        var port = new EcSyncedProfiles(inner, null);

        Assert.True(port.Set(TestProfiles.Quiet));
        Assert.Equal(["quiet"], inner.SetCallIds);
    }

    /// <summary>The read side is pass-through, including <c>LastError</c>. The UI builds its profile list and its
    /// error text from these, so a decorator that swallowed one would hide a real failure.</summary>
    [Fact]
    public void TheReadSideIsForwardedUnchanged()
    {
        var inner = new FakePowerProfiles(TestProfiles.All,
                                          selectable: [TestProfiles.Eco],
                                          current: TestProfiles.Eco) { LastError = "boom" };
        var port = new EcSyncedProfiles(inner, _ => true);

        Assert.Equal(TestProfiles.All.Select(p => p.Id), port.All.Select(p => p.Id));
        Assert.Equal(["eco"], port.Selectable().Select(p => p.Id));
        Assert.Equal("eco", port.Current()!.Id);
        Assert.Equal("boom", port.LastError);
    }

    // ---- AcerMappedProfiles: the kernel's profile vocabulary translated to Acer's ----

    /// <summary>What the inner port offers on this hardware: the KERNEL's tokens as <c>Id</c>, described the way
    /// <c>SysfsPowerProfiles</c> describes them. The fake answers for those ids with the canonical id→kind table,
    /// which is a source's own reading of a word that means Turbo to Acer — <c>"performance"</c> classifies as
    /// <see cref="ProfileKind.Performance"/> there. The decorator has to override that reading, so a test that
    /// used the Acer table on both sides would prove nothing.</summary>
    private static class Kernel
    {
        public static PerformanceProfile LowPower            => new("low-power",            "Low power");
        public static PerformanceProfile Quiet               => new("quiet",                "Quiet");
        public static PerformanceProfile Balanced            => new("balanced",             "Balanced");
        public static PerformanceProfile BalancedPerformance => new("balanced-performance", "Balanced performance");
        public static PerformanceProfile Performance         => new("performance",          "Performance");

        /// <summary>The full set the Acer handler offers under <c>predator_v4=1</c>: all five.</summary>
        public static PerformanceProfile[] All => [LowPower, Quiet, Balanced, BalancedPerformance, Performance];
    }

    /// <summary>The list the UI and the hotkey cycle see: the Acer table's own five profiles, in Acer display
    /// order, carrying the Acer ids — NOT the source's profiles, whose ids are <c>"low-power"</c> and friends.
    ///
    /// The ids are the load-bearing part: they are what <c>LaptopService</c> persists as <c>ProfileMemory.BaseId</c>
    /// and what <c>AcerProfiles.ToByte</c> parses for the EC write, so a port that passed the source's profiles
    /// through would put <c>"balanced-performance"</c> into <c>settings.json</c> — a value the Windows half has
    /// never seen. The CLASS is the other half of the same override and it now has to be asserted through the
    /// port: the source's own reading would hand the envelope Turbo as Performance, which is the live 93 W/108 W
    /// bug this decorator exists to close.</summary>
    [Fact]
    public void TheMappedSetIsTheAcerTableInDisplayOrder()
    {
        var port = new AcerMappedProfiles(new FakePowerProfiles(Kernel.All));

        Assert.Equal(["6", "0", "1", "4", "5"], port.All.Select(p => p.Id));
        Assert.Equal(["Eco", "Quiet", "Balanced", "Performance", "Turbo"], port.All.Select(p => p.DisplayName));
        Assert.Equal([ProfileKind.Eco, ProfileKind.Quiet, ProfileKind.Balanced, ProfileKind.Performance, ProfileKind.Turbo],
                     port.All.Select(p => port.Traits(p).Kind));
        // ...and NOT the source's reading of the very same token, which is the whole reason for the override: the
        // kernel's "performance" classifies as Performance there, while the Acer profile behind it is Turbo.
        var source = new FakePowerProfiles(Kernel.All);
        Assert.Equal(ProfileKind.Performance, ProfileTraits.Of(source, Kernel.Performance).Kind);
        Assert.Equal(ProfileKind.Turbo, port.Traits(AcerProfiles.FromChoiceName("performance")!).Kind);

        Assert.Equal(port.All.Select(p => p.Id), port.Selectable().Select(p => p.Id));
    }

    /// <summary>The ORDER comes from the table, not from the source. sysfs lists the same five names in whatever
    /// order it likes — the kernel's channel array order is not the app's — and this order is not decoration: it
    /// is the performance hotkey's cycle, so a port that echoed the source's order would change what the key does
    /// from one boot to the next.</summary>
    [Fact]
    public void TheSourcesOrderDoesNotBecomeTheDisplayOrder()
    {
        var reversed = Kernel.All.Reverse().ToList();
        var port = new AcerMappedProfiles(new FakePowerProfiles(reversed));

        Assert.Equal(["Eco", "Quiet", "Balanced", "Performance", "Turbo"], port.All.Select(p => p.DisplayName));
    }

    /// <summary>A source offering only some of the five yields exactly its Acer subset — the subset is a hardware
    /// fact (the class node's <c>choices</c> file, or the EC supported-mask on Windows), not a policy filter, and
    /// it is the one thing that legitimately shortens this list. The subset is handed to the fake in a scrambled
    /// order on purpose: the result must still be Acer-ordered.</summary>
    [Fact]
    public void AReducedSourceYieldsOnlyItsAcerSubset()
    {
        var port = new AcerMappedProfiles(new FakePowerProfiles([Kernel.Performance, Kernel.Quiet, Kernel.Balanced]));

        Assert.Equal(["0", "1", "5"], port.All.Select(p => p.Id));            // Quiet, Balanced, Turbo
        Assert.Equal(["Quiet", "Balanced", "Turbo"], port.All.Select(p => p.DisplayName));
    }

    /// <summary>A token the table cannot name is dropped from the list rather than guessed at. <c>"cool"</c> is a
    /// real token generic sysfs handlers expose (the Acer one does not, today) and it must NOT become the nearest
    /// Acer mode: the list is what the user clicks, and a row that silently means something else is worse than a
    /// missing row. The second case is the one that matters most — a source offering ONLY unknown tokens reduces
    /// to nothing, not to all five, because "we did not recognise this machine" has to hide the section rather
    /// than offer five modes that cannot be set.</summary>
    [Fact]
    public void AnUnknownTokenTheSourceOffersIsDropped()
    {
        var cool = new PerformanceProfile("cool", "Cool");

        var mixed = new AcerMappedProfiles(new FakePowerProfiles([Kernel.Quiet, cool]));
        Assert.Equal(["Quiet"], mixed.All.Select(p => p.DisplayName));

        var only = new AcerMappedProfiles(new FakePowerProfiles([cool]));
        Assert.Empty(only.All);
        Assert.Empty(only.Selectable());
    }

    /// <summary>Reading the active profile goes through the table, so <c>"performance"</c> is reported as Turbo —
    /// id <c>"5"</c>, kind <see cref="ProfileKind.Turbo"/>, the Acer display name — and not as the source's own
    /// "Performance" profile. That difference is not cosmetic: the kind is what selects the per-mode presets and
    /// drives the EC envelope, so the wrong one applies another mode's fan curve and 93 W where the hardware is
    /// running 108 W.
    ///
    /// The whole table is exercised rather than the one risky row, because a table with a single transposed pair
    /// would pass a single-case test.</summary>
    [Theory]
    [InlineData("low-power", "6", "Eco")]
    [InlineData("quiet", "0", "Quiet")]
    [InlineData("balanced", "1", "Balanced")]
    [InlineData("balanced-performance", "4", "Performance")]
    [InlineData("performance", "5", "Turbo")]
    public void CurrentIsTheAcerProfileTheSourcesTokenStandsFor(string token, string id, string name)
    {
        var inner = new FakePowerProfiles(Kernel.All, current: new PerformanceProfile(token, token));
        var port = new AcerMappedProfiles(inner);

        Assert.Equal(id, port.Current()!.Id);
        Assert.Equal(name, port.Current()!.DisplayName);
    }

    /// <summary>A current token the table cannot name — <c>"custom"</c> is the one this machine actually reports,
    /// and only through the legacy ACPI alias, which reads back <c>custom</c> after any vendor handler writes —
    /// gives null, which is "the app cannot name the mode the hardware is in".
    ///
    /// The tempting repair is a fallback (<c>?? AcerProfiles.All[2]</c>, or the first entry), and it is worse than
    /// the null in exactly the way the null prevents: on every startup and every hotkey-driven profile change the
    /// app would decide the machine is Balanced, apply Balanced's presets and write Balanced's EC usage mode — on
    /// a machine that is in a mode of its own, and without the user having asked for anything. Null shows an
    /// unnamed mode and leaves the hardware alone.</summary>
    [Fact]
    public void AnUnknownCurrentTokenIsNull_NotANearbyProfile()
    {
        var custom = new PerformanceProfile("custom", "custom");
        var port = new AcerMappedProfiles(new FakePowerProfiles(Kernel.All, current: custom));

        Assert.Null(port.Current());

        var unreadable = new AcerMappedProfiles(new FakePowerProfiles(Kernel.All, current: null));
        Assert.Null(unreadable.Current());
    }

    /// <summary>The write reaches the inner port with the KERNEL's token, not the Acer id: the inner port writes
    /// <c>profile.Id</c> verbatim into the class node, so handing it the Acer profile would write <c>"5"</c> — a
    /// string no handler accepts, and one the driver would reject rather than ignore.
    ///
    /// <c>Assert.Same</c> rather than an id comparison is the point: what must be handed over is the inner port's
    /// OWN object, the one it built from the class node's <c>choices</c>. The decorator must not construct a
    /// lookalike, and the reason is not style — the source's profile is what the port recognises, and building a
    /// second identity for a mode that already has one is how a port ends up being "fixed" later to accept both.
    /// The trap row is explicit: Turbo is written as <c>"performance"</c>.</summary>
    [Theory]
    [InlineData("Eco", "low-power")]
    [InlineData("Quiet", "quiet")]
    [InlineData("Balanced", "balanced")]
    [InlineData("Performance", "balanced-performance")]
    [InlineData("Turbo", "performance")]
    public void SetHandsTheInnerPortItsOwnObjectForThatToken(string acerName, string token)
    {
        var inner = new FakePowerProfiles(Kernel.All);
        var port = new AcerMappedProfiles(inner);
        var clicked = AcerProfiles.All.Single(p => p.DisplayName == acerName);

        Assert.True(port.Set(clicked));

        Assert.Equal([token], inner.SetCallIds);
        Assert.Same(inner.All.Single(p => p.Id == token), inner.SetCalls[0]);
    }

    /// <summary>Clicking Turbo writes <c>"performance"</c> and clicking Performance writes
    /// <c>"balanced-performance"</c> — the pairing that a name-keyed shortcut gets backwards, asserted together
    /// and in both directions because each direction alone can look right by accident. If this test fails, the app
    /// has asked the kernel for the other mode: the user clicked the 93 W profile and the machine went to 108 W,
    /// or the reverse, and nothing in the UI would say so.</summary>
    [Fact]
    public void TheWritePathSpellsTurboPerformanceAndPerformanceBalancedPerformance()
    {
        var turboInner = new FakePowerProfiles(Kernel.All);
        new AcerMappedProfiles(turboInner).Set(AcerProfiles.All.Single(p => AcerProfiles.TraitsOf(p).Kind == ProfileKind.Turbo));
        Assert.Equal(["performance"], turboInner.SetCallIds);

        var perfInner = new FakePowerProfiles(Kernel.All);
        new AcerMappedProfiles(perfInner).Set(AcerProfiles.All.Single(p => AcerProfiles.TraitsOf(p).Kind == ProfileKind.Performance));
        Assert.Equal(["balanced-performance"], perfInner.SetCallIds);
    }

    /// <summary>A profile the source does not offer is REFUSED, and the refusal says which token was missing —
    /// nothing is written, so the hardware keeps its current mode rather than being moved to a nearby one.
    ///
    /// The alternative shape, sending the closest token the source does offer, is the shape this port exists to
    /// prevent: the tokens are not ordered (Turbo is "performance" and Performance is "balanced-performance"), so
    /// there is no "closest" to pick, and every guess is a mode the user did not ask for. The message is asserted
    /// by content rather than verbatim, because what the caller needs from it is the token; the exact wording is
    /// not a contract. It is also read through <c>LaptopService</c>'s <c>Attempt(() =&gt; pp.Set(p), () =&gt;
    /// pp.LastError)</c>, so a refusal with no message would leave the UI showing nothing at all.</summary>
    [Fact]
    public void SetRefusesAProfileTheSourceDoesNotOffer()
    {
        var inner = new FakePowerProfiles([Kernel.Quiet, Kernel.Balanced]);
        var port = new AcerMappedProfiles(inner);

        Assert.False(port.Set(AcerProfiles.All.Single(p => p.DisplayName == "Turbo")));

        Assert.Empty(inner.SetCallIds);
        Assert.Contains("\"performance\"", port.LastError);

        // A refusal is not sticky: the next successful write must not report the earlier one's reason.
        Assert.True(port.Set(AcerProfiles.All.Single(p => p.DisplayName == "Balanced")));
        Assert.Null(port.LastError);
    }

    /// <summary>The inner port's own answer is the one that counts — the decorator is a translator, not a judge of
    /// the hardware. A false from the inner port is forwarded as a false, the inner port was still reached with
    /// the right token, and no local message is invented for it: the decorator has nothing to say about a refusal
    /// the hardware made, and overwriting the port's <c>LastError</c> here would replace the driver's reason with
    /// a made-up one.</summary>
    [Fact]
    public void ARefusedInnerWriteIsForwardedAsARefusal()
    {
        var inner = new FakePowerProfiles(Kernel.All) { SetResult = false };
        var port = new AcerMappedProfiles(inner);

        Assert.False(port.Set(AcerProfiles.All.Single(p => p.DisplayName == "Turbo")));

        Assert.Equal(["performance"], inner.SetCallIds);
        Assert.Null(port.LastError);
    }

    /// <summary>When the decorator has no refusal of its own, <c>LastError</c> is the inner port's — the driver's
    /// message reaches the UI unwrapped, which is the only place it exists.</summary>
    [Fact]
    public void LastErrorIsForwardedFromTheInnerPort()
    {
        var inner = new FakePowerProfiles(Kernel.All) { LastError = "boom" };
        var port = new AcerMappedProfiles(inner);

        Assert.Equal("boom", port.LastError);
    }

    /// <summary>The deliberate pin that keeps the removal of <c>BatteryGatedProfiles</c> a decision rather than an
    /// accident. That decorator greyed out everything but balanced and low-power while unplugged, and BOTH halves
    /// of its justification turned out to be wrong on this hardware: its stated reason — the driver returning
    /// EOPNOTSUPP for profile writes on battery — was measured against the AMD handler on
    /// <c>platform-profile-0</c>, which is a different handler on a different node, while all five profiles were
    /// written on battery through the Acer handler on <c>platform-profile-1</c> on 2026-09-20; and the gate had no
    /// Windows analogue at all, since Windows gets its available set from the EC supported-mask and has no
    /// power-source input anywhere on the profile path. Windows parity is this port's whole purpose, so a gate
    /// here would be the one place Linux offers less for no reason the hardware gives.
    ///
    /// The last assertion is the structural half, and it is why this cannot be quietly reverted: the port takes no
    /// power-source input, so re-introducing the gate means ADDING one — the five deleted tests cannot simply be
    /// pasted back, and whoever does it has to delete this test to make the build pass. The behavioural half alone
    /// (five profiles, no gate visible) would still pass if a gate were added with a default of "on AC".</summary>
    [Fact]
    public void TheMappedPortDoesNotGateOnPowerSource()
    {
        var port = new AcerMappedProfiles(new FakePowerProfiles(Kernel.All));

        Assert.Equal(5, port.All.Count);
        Assert.Equal(5, port.Selectable().Count);
        Assert.Equal(port.All.Select(p => p.Id), port.Selectable().Select(p => p.Id));

        var ctor = Assert.Single(typeof(AcerMappedProfiles).GetConstructors());
        Assert.Equal(typeof(IPowerProfiles), Assert.Single(ctor.GetParameters()).ParameterType);
    }
}
