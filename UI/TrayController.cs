using System.Runtime.InteropServices;
using AcerHelper.Features;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AcerHelper.UI;

/// <summary>Owns the tray icon + native menu and keeps them in sync with the current profile (the icon
/// is a solid swatch of the active profile's accent colour). All interactions are delegated back to the
/// <see cref="AppController"/> via the callbacks passed to the constructor.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly NativeMenu _menu;
    private readonly Dictionary<string, WindowIcon> _icons = new();
    private readonly Dictionary<string, NativeMenuItem> _menuItems = new();
    private NativeMenuItem? _updateItem;

    public TrayController(IDevice device, Action<PerformanceProfile> applyProfile,
                          Action toggleMain, Action openMain, Action showLighting, Action exit)
    {
        _menu = BuildMenu(device, applyProfile, openMain, showLighting, exit);
        _tray = new TrayIcon
        {
            ToolTipText = "Acer Helper",
            IsVisible = true,
            Icon = MakeIcon(Colors.Gray),
            Menu = _menu,
        };
        _tray.Clicked += (_, _) => toggleMain();
        TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { _tray });
    }

    /// <summary>Add an "update available" item at the top of the menu (once), opening the download page.</summary>
    public void SetUpdate(string label, Action open)
    {
        if (_updateItem != null) return;
        _updateItem = new NativeMenuItem { Header = label };
        _updateItem.Click += (_, _) => open();
        _menu.Items.Insert(0, _updateItem);
        _menu.Items.Insert(1, new NativeMenuItemSeparator());
    }

    /// <summary>Reflect the current profile in the tooltip, icon and the menu's radio state.</summary>
    public void Update(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable)
    {
        _tray.ToolTipText = "Acer Helper — " + (current?.DisplayName ?? "?");
        if (current != null) _tray.Icon = ProfileIcon(current);
        foreach (var kv in _menuItems)
        {
            kv.Value.IsChecked = current?.Id == kv.Key;
            kv.Value.IsEnabled = selectable.Any(p => p.Id == kv.Key);
        }
    }

    public void Dispose()
    {
        _tray.IsVisible = false;
        _tray.Dispose();
    }

    private NativeMenu BuildMenu(IDevice device, Action<PerformanceProfile> applyProfile,
                                 Action openMain, Action showLighting, Action exit)
    {
        var menu = new NativeMenu();
        var profiles = device.PowerProfiles?.All ?? [];
        foreach (var p in profiles)
        {
            var item = new NativeMenuItem { Header = p.DisplayName, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => applyProfile(p);
            _menuItems[p.Id] = item;
            menu.Items.Add(item);
        }
        if (profiles.Count > 0) menu.Items.Add(new NativeMenuItemSeparator());
        var show = new NativeMenuItem { Header = "Show" }; show.Click += (_, _) => openMain(); menu.Items.Add(show);
        if (device.Lighting != null || device.KeyboardBrightness != null) { var light = new NativeMenuItem { Header = "Lighting…" }; light.Click += (_, _) => showLighting(); menu.Items.Add(light); }
        var ex = new NativeMenuItem { Header = "Exit" }; ex.Click += (_, _) => exit(); menu.Items.Add(ex);
        return menu;
    }

    private WindowIcon ProfileIcon(PerformanceProfile p)
    {
        if (_icons.TryGetValue(p.Id, out var cached)) return cached;
        var c = p.Accent is { } a ? Color.FromRgb(a.R, a.G, a.B) : Colors.Gray;
        return _icons[p.Id] = MakeIcon(c);
    }

    private static WindowIcon MakeIcon(Color c)
    {
        const int sz = 32;
        var wb = new WriteableBitmap(new PixelSize(sz, sz), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = new byte[sz * sz * 4];
        for (var i = 0; i < sz * sz; i++)
        {
            var o = i * 4;
            px[o] = c.B;
            px[o + 1] = c.G;
            px[o + 2] = c.R;
            px[o + 3] = 255;
        }
        using (var fb = wb.Lock()) Marshal.Copy(px, 0, fb.Address, px.Length);
        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
