using System.Windows.Input;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The Options/Lighting side panel: a separate acrylic window pinned to the left of the main
/// flyout. Shown/hidden without animation; content/title/back-command are set by AppController.</summary>
public partial class SidePanelWindow : Window
{
    public SidePanelWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Opened += (_, _) => WindowEffects.ApplyAcrylic(this);   // rounded corners + modern acrylic
        Closing += (_, e) => { e.Cancel = true; Hide(); };       // reused across opens; never truly close
    }

    public void SetBack(ICommand back) => BackButton.Command = back;

    public void SetPanel(string title, object? content)
    {
        TitleText.Text = title;
        ContentHost.Content = content;
    }
}
