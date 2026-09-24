using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>
/// The session's notifications — the list behind the bell. A message the app used to put on screen as a banner
/// (hardware access not granted, a reboot pending, an update waiting) is raised HERE instead, and the window only
/// shows the bell plus the count of what has not been read.
///
/// SESSION STATE, NOT SETTINGS. Every condition in here describes what the running app is living with right now,
/// which is the same lifetime the access banner had before it ("Same lifetime as the condition it describes: the
/// session" — AppController). Nothing is written to settings.json, and there is no persisted "read" flag: a new
/// session starts with an empty list and re-derives whatever is still true (see AppController's startup raises).
///
/// IT OUTLIVES THE VIEW MODEL, and that is the point of it being a separate object handed in rather than a field
/// of <see cref="MainViewModel"/>: a live language switch rebuilds the whole string-baked UI
/// (<c>AppController.RebuildForLanguage</c>), and a notification is a condition the user is still living with —
/// it must survive that swap, read flags included. The one string-baked part, the TEXT, needs no carrying: an
/// entry holds a factory that is evaluated on every read, so the rebuilt window reads it in the new language
/// (the same rule <c>Loc</c> states for every other string: re-read on construction, no change notification).
///
/// SOURCES ARE PRODUCERS. Whoever raises a message supplies an id, a text factory and (optionally) the action a
/// click should run — the retry-install, the download-update. This class decides only how the list behaves:
/// an id is one entry, a raise refreshes it rather than stacking a second copy, and an IGNORED id does not come
/// back for the rest of the session.
/// </summary>
public sealed class NotificationCenter : ObservableObject
{
    /// <summary>The id of "the Linux permission files are not installed yet" — the offer to install them.
    /// Stable, because the same condition is re-raised by every startup and every UI rebuild.</summary>
    public const string HardwareAccessNeeded = "hardware-access-needed";

    /// <summary>The id of "a reboot is pending": the files are in /etc but the module parameters are not live.
    ///
    /// DELIBERATELY A DIFFERENT ID from the offer above, although the two are states of one problem and only one
    /// is ever true at a time. The reason is the state where this entry is the ONLY way out: a retry is the
    /// recovery short of a reboot, and the installer never offers itself again (the files are already in /etc,
    /// so <c>RulesNeeded()</c> is false). Were the two one id, an entry the user had ignored while it was only an
    /// offer would swallow this instruction — the dead end the old banner's two messages were arranged to
    /// prevent. <see cref="MainViewModel.SetHardwareAccessRebootPending"/> retires the offer when it raises this.
    /// </summary>
    public const string HardwareAccessRebootPending = "hardware-access-reboot-pending";

    /// <summary>The id of the update condition, keyed by the VERSION it is about: the periodic re-check of the
    /// same release refreshes this entry, while a newer release is a new condition and reaches the user even if
    /// the previous one was ignored. <see cref="UpdateIdPrefix"/> names the family, which is what the superseded
    /// member of it is retired by.</summary>
    public const string UpdateIdPrefix = "update:";

    /// <summary>The update condition's id, keyed by the VERSION it is about. That is what makes the id a
    /// statement about the condition rather than about the feature: the periodic re-check of the same release
    /// refreshes this entry, while a newer release is a new condition and reaches the user even if the previous
    /// one was ignored.</summary>
    public static string UpdateId(string version) => UpdateIdPrefix + version;

    private readonly ObservableCollection<NotificationViewModel> _items = [];
    // The ids the user has ignored this session. A re-raise is what a still-true condition does — the update
    // check runs again, the access offer is re-stated after a UI rebuild — so without this an ignored message
    // would be back on the bell a few minutes later, i.e. ignoring it would not have worked. Nothing is lost by
    // it: the condition itself is untouched (the tray item still offers the update, the module parameters are
    // still what they are); what the user ignored is being TOLD about it again.
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private int _unread;

    /// <summary>The list, newest last. The window renders it through an <c>ItemsControl</c>; the collection is
    /// mutable in place so raises and dismissals update the open drop-down without rebuilding it.</summary>
    public ObservableCollection<NotificationViewModel> Items => _items;

    /// <summary>How many entries the user has not read yet — the number on the bell's badge.</summary>
    public int UnreadCount => _unread;

    /// <summary>The badge's own text. A string rather than the count, so the binding needs no int-to-text
    /// conversion, and capped so a long list cannot widen the bell.</summary>
    public string UnreadBadge => _unread > 9 ? "9+" : _unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the badge is shown at all. This is the whole reason the messages can be hidden behind a
    /// button: without it, a bell is a place notifications go to be missed.</summary>
    public bool HasUnread => _unread > 0;

    /// <summary>Whether there is anything to show — the drop-down says so rather than opening on nothing.</summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>Raise a notification (or refresh the one this id already has). <paramref name="text"/> is a
    /// FACTORY, not a string: an entry that survives a language rebuild has to be able to say itself in the new
    /// language, and evaluating it per read is the same rule the rest of the UI follows.
    ///
    /// <paramref name="details"/> is the optional BODY (the update notice's changelog). An entry with a body does
    /// not run its action when clicked — it EXPANDS, and only the body's Install button acts (see
    /// <see cref="NotificationViewModel"/>). Pass null (the default) for a message whose click IS its action.
    ///
    /// An id that is already on the list is REPLACED — same entry, refreshed text and action, read flag kept.
    /// That is what makes a re-raise harmless: the update check runs again, the UI is rebuilt, and the user's
    /// list does not grow a second copy of a message they have already dealt with.
    ///
    /// A raise of an id the user has IGNORED is dropped (see <see cref="Ignore"/>).</summary>
    public void Raise(string id, Func<string> text, Action? action = null, Func<string>? details = null)
    {
        if (_ignored.Contains(id)) return;

        if (Find(id) is { } existing) { existing.Replace(text, action, details); return; }

        var entry = new NotificationViewModel(this, id, text, action, details);
        entry.PropertyChanged += OnEntryChanged;
        _items.Add(entry);
        OnPropertyChanged(nameof(IsEmpty));
        Recount();
    }

    /// <summary>Retract an id, and keep it retracted for the session: the condition is over (the install took),
    /// or the user dismissed it. Either way the entry leaves the list, and a later raise of the same id — which
    /// for a still-true condition is exactly what a source does — does not bring it back.</summary>
    public void Ignore(string id)
    {
        _ignored.Add(id);
        if (Find(id) is { } entry) Drop(entry);
    }

    /// <summary>Retract every entry whose id starts with <paramref name="prefix"/> EXCEPT <paramref name="keep"/>
    /// — a SUPERSEDE, not an ignore: the ids are not poisoned, because what is wrong with those entries is that
    /// they are out of date, not that the user has dismissed them (so an id somehow raised again would be news
    /// rather than something already ignored). The relation this exists for is the one the centre cannot see by
    /// itself — that the VERSION is part of the condition (see <see cref="UpdateIdPrefix"/>): the superseded offer
    /// is not merely stale, its Install button installs the older release.
    ///
    /// Walked backwards because the retraction mutates the list being walked.</summary>
    public void RetireFamilyExcept(string prefix, string keep)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var entry = _items[i];
            if (entry.Id == keep || !entry.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            Drop(entry);
        }
    }

    /// <summary>Expand every entry whose id starts with <paramref name="prefix"/>. Reached from the TRAY item,
    /// which is not itself an install trigger: its click brings the user to the update's body (changelog + the
    /// explicit Install button) rather than straight into an install. No-op if there is no such entry.</summary>
    public void ExpandFamily(string prefix)
    {
        foreach (var entry in _items)
            if (entry.Id.StartsWith(prefix, StringComparison.Ordinal)) entry.IsExpanded = true;
    }

    /// <summary>Take one entry off the list, without deciding whether its id can come back — the caller of
    /// <see cref="Ignore"/> has decided that, and <see cref="RetireFamilyExcept"/> has decided it does not.</summary>
    private void Drop(NotificationViewModel entry)
    {
        entry.PropertyChanged -= OnEntryChanged;
        _items.Remove(entry);
        OnPropertyChanged(nameof(IsEmpty));
        Recount();
    }

    /// <summary>The per-entry dismiss control: same thing as <see cref="Ignore"/>, reached from the entry the
    /// user is looking at.</summary>
    internal void Remove(NotificationViewModel entry) => Ignore(entry.Id);

    private NotificationViewModel? Find(string id)
    {
        foreach (var item in _items)
            if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
        return null;
    }

    // A read flag is the only entry property the badge depends on, and re-reading it is what makes the badge
    // count down as the user works through the list.
    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationViewModel.IsRead)) Recount();
    }

    private void Recount()
    {
        var unread = 0;
        foreach (var item in _items)
            if (!item.IsRead) unread++;
        if (unread == _unread) return;

        _unread = unread;
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(UnreadBadge));
        OnPropertyChanged(nameof(HasUnread));
    }
}
