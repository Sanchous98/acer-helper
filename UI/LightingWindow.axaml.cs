using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The lighting window. Layout/bindings live in LightingWindow.axaml; this holds only the
/// window behaviour: acrylic backdrop and hide-on-close (the app keeps running in the tray).</summary>
public partial class LightingWindow : Window
{
    public LightingWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }
}
