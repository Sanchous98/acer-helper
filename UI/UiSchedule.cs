using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>The UI-thread posters the shared <see cref="Infrastructure.PeriodicSchedule"/> needs in its UI mode:
/// the one place that states the priority each kind of periodic UI work runs at, so no site re-derives it and
/// none falls back to the toolkit's default <c>Background</c> — the idle priority that starves on the GLib
/// backend (see <see cref="Infrastructure.PeriodicSchedule"/> for the whole reason).
///
/// These are the injected delegate, not a call made from the schedule: <see cref="Infrastructure.PeriodicSchedule"/>
/// lives in Infrastructure, which may not name Avalonia at all, so the toolkit half stays here in the UI.</summary>
internal static class UiSchedule
{
    /// <summary>Ordinary periodic UI work — the debounces and the lighting re-apply burst. <c>Normal</c> is well
    /// above the toolkit default (Background) and above input, so the tick runs promptly while the window is
    /// live rather than waiting for it to go idle.</summary>
    public static Action<Action> Normal { get; } = a => Dispatcher.UIThread.Post(a, DispatcherPriority.Normal);

    /// <summary>Animation (the fan spinner): <c>Render</c> runs just before the frame is rendered, so the
    /// rotation this tick writes is the one that frame shows. Still far above the idle priority.</summary>
    public static Action<Action> Render { get; } = a => Dispatcher.UIThread.Post(a, DispatcherPriority.Render);
}
