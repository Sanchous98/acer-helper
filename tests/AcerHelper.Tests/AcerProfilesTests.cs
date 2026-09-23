using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The EC byte ↔ performance-profile table: which byte the EC accepts, which name/kind/accent the UI shows
/// for it, which kernel <c>platform_profile</c> choice name stands for it, and which profile each bit of the
/// EC's supported-mask selects.
///
/// A wrong entry here is not a crash, it is the WRONG PROFILE: <c>AcerDevice.SetProfile</c> shifts
/// <see cref="AcerProfiles.ToByte"/>'s result straight into the <c>SetGamingMiscSetting</c> call, so a
/// mismatched byte silently sets a different power/thermal envelope than the user clicked. The table is pure
/// static data with no hardware path, so all of it is reachable from a test.
///
/// The mask rule is the non-obvious half and the reason this file is worth having: the bit position is the
/// EC BYTE VALUE, not the profile's index or display order, so Quiet — byte <c>0x00</c> — is bit 0 while Eco
/// — the first profile displayed — is bit 6.
///
/// The choice-name half has the opposite shape: it is a second vocabulary for the same five modes, and the two
/// most dangerous words in it are the ones that LOOK like the table's own. "performance" is Turbo, not
/// Performance, and "balanced-performance" is Performance — see the trap case below, which is the one test
/// here whose failure would show the user a correct-looking list of the wrong modes.
/// </summary>
public class AcerProfilesTests
{
    private static AccentColor C(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Display order, which is not decoration: it is the order the profile list appears in, and the
    /// order the performance hotkey cycles through.</summary>
    [Fact]
    public void TheDisplayOrderIsTheTableOrder()
    {
        Assert.Equal(["Eco", "Quiet", "Balanced", "Performance", "Turbo"],
            AcerProfiles.All.Select(p => p.DisplayName));
    }

    /// <summary>The <c>Id</c> convention, pinned because two other things depend on it: it is what
    /// <see cref="AcerProfiles.ToByte"/> parses, and it is what gets persisted as <c>ProfileMemory.BaseId</c>
    /// and looked up again by <c>LaptopService</c>. It is the EC byte as a DECIMAL string — "0" for Quiet,
    /// not "0x00".</summary>
    [Fact]
    public void TheIdIsTheEcByteInDecimal()
    {
        Assert.Equal(["6", "0", "1", "4", "5"], AcerProfiles.All.Select(p => p.Id));
    }

    /// <summary>Every column of the table, one profile per case, so a failure names the profile rather than
    /// pointing at a diff. These are the table's answers ABOUT a profile and not fields ON it: the class and both
    /// colours come off <see cref="AcerProfiles.TraitsOf"/> since 2026-09-22, and the record itself carries its id
    /// and its display name. The values are unchanged, and that is the point of still asserting them here — the
    /// move was a move. <c>Flash</c> is the colour the EC firmware paints the operating-mode indicator, and the
    /// comment in the source says these are the only colours the firmware accepts on that write — sending
    /// anything else reverts to amber, which is why they are worth freezing exactly.</summary>
    [Theory]
    [InlineData(0x06, "Eco", ProfileKind.Eco, 0x43A047, 0x00DC10)]
    [InlineData(0x00, "Quiet", ProfileKind.Quiet, 0x1B5E20, 0xFFFFFF)]
    [InlineData(0x01, "Balanced", ProfileKind.Balanced, 0xF57C00, 0xC7AE00)]
    [InlineData(0x04, "Performance", ProfileKind.Performance, 0xD32F2F, 0xC7092E)]
    [InlineData(0x05, "Turbo", ProfileKind.Turbo, 0x9C27B0, 0xFF00C7)]
    public void EachProfileCarriesItsKindAccentAndFlash(int b, string name, ProfileKind kind, int accent, int flash)
    {
        var fromTable = AcerProfiles.All.Single(p => p.Id == b.ToString());
        Assert.Equal(name, fromTable.DisplayName);
        Assert.Equal(new PerformanceProfile(b.ToString(), name), fromTable);   // the id and the label, nothing else
        Assert.Equal(kind, AcerProfiles.TraitsOf(fromTable).Kind);
        Assert.Equal(C(accent), AcerProfiles.TraitsOf(fromTable).Accent);
        Assert.Equal(C(flash), AcerProfiles.TraitsOf(fromTable).FlashColor);

        // ...and ToDomain(byte) agrees with the table row it stands for.
        var fromByte = AcerProfiles.ToDomain((byte)b);
        Assert.Equal(name, fromByte.DisplayName);
        Assert.Equal(kind, AcerProfiles.TraitsOf(fromByte).Kind);
        Assert.Equal(C(accent), AcerProfiles.TraitsOf(fromByte).Accent);
        Assert.Equal(C(flash), AcerProfiles.TraitsOf(fromByte).FlashColor);
    }

    /// <summary>The load-bearing invariant: <c>ToByte(ToDomain(b)) == b</c> for EVERY byte, not just the five
    /// in the table. An unknown byte gets a profile whose <c>Id</c> is still that byte in decimal — the
    /// <c>"0x.."</c> form is only the DISPLAY name — so a byte the EC reports but the table does not know
    /// still survives the round trip instead of throwing or, worse, coming back as a different profile.
    ///
    /// This is the direction that would break silently: a profile the app read from the EC and then wrote
    /// back must not change value on the way through the domain type.
    ///
    /// (Worth stating plainly, because a code-reading survey of this file concluded the opposite — that
    /// <see cref="AcerProfiles.ToByte"/> throws on the unknown-profile fallback. It does not: the fallback's
    /// <c>Id</c> is the decimal byte, and it is the fallback's DisplayName that is spelled "0x1F".)</summary>
    [Theory]
    [InlineData(0x00)] [InlineData(0x01)] [InlineData(0x04)] [InlineData(0x05)] [InlineData(0x06)]
    [InlineData(0x02)] [InlineData(0x07)] [InlineData(0x1F)] [InlineData(0x7F)] [InlineData(0x80)]
    [InlineData(0xFE)] [InlineData(0xFF)]
    public void ToByteAndToDomainRoundTripEveryByte(int b)
    {
        Assert.Equal((byte)b, AcerProfiles.ToByte(AcerProfiles.ToDomain((byte)b)));
    }

    /// <summary>An unrecognised byte is described as <see cref="ProfileKind.Other"/> and labelled with its hex
    /// value, so the UI shows something honest instead of picking a wrong profile. Accent and Flash are null:
    /// the app must not invent an accent for a profile it knows nothing about, and there is no flash colour it
    /// could legitimately send — the lookup answers <see cref="ProfileTraits.Unknown"/> for it rather than the
    /// nearest of the five, which is why it is table-keyed and not a fallback chain.</summary>
    [Fact]
    public void AnUnknownByteBecomesAnOtherProfileNamedAfterTheByte()
    {
        var p = AcerProfiles.ToDomain(0x1F);

        Assert.Equal(ProfileKind.Other, AcerProfiles.TraitsOf(p).Kind);
        Assert.Equal("0x1F", p.DisplayName);
        Assert.Equal("31", p.Id);
        Assert.Null(AcerProfiles.TraitsOf(p).Accent);
        Assert.Null(AcerProfiles.TraitsOf(p).FlashColor);
    }

    /// <summary>The lookup REFUSES what is not its own — the one thing a class-carrying record could never say,
    /// because the field was whatever the builder passed. A profile that never came from this table (another
    /// backend's id, the empty id) and an id that is a number but not a byte both answer
    /// <see cref="ProfileTraits.Unknown"/>. The last one is the load-bearing case: <see cref="AcerProfiles.ToByte"/>
    /// THROWS on <c>"300"</c>, and this lookup must not — it is a QUERY about a profile the app may be holding
    /// while some other source owns the machine, exactly like <see cref="AcerProfiles.ToChoiceName"/>, and
    /// "not ours" has to be an answer rather than an exception.</summary>
    [Theory]
    [InlineData("power-saver")]
    [InlineData("")]
    [InlineData("300")]
    [InlineData("0x05")]     // the hex spelling its DisplayName uses for unknown bytes, which its Id never does
    public void AProfileThatDidNotComeFromThisTableHasNoTraits(string id)
    {
        Assert.Equal(ProfileTraits.Unknown, AcerProfiles.TraitsOf(new PerformanceProfile(id, "Foreign")));
    }

    /// <summary><see cref="AcerProfiles.ToByte"/> is public and parses the profile's <c>Id</c> as a byte, but
    /// another backend's profiles carry a NAME as their id (the Linux power-profiles backend uses
    /// strings like <c>"power-saver"</c>). Handing one of those to this method must fail LOUDLY: the
    /// alternative — a silent default — would write a wrong byte into the EC.
    ///
    /// Only <c>AcerDevice</c> calls this today, with profiles this table produced, so both failures are
    /// guards rather than live paths. They are pinned because they are the contract that keeps it that way.</summary>
    [Theory]
    [InlineData("power-saver", typeof(FormatException))]   // another backend's id, not a number at all
    [InlineData("300", typeof(OverflowException))]         // a number that is not a byte
    [InlineData("", typeof(FormatException))]
    public void ToByteRefusesAProfileThatDidNotComeFromThisTable(string id, Type expected)
    {
        var foreign = new PerformanceProfile(id, "Foreign");

        Assert.Throws(expected, () => AcerProfiles.ToByte(foreign));
    }

    /// <summary>Mask 0 means "the EC did not tell us", and the response is every profile — deliberately
    /// different from "the mask has no known bit", which yields nothing (see the next case). The bit position
    /// is the EC byte value, which is why bit 0 is Quiet (byte 0x00) and bit 6 is Eco, the FIRST profile in
    /// display order. The 0x53 row is the real supported-mask this hardware reports: bits 0, 1, 4 and 6, i.e.
    /// everything except Turbo — and the result is in DISPLAY order, not mask order.</summary>
    [Theory]
    [InlineData(0x00, "Eco,Quiet,Balanced,Performance,Turbo")]   // 0 = unknown → all
    [InlineData(0x01, "Quiet")]                                  // bit 0 → byte 0x00
    [InlineData(0x02, "Balanced")]                               // bit 1 → byte 0x01
    [InlineData(0x10, "Performance")]                            // bit 4 → byte 0x04
    [InlineData(0x20, "Turbo")]                                  // bit 5 → byte 0x05
    [InlineData(0x40, "Eco")]                                    // bit 6 → byte 0x06
    [InlineData(0x53, "Eco,Quiet,Balanced,Performance")]         // the real machine
    [InlineData(0x80, "")]                                       // no known bit → nothing at all
    public void TheMaskBitForAProfileIsItsEcByte(int mask, string expected)
    {
        Assert.Equal(expected, string.Join(",", AcerProfiles.FromMask((byte)mask).Select(p => p.DisplayName)));
    }

    /// <summary>Every byte↔token pairing, one profile per case. This is the map between the two vocabularies for
    /// the same five modes — the EC byte (what the app writes and persists) and the kernel's
    /// <c>platform_profile</c> token (what <c>platform-profile-1/profile</c> reads and writes) — so a wrong row
    /// here does not fail anywhere: it selects a different power/thermal envelope, or reports the active one as
    /// some other mode. Both directions are asserted per row, because the port uses both (read = FromChoiceName,
    /// write = ToChoiceName) and a table that agreed in only one direction would look fine from either side
    /// alone.</summary>
    [Theory]
    [InlineData(0x06, "low-power")]              // Eco
    [InlineData(0x00, "quiet")]                  // Quiet
    [InlineData(0x01, "balanced")]               // Balanced
    [InlineData(0x04, "balanced-performance")]   // Performance (93 W)
    [InlineData(0x05, "performance")]            // Turbo (108 W) — see the trap below
    public void EachProfileCarriesItsKernelChoiceName(int b, string choice)
    {
        var profile = AcerProfiles.ToDomain((byte)b);

        Assert.Equal(choice, AcerProfiles.ToChoiceName(profile));

        var fromToken = AcerProfiles.FromChoiceName(choice);
        Assert.NotNull(fromToken);
        Assert.Equal(b.ToString(), fromToken.Id);            // the Acer id is the byte, in decimal
        Assert.Equal(profile.DisplayName, fromToken.DisplayName);
        Assert.Equal(AcerProfiles.TraitsOf(profile), AcerProfiles.TraitsOf(fromToken));
    }

    /// <summary>THE TRAP, stated as the one assertion that would catch it: the kernel's <c>"performance"</c> is
    /// Acer TURBO (byte <c>0x05</c>, the 108 W envelope), and Acer's Performance — the 93 W mode the UI shows as
    /// "Performance" — is the kernel's <c>"balanced-performance"</c>.
    ///
    /// Both halves are asserted because both were live: <c>SysfsPowerProfiles</c>'s own token table classifies
    /// <c>"performance"</c> as <see cref="ProfileKind.Performance"/>, and the app's EC envelope is driven from
    /// the kind, so a port that let the source's classification through sent mode 1 (93 W) where the hardware
    /// wanted mode 0. A name-keyed shortcut anywhere — <c>p.DisplayName.ToLower()</c> most of all — reproduces it
    /// silently, which is why the translation goes through this table instead.</summary>
    [Fact]
    public void TheKernelSpellsTurboPerformanceAndPerformanceBalancedPerformance()
    {
        var turbo = AcerProfiles.FromChoiceName("performance");

        Assert.NotNull(turbo);
        Assert.Equal("5", turbo.Id);                       // 0x05, the EC's Turbo byte
        Assert.Equal("Turbo", turbo.DisplayName);
        Assert.Equal(ProfileKind.Turbo, AcerProfiles.TraitsOf(turbo).Kind);   // NOT ProfileKind.Performance

        var performance = AcerProfiles.FromChoiceName("balanced-performance");

        Assert.NotNull(performance);
        Assert.Equal("4", performance.Id);                 // 0x04
        Assert.Equal("Performance", performance.DisplayName);
        Assert.Equal(ProfileKind.Performance, AcerProfiles.TraitsOf(performance).Kind);
    }

    /// <summary>Round trip through the choice name for every profile in the table, in both directions. The
    /// profile must come back WHOLE — id, display name, kind and both colours — because <c>AcerMappedProfiles</c>
    /// hands the result of one of these calls straight to the UI and to the envelope: a translation that lost the
    /// kind would re-apply another mode's presets, and one that lost the flash colour would repaint the
    /// operating-mode indicator amber (the firmware's response to a colour it does not accept).
    ///
    /// The token side is checked too, since a duplicated or misspelled token in the table would make the port
    /// offer two profiles that resolve to the same kernel mode — a list where clicking the second one never
    /// changes anything.</summary>
    [Fact]
    public void TheChoiceNameRoundTripsForEveryProfile()
    {
        foreach (var p in AcerProfiles.All)
        {
            var token = AcerProfiles.ToChoiceName(p);
            Assert.NotNull(token);
            Assert.Equal(p, AcerProfiles.FromChoiceName(token));
            Assert.Equal(token, AcerProfiles.ToChoiceName(AcerProfiles.FromChoiceName(token)!));
        }
    }

    /// <summary>Anything the kernel can report that this table does not name is null, never a nearby profile:
    /// <c>"custom"</c> is what the LEGACY ACPI alias reads back after a vendor handler writes (the alias fans a
    /// write out to every handler and reports the least specific answer), and <c>"cool"</c> is a fourth name
    /// generic sysfs handlers genuinely expose. The two that would be tempting to "fix" are here on purpose:
    /// <c>"PERFORMANCE"</c> (never a sysfs token, but a plausible typo to fold with a case-insensitive compare)
    /// and the empty string.
    ///
    /// Null is the contract the mapped port's <c>Current()</c> rests on — "the app cannot name this mode" — so a
    /// fallback returning Balanced here would show a machine sitting in a mode of its own as Balanced and then
    /// overwrite the EC usage mode with Balanced's envelope.</summary>
    [Theory]
    [InlineData("custom")]               // the legacy alias after a vendor write
    [InlineData("cool")]                 // a real generic-sysfs token, no Acer byte
    [InlineData("PERFORMANCE")]          // wrong case is not a match
    [InlineData("balanced performance")] // ...and neither is a space for the dash
    [InlineData("junk")]
    [InlineData("")]
    public void AnUnnameableTokenIsNull(string token)
    {
        Assert.Null(AcerProfiles.FromChoiceName(token));
    }

    /// <summary>The other direction's unknown cases: a profile that is not one of ours has no kernel token.
    /// <c>0x1F</c> is a byte the EC could report but the table does not know, and <c>power-saver</c> is another
    /// backend's id (PPD's), which <see cref="AcerProfiles.ToByte"/> would refuse with a throw —
    /// <see cref="AcerProfiles.ToChoiceName"/> must not, because it is a query on a profile the app may be
    /// holding while the machine reports something the table cannot name.</summary>
    [Theory]
    [InlineData(0x1F)] [InlineData(0x07)] [InlineData(0xFF)]
    public void AProfileOutsideTheTableHasNoChoiceName(int b)
    {
        Assert.Null(AcerProfiles.ToChoiceName(AcerProfiles.ToDomain((byte)b)));
    }

    /// <summary>The foreign-id half of the case above, spelled as its own test so a failure says which input was
    /// not handled: an id that is not a decimal byte at all must yield null rather than an exception.</summary>
    [Fact]
    public void AForeignProfileIdHasNoChoiceName()
    {
        Assert.Null(AcerProfiles.ToChoiceName(new PerformanceProfile("power-saver", "Power saver")));
        Assert.Null(AcerProfiles.ToChoiceName(new PerformanceProfile("", "Empty")));
    }
}
