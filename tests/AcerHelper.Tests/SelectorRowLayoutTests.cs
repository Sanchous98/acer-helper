using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE "LABEL + SELECTOR" ROWS ALL READ THE SAME. Three label/ComboBox pairs live in the Tuning drawer — the
/// GPU power level (GpuView), the CPU power mode (CpuView) and every Options choice row
/// (App.axaml's ChoiceRowViewModel template) — and they had drifted into three different geometries: the GPU
/// power row stretched its dropdown across the whole card (a label column of a fixed 64 DIP plus a star column),
/// while the CPU and Options rows put a content-sized dropdown on the right. The owner's report was exactly that
/// inconsistency. The agreed shape is the app's own dominant one, and this pins it against drift:
///   * the label fills the LEFT column (<c>ColumnDefinitions="*,Auto"</c>), wrapping rather than trimming;
///   * the ComboBox sits in the RIGHT column, content-sized (<c>MinWidth</c>, never <c>Stretch</c>), so it is
///     never stretched across the card.
///
/// A source guard: no test in this suite can lay out a window, so the shape is asserted where it is written — the
/// same technique (and the same limit) as <c>SectionSwitchPerformanceTests</c> and <c>GpuMuxTests</c>.
/// </summary>
public class SelectorRowLayoutTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The GPU power row uses the shared label-left / dropdown-right geometry: the label fills the left
    /// column and the dropdown is content-sized on the right. The old fixed-64-DIP label + star column (which
    /// stretched the dropdown) is the drift this forbids.</summary>
    [Fact]
    public void TheGpuPowerRowUsesTheSharedLabelAndSelectorGeometry()
    {
        var xaml = Source("UI/Views/GpuView.axaml");

        var rowStart = xaml.IndexOf("oc.gpu_power", StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "the GPU power row is gone from GpuView.axaml");
        var gridStart = xaml.LastIndexOf("<Grid", rowStart, StringComparison.Ordinal);
        var gridEnd = xaml.IndexOf("</Grid>", rowStart, StringComparison.Ordinal);
        Assert.True(gridStart >= 0 && gridEnd > gridStart, "the GPU power row is no longer a Grid");
        var row = xaml[gridStart..gridEnd];

        // Label fills the left, the selector is a content-sized right column — NOT the old Auto,*,Auto + Width=64.
        Assert.Contains("ColumnDefinitions=\"*,Auto\"", row, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"160\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"Auto,*,Auto\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"64\"", row, StringComparison.Ordinal);
        // A stretched dropdown is exactly the shape the owner flagged; the selector must be content-sized.
        Assert.DoesNotContain("HorizontalAlignment=\"Stretch\"", row, StringComparison.Ordinal);
    }

    /// <summary>The CPU power-mode row is the reference this alignment copies: label fills the left, content-sized
    /// dropdown on the right. Pinned so the two rows cannot drift apart in the other direction.</summary>
    [Fact]
    public void TheCpuPowerRowKeepsTheSameGeometry()
    {
        var xaml = Source("UI/Views/CpuView.axaml");

        Assert.Contains("ColumnDefinitions=\"*,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"160\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>The Options choice rows — the template every Options dropdown goes through — keep the same shape,
    /// so the three surfaces are visibly one pattern.</summary>
    [Fact]
    public void TheOptionsChoiceRowKeepsTheSameGeometry()
    {
        var xaml = Source("UI/App.axaml");

        var tmpl = xaml.IndexOf("vm:ChoiceRowViewModel", StringComparison.Ordinal);
        Assert.True(tmpl >= 0, "the ChoiceRowViewModel template is gone from App.axaml");
        var start = xaml.IndexOf("<Grid", tmpl, StringComparison.Ordinal);
        var end = xaml.IndexOf("</Grid>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the Options choice row is no longer a Grid");
        var row = xaml[start..end];

        Assert.Contains("ColumnDefinitions=\"*,Auto\"", row, StringComparison.Ordinal);
        Assert.Contains("MinWidth=", row, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalAlignment=\"Stretch\"", row, StringComparison.Ordinal);
    }
}
