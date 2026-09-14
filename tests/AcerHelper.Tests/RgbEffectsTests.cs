using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The ENE lighting effect catalogue: the mode byte each effect is sent as, and the capability flags the UI
/// builds its controls from.
///
/// Pure static tables — no hardware path — and they matter because three other things branch on them:
/// <c>EneHidController</c> turns the flags into the wire packet and uses
/// <see cref="RgbEffects.StaticModeByte"/> for every per-zone colour write, the lighting UI decides whether to
/// show a colour picker at all, and <c>LampArrayBridge.StaticEffect</c> picks "the arbitrary-colour effect"
/// with the same rule. A wrong <c>ModeByte</c> selects a different effect on the wire; a wrong
/// <c>HasColor</c> ships a colour picker that does nothing, or hides a working one. Neither throws.
///
/// The ORDER is a compatibility surface too, not just presentation: the chosen effect is persisted as a
/// positional <c>LightSettings.EffectIndex</c>, so reordering or inserting into these lists silently repoints
/// every user's saved effect at a different one.
/// </summary>
public class RgbEffectsTests
{
    private static string Names(RgbEffect[] effects) => string.Join(",", effects.Select(e => e.Name));
    private static string Bytes(RgbEffect[] effects) => string.Join(",", effects.Select(e => e.ModeByte));

    /// <summary>The keyboard's effects in their persisted order, with their wire bytes. The bytes were
    /// verified against the hardware, so they are the contract rather than an implementation detail.</summary>
    [Fact]
    public void TheKeyboardEffectsAreInTheirPersistedOrderWithTheirWireBytes()
    {
        Assert.Equal("Static,Breathing,Neon,Wave,Shifting,Zoom,Meteor,Twinkling", Names(RgbEffects.Keyboard));
        Assert.Equal("2,4,5,7,8,9,10,11", Bytes(RgbEffects.Keyboard));
    }

    /// <summary>The lightbar renders fewer effects than the keyboard — the spatial ones do nothing on a
    /// single-zone strip — so it offers a shorter list with the same encoding.</summary>
    [Fact]
    public void TheLightbarOffersOnlyTheEffectsItCanRender()
    {
        Assert.Equal("Static,Breathing,Neon", Names(RgbEffects.Lightbar));
        Assert.Equal("2,4,5", Bytes(RgbEffects.Lightbar));
    }

    /// <summary>Two effects sharing a mode byte would be indistinguishable on the wire: the EC would receive
    /// one value for two UI choices, so picking the second would silently do what the first does. Names are
    /// unique for the neighbouring reason — they are what the dropdown shows, and duplicates make the list
    /// unreadable.</summary>
    [Fact]
    public void EveryEffectSelectsItsOwnModeByteAndHasItsOwnName()
    {
        foreach (var list in new[] { RgbEffects.Keyboard, RgbEffects.Lightbar })
        {
            Assert.Equal(list.Length, list.Select(e => e.ModeByte).Distinct().Count());
            Assert.Equal(list.Length, list.Select(e => e.Name).Distinct().Count());
        }
    }

    /// <summary>The rule both consumers use to mean "the effect that paints an arbitrary colour and holds
    /// still": <c>HasColor &amp;&amp; !HasSpeed</c> — see <c>LampArrayBridge.StaticEffect</c> and the lighting
    /// UI's colour-swatch decision. Both take the FIRST match, so a second effect with that flag combination
    /// would make "the static effect" depend on list order, and the UI and the bridge could disagree about
    /// which one it is. Exactly one per list, and it is the Static entry.
    ///
    /// This is the one test here that guards a rule living in ANOTHER file, which is where the risk actually
    /// is: nothing in <c>RgbEffects.cs</c> announces that its flags are load-bearing elsewhere.</summary>
    [Fact]
    public void EachListHasExactlyOneArbitraryColourEffectAndItIsStatic()
    {
        foreach (var list in new[] { RgbEffects.Keyboard, RgbEffects.Lightbar })
        {
            var arbitrary = list.Where(e => e.HasColor && !e.HasSpeed).ToList();
            Assert.Single(arbitrary);

            // The rule as production applies it, including which one wins when several would match.
            var chosen = list.FirstOrDefault(e => e is { HasColor: true, HasSpeed: false });
            Assert.Same(list[0], chosen);
            Assert.Equal(RgbEffects.StaticModeByte, chosen!.ModeByte);
        }
    }

    /// <summary>Only Static honours the chosen colour; every animated effect cycles the firmware's own
    /// palette. Breathing is the case the source comment records as verified on hardware (mode 0x04 ignores
    /// the colour bytes, pre-seeding a static colour and switching still cycles) — so it is
    /// <c>hasColor: false</c> on purpose, to avoid "a colour picker that does nothing".</summary>
    [Fact]
    public void OnlyStaticHonoursTheChosenColour()
    {
        Assert.Equal("Static", RgbEffects.Keyboard.Single(e => e.HasColor).Name);
        Assert.Equal("Static", RgbEffects.Lightbar.Single(e => e.HasColor).Name);
        Assert.False(RgbEffects.Keyboard.Single(e => e.Name == "Breathing").HasColor);
    }

    /// <summary>The static/effect flag is what tells the controller which packet shape to build
    /// (<c>0x01</c> static vs <c>0x02</c> effect + speed), so it has to be false for exactly the Static
    /// entries and true for everything that animates. Speed follows the same split: it is only meaningful for
    /// an animated effect.</summary>
    [Fact]
    public void StaticIsTheOnlyEffectThatIsNotAnEffect()
    {
        foreach (var list in new[] { RgbEffects.Keyboard, RgbEffects.Lightbar })
        {
            Assert.False(list[0].IsEffect);
            Assert.False(list[0].HasSpeed);
            Assert.All(list.Skip(1), e => Assert.True(e.IsEffect));
            Assert.All(list.Skip(1), e => Assert.True(e.HasSpeed));
        }
    }

    /// <summary>Wave is the only directional effect — it is the one that reports a direction byte. Marking a
    /// non-directional effect directional would send a byte the firmware ignores; failing to mark Wave would
    /// lose the direction control the UI offers for it.</summary>
    [Fact]
    public void OnlyWaveIsDirectional()
    {
        Assert.Equal("Wave", RgbEffects.Keyboard.Single(e => e.HasDirection).Name);
        Assert.All(RgbEffects.Keyboard.Where(e => e.Name != "Wave"), e => Assert.False(e.HasDirection));
        Assert.All(RgbEffects.Lightbar, e => Assert.False(e.HasDirection));
    }

    /// <summary>The static mode byte is published for the per-zone colour writes (every
    /// <c>ApplySubZone</c>/<c>Blank</c> call goes out as this effect), so it has to BE the Static entry's byte
    /// rather than a second copy of it that could drift. 0x02 is pinned as well because it is the value on the
    /// wire.</summary>
    [Fact]
    public void TheStaticModeByteIsTheStaticEffect()
    {
        Assert.Equal(0x02, RgbEffects.StaticModeByte);
        Assert.Equal(RgbEffects.Keyboard[0].ModeByte, RgbEffects.StaticModeByte);
        Assert.Equal(RgbEffects.Lightbar[0].ModeByte, RgbEffects.StaticModeByte);
    }

    /// <summary>The two lists describe different surfaces but encode the same effects, and
    /// <c>EneHidController.Send</c> writes one mode byte regardless of which surface it targets — so an effect
    /// present in both must be identical in both, flags included. A divergence would make the lightbar render
    /// something other than what the UI told the user it would.</summary>
    [Fact]
    public void TheLightbarSharesTheKeyboardEncodingForTheEffectsTheyHaveInCommon()
    {
        foreach (var shared in RgbEffects.Lightbar)
        {
            var kb = RgbEffects.Keyboard.Single(e => e.Name == shared.Name);

            Assert.Equal(kb.ModeByte, shared.ModeByte);
            Assert.Equal(kb.IsEffect, shared.IsEffect);
            Assert.Equal(kb.HasColor, shared.HasColor);
            Assert.Equal(kb.HasSpeed, shared.HasSpeed);
            Assert.Equal(kb.HasDirection, shared.HasDirection);
        }
    }

    /// <summary>The projection the UI binds to carries the <see cref="RgbEffect"/> itself as its opaque
    /// handle — that is how a chosen <c>RgbModeInfo</c> is resolved back to the vendor encoding, so the handle
    /// must be the original instance and not a copy that lost the mode byte.</summary>
    [Fact]
    public void ToModeInfoCarriesTheEffectItselfAsTheOpaqueHandle()
    {
        foreach (var effect in RgbEffects.Keyboard.Concat(RgbEffects.Lightbar))
        {
            var info = effect.ToModeInfo();

            Assert.Same(effect, info.Handle);
            Assert.Equal(effect.Name, info.Name);
            Assert.Equal(effect.HasColor, info.HasColor);
            Assert.Equal(effect.HasSpeed, info.HasSpeed);
            Assert.Equal(effect.HasDirection, info.HasDirection);
        }
    }

    /// <summary>The name is what the UI shows, so it must not silently fall back to the type name.</summary>
    [Fact]
    public void AnEffectDisplaysAsItsName()
    {
        Assert.Equal("Static", RgbEffects.Keyboard[0].ToString());
        Assert.Equal("Twinkling", RgbEffects.Keyboard[^1].ToString());
    }
}
