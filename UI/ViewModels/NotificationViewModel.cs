using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>
/// One entry in the bell's drop-down: a sentence, whether the user has read it, and — for some entries — an
/// expandable BODY with its own action button. The owner is <see cref="NotificationCenter"/>, which decides which
/// ids exist; this type is only one row of that list and what the row's controls do.
///
/// THE TWO CONTROLS ARE THE OWNER'S TWO VERBS. Clicking the entry READS it; and then either the entry has a body
/// or it does not:
///   * NO BODY (the retry-install, a plain message): the click IS the action — reading it and running it are the
///     one gesture, which is what those messages mean.
///   * WITH A BODY (the update notice): the click EXPANDS the body — the changelog plus its explicit Install
///     button — and installs NOTHING. This is deliberate: an available update is something to read about before
///     acting on, and an accidental click must not start a download/install. Only <see cref="Install"/> does that.
/// The small ✕ beside the entry IGNORES it: the entry leaves the list and its id will not be raised again this
/// session. Neither control is the other one — a message can be read and still be there, and ignoring one never
/// runs its action.
/// </summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly NotificationCenter _owner;
    private Func<string> _text;
    private Action? _action;
    private Func<string>? _details;
    // Details are shown only while the entry is expanded, and only an entry that HAS details can expand (Activate).

    internal NotificationViewModel(NotificationCenter owner, string id, Func<string> text, Action? action,
                                   Func<string>? details = null)
    {
        _owner = owner;
        Id = id;
        _text = text;
        _action = action;
        _details = details;
    }

    /// <summary>What this notification is about, as the source named it. Identifies the entry: a raise of the
    /// same id refreshes this one rather than adding a second (see <see cref="NotificationCenter.Raise"/>).</summary>
    public string Id { get; }

    /// <summary>The sentence on screen, evaluated on every read rather than captured: the window that shows it is
    /// rebuilt for a language switch, and this is what makes the entry come back in the new language while the
    /// list itself — the condition the user is still living with — survives the rebuild untouched.</summary>
    public string Text => _text();

    /// <summary>Whether this entry has an expandable body. The update notice does (its changelog); the
    /// hardware-access offers do not, and their click keeps running their action.</summary>
    public bool HasDetails => _details != null;

    /// <summary>The body shown while expanded (the release changelog), and empty for an entry with none. Like
    /// <see cref="Text"/> it is evaluated per read, so it does not need carrying across a language rebuild.</summary>
    public string Details => _details?.Invoke() ?? "";

    /// <summary>Whether there is a non-empty body to show. A release published with an empty body still gets the
    /// Install button, but no empty "Changelog" heading.</summary>
    public bool HasChangelog => !string.IsNullOrWhiteSpace(Details);

    /// <summary>Whether the body is showing. PRESENTATION state, per entry: a rebuilt window comes back with an
    /// expanded entry still expanded, because the entry itself survives the rebuild (see
    /// <see cref="NotificationCenter"/>).</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Set when the user has read the entry (a click on it). Drives the unread dot here and the badge
    /// count through the owner; it is never reset — read is read for the session.</summary>
    [ObservableProperty] private bool _isRead;

    /// <summary>The unread dot on the row. Derived rather than stored so there is one piece of state, not two.</summary>
    public bool Unread => !IsRead;

    // Keep the derived flag in step with the stored one (the [ObservableProperty] setter raises this).
    partial void OnIsReadChanged(bool value) => OnPropertyChanged(nameof(Unread));

    /// <summary>A click on the entry. An entry WITH a body toggles that body and runs nothing — the whole point
    /// is that the update's changelog and its Install button are one click away and that this click does not
    /// install. An entry with no body keeps the old meaning: read it, then run what it carries. Reading happens
    /// first either way, and is deliberately not conditional on being unread — a second click must still be a
    /// click that does something.</summary>
    [RelayCommand]
    private void Activate()
    {
        IsRead = true;
        if (HasDetails) { IsExpanded = !IsExpanded; return; }
        _action?.Invoke();
    }

    /// <summary>The expanded body's Install button — the ONLY gesture that starts an install. Kept apart from
    /// <see cref="Activate"/> so that opening the changelog can never be mistaken for agreeing to the update.</summary>
    [RelayCommand]
    private void Install()
    {
        IsRead = true;
        _action?.Invoke();
    }

    /// <summary>The ✕ on the row.</summary>
    [RelayCommand] private void Dismiss() => _owner.Remove(this);

    /// <summary>Take the newest text and action for this id (a re-raise of a condition already on the list).
    /// The read flag is NOT touched: the user has read this message, and re-stating the condition does not make
    /// it unread again. A live binding re-reads the text through the property notification.</summary>
    internal void Replace(Func<string> text, Action? action, Func<string>? details)
    {
        _text = text;
        _action = action;
        _details = details;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(HasChangelog));
    }
}
