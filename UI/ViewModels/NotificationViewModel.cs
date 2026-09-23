using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>
/// One entry in the bell's drop-down: a sentence, whatever action a click on it should run, and whether the user
/// has read it yet. The owner is <see cref="NotificationCenter"/>, which decides which ids exist; this type is
/// only one row of that list and what the row's two controls do.
///
/// THE TWO CONTROLS ARE THE OWNER'S TWO VERBS. Clicking the entry READS it and runs its action (the retry-install,
/// the download-update) — a message with something to do is a message whose click does it, and reading it is what
/// takes it off the badge. The small ✕ beside it IGNORES it: the entry leaves the list and its id will not be
/// raised again this session. Neither control is the other one — a message can be read and still be there, and
/// ignoring one never runs its action.
/// </summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly NotificationCenter _owner;
    private Func<string> _text;
    private Action? _action;

    internal NotificationViewModel(NotificationCenter owner, string id, Func<string> text, Action? action)
    {
        _owner = owner;
        Id = id;
        _text = text;
        _action = action;
    }

    /// <summary>What this notification is about, as the source named it. Identifies the entry: a raise of the
    /// same id refreshes this one rather than adding a second (see <see cref="NotificationCenter.Raise"/>).</summary>
    public string Id { get; }

    /// <summary>The sentence on screen, evaluated on every read rather than captured: the window that shows it is
    /// rebuilt for a language switch, and this is what makes the entry come back in the new language while the
    /// list itself — the condition the user is still living with — survives the rebuild untouched.</summary>
    public string Text => _text();

    /// <summary>Set when the user has read the entry (a click on it). Drives the unread dot here and the badge
    /// count through the owner; it is never reset — read is read for the session.</summary>
    [ObservableProperty] private bool _isRead;

    /// <summary>The unread dot on the row. Derived rather than stored so there is one piece of state, not two.</summary>
    public bool Unread => !IsRead;

    // Keep the derived flag in step with the stored one (the [ObservableProperty] setter raises this).
    partial void OnIsReadChanged(bool value) => OnPropertyChanged(nameof(Unread));

    /// <summary>A click on the entry: read it, then run what it carries (if anything). Deliberately in that
    /// order, and deliberately not conditional on being unread — clicking an update notice twice must not be a
    /// click that does nothing the second time.</summary>
    [RelayCommand]
    private void Activate()
    {
        IsRead = true;
        _action?.Invoke();
    }

    /// <summary>The ✕ on the row.</summary>
    [RelayCommand] private void Dismiss() => _owner.Remove(this);

    /// <summary>Take the newest text and action for this id (a re-raise of a condition already on the list).
    /// The read flag is NOT touched: the user has read this message, and re-stating the condition does not make
    /// it unread again. A live binding re-reads the text through the property notification.</summary>
    internal void Replace(Func<string> text, Action? action)
    {
        _text = text;
        _action = action;
        OnPropertyChanged(nameof(Text));
    }
}
