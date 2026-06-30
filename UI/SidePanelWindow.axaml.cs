using System.Windows.Input;

namespace AcerHelper.UI;

/// <summary>The Options/Lighting drawer window, shown to the left of the main flyout. Sized to its own
/// content (width fixed; height set by AppController to match the main flyout). AppController positions it
/// flush-left of the main card and feeds it title/content; AnimateIn/AnimateOut (from
/// <see cref="FlyoutWindow"/>) reveal/hide the card. Its transparent margins are deliberately INERT (no
/// backdrop dismiss): they're a padding buffer, and the side facing the main flyout is the inner gap, not
/// "outside" — clicking truly outside the app still dismisses via Deactivated.</summary>
public partial class SidePanelWindow : FlyoutWindow
{
    public SidePanelWindow()
    {
        InitializeComponent();
        InitFlyout(Root);
        Closing += (_, e) => { e.Cancel = true; Hide(); };   // hide, never destroy
    }

    public void SetBack(ICommand back) => BackButton.Command = back;

    public void SetPanel(string title, object? content)
    {
        TitleText.Text = title;
        ContentHost.Content = content;
    }
}
