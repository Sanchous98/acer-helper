using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The PURE AsusArmoury rules: asusd's attribute-type table, per-attribute path discovery, the possible-values
/// parse, and the validation that REFUSES OVER GUESSING. No bus, no asusd, no hardware.
///
/// The boundary tests are the point of the file: every refusal is a value the app must never send to firmware,
/// and each has its own sentence so the user is told which bound was hit.
/// </summary>
public class AsusArmouryTests
{
    private static AsusAttributeDescriptor Attr(
        string name, AsusAttributeKind kind = AsusAttributeKind.Immediate,
        bool available = true, int min = -1, int max = -1, int inc = -1, params int[] possible)
        => new(name, AsusdArmoury.AttributePath(name), kind, available,
               CurrentValue: 0, MinValue: min, MaxValue: max, ScalarIncrement: inc, DefaultValue: 0,
               PossibleValues: possible);

    // ---- attribute kinds (asusd's own table) ----

    [Theory]
    [InlineData("ppt_pl1_spl", "Ppt")]
    [InlineData("ppt_pl2_sppt", "Ppt")]
    [InlineData("ppt_pl3_fppt", "Ppt")]
    [InlineData("ppt_fppt", "Ppt")]
    [InlineData("nv_dynamic_boost", "Ppt")]
    [InlineData("nv_temp_target", "Ppt")]
    [InlineData("panel_od", "Immediate")]
    [InlineData("gpu_mux_mode", "Gpu")]
    [InlineData("dgpu_disable", "Gpu")]
    [InlineData("nv_base_tgp", "ReadOnly")]
    [InlineData("boot_sound", "Bios")]
    [InlineData("some_future_knob", "Unknown")]
    public void TheKindMatchesAsusdsOwnTable(string name, string kind)
        => Assert.Equal(kind, AsusArmouryKinds.KindOf(name).ToString());

    [Theory]
    [InlineData("Gpu", true)]
    [InlineData("Ppt", false)]
    [InlineData("Immediate", false)]
    [InlineData("ReadOnly", false)]
    public void OnlyGpuAttributesAreQueued(string kind, bool queued)
        => Assert.Equal(queued, AsusArmouryKinds.IsQueued(Enum.Parse<AsusAttributeKind>(kind)));

    // ---- path discovery ----

    [Fact]
    public void TheAttributePathsAreTheChildrenOfTheRoot()
    {
        const string tree =
            "xyz.ljones.Asusd\n" +
            "└─/xyz/ljones\n" +
            "  ├─/xyz/ljones/asus_armoury\n" +
            "  │ ├─/xyz/ljones/asus_armoury/ppt_pl1_spl\n" +
            "  │ └─/xyz/ljones/asus_armoury/panel_od\n" +
            "  └─/xyz/ljones/Platform\n";

        Assert.Equal(
            ["/xyz/ljones/asus_armoury/ppt_pl1_spl", "/xyz/ljones/asus_armoury/panel_od"],
            AsusdArmoury.AttributePaths(tree));
    }

    [Fact]
    public void ATreeWithNoAttributesYieldsNothing()
    {
        Assert.Empty(AsusdArmoury.AttributePaths("xyz.ljones.Asusd\n└─/xyz/ljones\n"));
        Assert.Empty(AsusdArmoury.AttributePaths(""));
    }

    [Fact]
    public void TheExactArmouryArgumentLists()
    {
        Assert.Equal(
            ["get-property", "xyz.ljones.Asusd", "/xyz/ljones/asus_armoury/panel_od", "xyz.ljones.AsusArmoury", "min_value"],
            AsusdArmoury.GetPropertyArguments("/xyz/ljones/asus_armoury/panel_od", "min_value"));

        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", "/xyz/ljones/asus_armoury/panel_od", "xyz.ljones.AsusArmoury", "current_value", "i", "1"],
            AsusdArmoury.SetCurrentValueArguments("/xyz/ljones/asus_armoury/panel_od", 1));

        Assert.Equal("/xyz/ljones/asus_armoury/gpu_mux_mode", AsusdArmoury.AttributePath("gpu_mux_mode"));
    }

    // ---- validation: range ----

    [Theory]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void ARangeBoundAttributeAcceptsOnlyInsideIt(int value, bool ok)
    {
        var (accepted, message) = AsusArmouryRules.Validate(Attr("panel_od", min: 0, max: 100), value);

        Assert.Equal(ok, accepted);
        if (!ok) Assert.Equal(AsusArmouryMessages.OutOfRange, message);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(7, false)]
    public void AStepBoundAttributeAcceptsOnlyWholeSteps(int value, bool ok)
    {
        var (accepted, message) = AsusArmouryRules.Validate(Attr("panel_od", min: 0, max: 100, inc: 5), value);

        Assert.Equal(ok, accepted);
        if (!ok) Assert.Equal(AsusArmouryMessages.NotAStep, message);
    }

    [Fact]
    public void AValueSetBoundAttributeUsesTheSetNotTheRange()
    {
        var attr = Attr("charge_mode", min: 0, max: 100, possible: [0, 1, 2]);

        Assert.True(AsusArmouryRules.Validate(attr, 2).ok);
        Assert.Equal(AsusArmouryMessages.NotAnOption, AsusArmouryRules.Validate(attr, 3).message);
    }

    // ---- validation: refusals ----

    [Fact]
    public void ReadOnlyIsRefused()
        => Assert.Equal(AsusArmouryMessages.ReadOnly,
                        AsusArmouryRules.Validate(Attr("nv_base_tgp", AsusAttributeKind.ReadOnly, min: 0, max: 100), 1).message);

    [Fact]
    public void AnUnknownAttributeIsRefused()
        => Assert.Equal(AsusArmouryMessages.UnknownAttribute,
                        AsusArmouryRules.Validate(Attr("future_knob", AsusAttributeKind.Unknown, min: 0, max: 100), 1).message);

    [Fact]
    public void AnUnavailableAttributeIsRefused()
        => Assert.Equal(AsusArmouryMessages.Unavailable,
                        AsusArmouryRules.Validate(Attr("panel_od", available: false, min: 0, max: 100), 1).message);

    /// <summary>THE RULE THE WHOLE PHASE TURNS ON: with neither a value set nor a device-reported range there is
    /// nothing to validate against, and the app refuses rather than inventing a bound.</summary>
    [Fact]
    public void NoReportedRangeIsRefusedRatherThanGuessed()
        => Assert.Equal(AsusArmouryMessages.NoRange,
                        AsusArmouryRules.Validate(Attr("dgpu_disable", AsusAttributeKind.Gpu), 1).message);
}
