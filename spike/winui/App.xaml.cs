using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Spike.WinUI;

public partial class App : Application
{
    private MainWindow? _window;
    private TaskbarIcon? _tray;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        // The tray. A right-click menu and a profile-coloured icon are the real app's job; this proves the
        // mechanism: an icon appears, and a left-click brings the flyout up (or puts it away).
        _tray = new TaskbarIcon
        {
            ToolTipText = "Acer Helper (spike)",
            IconSource = new GeneratedIconSource
            {
                Text = "A",
                FontSize = 30,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            },
            LeftClickCommand = new RelayCommand(() => _window?.Toggle()),
        };
        _tray.ForceCreate();

        _window.Activate();
    }
}

/// <summary>A minimal ICommand so the spike does not need a toolkit MVVM package.</summary>
internal sealed class RelayCommand(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
