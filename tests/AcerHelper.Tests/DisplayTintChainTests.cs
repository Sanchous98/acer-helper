using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The blue-light chain: the fall-through order, the three availability decisions, and the level ladder the two
/// temperature-based links share.
///
/// It is covered here rather than left to the hardware because the chain and its decisions live in an UN-SUFFIXED
/// file for exactly this reason: the test project targets <c>net10.0-windows</c> while the app excludes
/// <c>**/*.Linux.cs</c> from that TFM, so anything left in <c>TintChain.Linux.cs</c> would be unreachable by the
/// suite. The Linux half only gathers the facts — environment variables, <c>xrandr</c>, the session bus, the
/// gsettings schema — and hands them to the functions pinned below.
///
/// THE ONE THAT MATTERS MOST is <see cref="TheX11RampNeedsMoreThanXrandrAnswering"/>. The feature the owner
/// reported broken was broken by exactly this decision: <c>DISPLAY=:0</c> exists on their KWin/Wayland session
/// because Plasma starts XWayland, <c>xrandr --query</c> therefore SUCCEEDS and reports a connected output, and a
/// probe that asked only "did xrandr answer?" selected an X11 gamma ramp on a Wayland session — writing into
/// XWayland's ramp, which is not the path the screen is composed from. The row appeared, and did nothing.
/// </summary>
public class DisplayTintChainTests
{
    // ---- the order, and only the order ----

    /// <summary>The first link that answers wins, whatever answers later — the chain is an order, not a vote.</summary>
    [Fact]
    public void TheFirstAvailableLinkWins()
    {
        var first = new FakeDisplayTint();
        var second = new FakeDisplayTint();

        var chain = DisplayTintChain.TryCreate(new[] { new TintLink("first", () => first), new TintLink("second", () => second) });

        Assert.NotNull(chain);
        Assert.Equal("first", chain!.ActiveName);
        Assert.Same(first, chain.Active);

        chain.Apply(2);
        Assert.Equal(new[] { 2 }, first.ApplyCalls);
        Assert.Empty(second.ApplyCalls);      // the loser is never consulted
    }

    /// <summary>A link whose environment is absent is skipped, in order, until one answers.</summary>
    [Fact]
    public void DecliningLinksAreSkippedInOrder()
    {
        var third = new FakeDisplayTint();

        var chain = DisplayTintChain.TryCreate(
        [
            new TintLink("first", () => null),
            new TintLink("second", () => null),
            new TintLink("third", () => third),
            new TintLink("fourth", () => throw new InvalidOperationException("a link after the winner must not be probed")),
        ]);

        Assert.Equal("third", chain!.ActiveName);
        Assert.Same(third, chain.Active);
    }

    /// <summary>Every link declining is the "this session offers nothing" case: the port is null and the Options
    /// row hides. Returning a port that could not change the screen is the failure this whole file guards.</summary>
    [Fact]
    public void NoLinkAtAllMeansNoPort()
    {
        Assert.Null(DisplayTintChain.TryCreate(new[] { new TintLink("a", () => null), new TintLink("b", () => null) }));
        Assert.Null(DisplayTintChain.TryCreate(Array.Empty<TintLink>()));
    }

    /// <summary>The chain is the port the app sees: the levels come from the chosen link and an apply goes to it,
    /// not to the chain.</summary>
    [Fact]
    public void TheChosenLinkCarriesTheLevelsAndTheApply()
    {
        var winner = new FakeDisplayTint(levels: 5) { ApplyResult = false };
        var chain = DisplayTintChain.TryCreate(new[] { new TintLink("only", () => winner) })!;

        Assert.Equal(5, chain.Levels);
        Assert.False(chain.Apply(3));
        Assert.Equal(new[] { 3 }, winner.ApplyCalls);
    }

    // ---- the release travels through the chain ----

    /// <summary>
    /// THE CHAIN MUST BE RELEASABLE, AND THE RELEASE MUST REACH THE LINK. Two of the five links implement the
    /// filter by writing the user's OWN configuration and putting it back when the app exits — KWin's night light
    /// (<c>KwinConfigTint.Dispose</c> → <c>Release</c>) and GNOME's (<c>GnomeConfigTint</c> the same) — because a
    /// tint that lives in a configuration file outlives the process that wrote it. What the device owns and
    /// releases is THIS object (<c>Device.Own</c>), so a chain that is not itself disposable is a release that
    /// never arrives: the user quits, <c>kwinrc</c> keeps <c>Active=true</c> at this app's temperature, and KWin
    /// goes on tinting a screen the app is no longer running behind. That is not hypothetical — the composition
    /// root used to hand the device an <c>IDisplayTint</c>-typed value and test it with a pattern match
    /// (<c>if (tint is IDisposable owned) Own(owned)</c>) which, the chain not being disposable, was always false.
    ///
    /// MUTATION that reddens it: give the chain an empty <c>Dispose</c>, or forward to nothing — <c>Disposed</c>
    /// stays false.
    /// </summary>
    [Fact]
    public void DisposingTheChainReleasesTheLinkThatHoldsTheUsersSettings()
    {
        var winner = new DisposableDisplayTint();
        var chain = DisplayTintChain.TryCreate(new[] { new TintLink("only", () => winner) })!;

        chain.Dispose();

        Assert.True(winner.Disposed);
    }

    /// <summary>A link with nothing to release must not make the chain's own release fail: the X11 gamma ramp
    /// holds no configuration and needs no teardown (the server drops its ramp with the client), so it is not
    /// disposable, and the chain has to tolerate that rather than require every link to implement a no-op.</summary>
    [Fact]
    public void ALinkWithNothingToReleaseIsNotAProblem()
    {
        var chain = DisplayTintChain.TryCreate(new[] { new TintLink("ramp", () => new FakeDisplayTint()) })!;

        Assert.Null(Record.Exception(chain.Dispose));
    }

    /// <summary>An <see cref="IDisplayTint"/> that can be released, standing in for the two links that write the
    /// user's own settings — the shape a fake has to have for the chain's forwarding to be observable at all.</summary>
    private sealed class DisposableDisplayTint : IDisplayTint, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Levels => 5;
        public bool Apply(int level) => true;
        public void Dispose() => Disposed = true;
    }

    // ---- the three decisions ----

    /// <summary>
    /// THE BUG, as a table. <c>xrandr</c> answering is necessary and NOT sufficient: on a Wayland session the
    /// compositor owns colour, so an X11 ramp cannot reach the screen no matter how healthy the X server looks. On
    /// a real X11 session it is the right lever — silent, persistent, and no configuration touched.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]     // real X11, outputs found -> the ramp works
    [InlineData(true, true, false)]     // XWayland answers on a Wayland session -> it does NOT (this session)
    [InlineData(false, false, false)]   // no X11 at all
    [InlineData(false, true, false)]    // Wayland with no XWayland
    public void TheX11RampNeedsMoreThanXrandrAnswering(bool xrandrFoundOutputs, bool sessionIsWayland, bool works)
        => Assert.Equal(works, TintProbe.X11GammaWorks(xrandrFoundOutputs, sessionIsWayland));

    /// <summary>KWin's config route needs the bus, the interface, and a compositor that says it can do night
    /// light — three separate machines, and only the last is about KWin being willing.</summary>
    [Theory]
    [InlineData(false, false, false, false)]   // no session bus
    [InlineData(true, false, false, false)]    // bus, no NightLight interface (non-KWin compositor)
    [InlineData(true, true, false, false)]     // interface present, compositor reports no night-light support
    [InlineData(true, true, true, true)]       // KWin with a working night light
    public void KwinNeedsTheBusTheInterfaceAndACompositorThatCan(bool bus, bool iface, bool available, bool usable)
        => Assert.Equal(usable, TintProbe.KwinConfigUsable(bus, iface, available));

    /// <summary>GNOME needs <c>gsettings</c> AND the schema: the tool ships on KDE systems where the schema is
    /// absent, and a write without it fails — which is this machine's case exactly.</summary>
    [Theory]
    [InlineData(false, false, false)]   // no gsettings
    [InlineData(true, false, false)]    // gsettings on a KDE box: the schema is not installed (measured here)
    [InlineData(false, true, false)]    // schema present but no tool
    [InlineData(true, true, true)]      // a real GNOME
    public void GnomeNeedsBothTheToolAndTheSchema(bool tool, bool schema, bool usable)
        => Assert.Equal(usable, TintProbe.GnomeUsable(tool, schema));

    // ---- the shared level ladder ----

    /// <summary>Five levels, Off first — the count the Options row builds its names from, shared with the Windows
    /// and X11 gamma ramps.</summary>
    [Fact]
    public void TheLadderIsTheFiveSharedLevels()
    {
        Assert.Equal(5, NightTintLevels.Count);
        Assert.Equal(6500, NightTintLevels.TemperatureFor(0));
    }

    /// <summary>Each level warmer than the last, "Long-use" strongest, over a real blackbody ladder rather than a
    /// blue-channel scale.</summary>
    [Fact]
    public void EachLevelIsWarmerThanTheLast()
    {
        var kelvin = Enumerable.Range(0, NightTintLevels.Count).Select(NightTintLevels.TemperatureFor).ToList();

        Assert.Equal(new[] { 6500, 4500, 4000, 3500, 3000 }, kelvin);
        Assert.True(kelvin.Zip(kelvin.Skip(1)).All(p => p.First > p.Second));
    }

    [Theory]
    [InlineData(-1, 6500)]
    [InlineData(9, 3000)]
    [InlineData(2, 4000)]
    public void LevelsClampIntoTheTable(int level, int expected)
        => Assert.Equal(expected, NightTintLevels.TemperatureFor(level));

    /// <summary>Nothing above KWin's own neutral goes into the user's settings — it clamps a night temperature
    /// there, so a larger value would be a request the screen silently would not honour.</summary>
    [Fact]
    public void ARequestAboveNeutralIsClampedDown()
        => Assert.Equal(NightTintLevels.Neutral, NightTintLevels.Clamp(7200));

    /// <summary>The remembered-value record, whose whole point is that ABSENT and present-but-empty are different
    /// things: an absent key has to be deleted again so the application's own default applies.</summary>
    [Fact]
    public void APriorKnowsWhetherItsKeyWasAbsent()
    {
        Assert.True(new TintPrior("Mode", null).WasAbsent);
        Assert.False(new TintPrior("Mode", "DarkLight").WasAbsent);
        Assert.Null(new TintPrior("Mode", null).RestoreValue);
        Assert.Equal("DarkLight", new TintPrior("Mode", "DarkLight").RestoreValue);
    }
}
