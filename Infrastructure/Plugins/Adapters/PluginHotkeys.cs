using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// THE DECLARED, TESTABLE SEAM THE HOTKEY ADAPTER SITS ON, and the reason it is a seam rather than native
/// plumbing (docs/vendor-plugins.md §3.2, §3.5, §4.2, §7 item 9).
///
/// §3.5 says hotkeys are "event-only, raised through <c>ah_set_event_sink</c>": the plugin pushes events INTO the
/// host, rather than the host polling the plugin. The native half of that — an <c>[UnmanagedCallersOnly]</c> sink
/// the plugin calls back on, marshalling a payload into a <see cref="HotkeyAction"/> — is NOT built yet. Task T4's
/// binding (<c>INativePluginBinding</c>) has no sink export and this task may not add one, and the design itself
/// schedules hotkeys LAST (§7 item 9): they are the one capability that needs the reverse direction, so they land
/// with the plugin that first needs them rather than as speculative native plumbing.
///
/// What that leaves is this interface: the MANAGED shape the future sink wiring will feed. When the native sink is
/// built, its callback marshals the payload and calls <see cref="RaisePressed"/> / <see cref="RaiseActivity"/>;
/// <see cref="PluginHotkeys"/> needs no change at that point, and neither does any caller of <c>IHotkeys</c>.
/// Until then the adapter is exercised with a hand-written source, exactly as the in-tree fake ports stand in for
/// hardware — which is what makes the marshalling and the raise itself testable with no plugin and no pointer.
///
/// The two events mirror <see cref="IHotkeys"/>'s own two, and that is deliberate: a source is the plugin's view
/// of the same events, so the adapter is a marshalling pass-through and nothing more.
/// </summary>
internal interface IPluginEventSource
{
    /// <summary>A mapped special key was pressed.</summary>
    event Action<HotkeyAction>? Pressed;

    /// <summary>Any special-key/raw input was observed, mapped or not — <see cref="IHotkeys.InputActivity"/>.</summary>
    event Action? InputActivity;
}

/// <summary>
/// The hotkey adapter (docs/vendor-plugins.md §3.5, §4.2, §7 item 9): implements the host <see cref="IHotkeys"/>
/// contract over an <see cref="IPluginEventSource"/>, marshalling every raise onto the
/// <see cref="SynchronizationContext"/> captured at construction.
///
/// WHY THE MARSHAL. The plugin's key threads are its own (§4.3: "the plugin's key threads are its own"), while
/// <c>AppController</c> subscribes to <see cref="Pressed"/>/<see cref="InputActivity"/> on the UI thread, and the
/// in-host hotkey port already documents "AppController marshals to the UI thread"
/// (AcerHotkeys.Linux.cs:140). Capturing the context at construction — where the adapter is built by composition
/// on the UI thread — and posting every raise through it keeps the contract the subscribers already rely on
/// without the caller having to remarshal. A null context (a test, or a host with no UI context) raises inline,
/// which is the same behaviour a plain event has.
///
/// SUBSCRIBER EXCEPTIONS ARE SWALLOWED, matching the in-host port: "subscriber exceptions must not kill the read
/// loop" (AcerHotkeys.Linux.cs:135-142). A handler bug must not turn into a dead key or an unhandled throw on a
/// background thread.
///
/// THE SOURCE IS DISPOSED WITH THE ADAPTER if it is disposable, so a native sink implementation that owns a
/// registration can release it. <see cref="IHotkeys"/> is <see cref="IDisposable"/>, so that is the one lifetime
/// hook the contract already provides.
/// </summary>
internal sealed class PluginHotkeys : IHotkeys
{
    private readonly IPluginEventSource _source;
    private readonly SynchronizationContext? _ui;

    public PluginHotkeys(IPluginEventSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _ui = SynchronizationContext.Current;

        // Subscribe once, here, so the adapter owns the whole lifetime: the events below are raised by the source
        // on a plugin thread and forwarded to the subscriber's thread through the captured context.
        _source.Pressed += OnPressed;
        _source.InputActivity += OnInputActivity;
    }

    public event Action<HotkeyAction>? Pressed;
    public event Action? InputActivity;

    private void OnPressed(HotkeyAction action) => Raise(() => Pressed?.Invoke(action));

    private void OnInputActivity() => Raise(() => InputActivity?.Invoke());

    /// <summary>Post <paramref name="raise"/> to the captured context, or run it inline when there is none.
    /// Exceptions from a subscriber are swallowed — see the class note.</summary>
    private void Raise(Action raise)
    {
        if (_ui is null)
        {
            SafeRaise(raise);
            return;
        }

        _ui.Post(_ => SafeRaise(raise), null);
    }

    private static void SafeRaise(Action raise)
    {
        try { raise(); }
        catch { /* handler bug — swallow, keep listening (AcerHotkeys.Linux.cs:135-142) */ }
    }

    /// <summary>Detach from the source and dispose it if it owns anything. Idempotent: a second dispose unsubscribes
    /// again (a no-op on the event) without double-disposing the source.</summary>
    public void Dispose()
    {
        _source.Pressed -= OnPressed;
        _source.InputActivity -= OnInputActivity;
        (_source as IDisposable)?.Dispose();
    }
}
