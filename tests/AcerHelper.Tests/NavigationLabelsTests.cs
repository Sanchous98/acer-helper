using AcerHelper.Localization;

namespace AcerHelper.Tests;

/// <summary>The shell's footer launches the Options drawer and the performance drawer side by side. The latter
/// used to be called <c>Tuning</c> / "Настройка", which in Russian reads as a plain synonym of "Параметры"
/// (Options) — two buttons that look like they open the same thing. It is named for what it actually holds
/// (GPU overclock, CPU power mode, CPU undervolt, GPU MUX), and this pins that the two labels stay distinct so
/// the rename cannot quietly regress.</summary>
public class NavigationLabelsTests
{
    /// <summary>Both keys exist, and the performance drawer is no longer a "settings" word.</summary>
    [Fact]
    public void ThePerformanceDrawerIsNotSynonymousWithOptions()
    {
        var performance = Loc.Ru("Overclocking and Power");
        var options = Loc.Ru("Options");
        Assert.NotNull(performance);
        Assert.NotNull(options);

        Assert.Equal("Разгон и питание", performance);
        Assert.NotEqual(options, performance);
        Assert.DoesNotContain("Настройка", performance);
    }
}
