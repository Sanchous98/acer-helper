using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Localization;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE LIST BEHIND THE BELL, on its own: what an entry is, what a raise does to the entry of the same id, what
/// the two controls on a row mean, and what the badge counts. All of it is view-model state with no window in
/// it, so these rows drive the real commands a click runs rather than a method that looks like them.
///
/// THE RULES HERE ARE THE ONES THAT MAKE HIDING THE MESSAGES SAFE. A banner is visible whether or not it is
/// news; a notification is only found if the count on the bell is right and if the entry it belongs to is still
/// there — so "an id is one entry", "read takes it off the count", "ignored does not come back" and "the words
/// are the current language" are the feature, not the polish.
/// </summary>
public class NotificationListTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory (the test
    /// host runs with its working directory set to the output folder, where a relative path finds nothing).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>An id is one entry, and that is what makes a re-raise safe: the update check runs again, the UI
    /// is rebuilt, the access offer is re-stated — and the user's list does not grow a second copy of a message
    /// they have already dealt with. A DIFFERENT id is a different condition and is its own entry, which is the
    /// other half of the same rule (and the one a "deduplicate by text" implementation would get wrong).
    ///
    /// MUTATION: in <c>NotificationCenter.Raise</c>, drop the <c>Find(id)</c> branch so every raise appends —
    /// the two same-id raises then leave two entries and the first assert goes red.</summary>
    [Fact]
    public void AnIdIsOneEntry_AndADifferentIdIsASecondOne()
    {
        var list = new NotificationCenter();

        list.Raise("update:v1", () => "Update available: v1");
        list.Raise("update:v1", () => "Update available: v1");

        Assert.Single(list.Items);
        Assert.Equal(1, list.UnreadCount);

        list.Raise("hardware-access-needed", () => "Grant hardware access…");

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.UnreadCount);
    }

    /// <summary>A re-raise REPLACES — the newest words and the newest action, on the entry that is already
    /// there, with its read flag kept. Read is a decision the user made about a condition; re-stating the same
    /// condition is not new information, so the badge must not come back for it.
    ///
    /// MUTATION: in <c>Raise</c>, make the existing-id branch add a new entry (or clear the flag on replace) —
    /// the entry count or the read flag goes red.</summary>
    [Fact]
    public void AReRaiseRefreshesTheEntryAndKeepsItRead()
    {
        var list = new NotificationCenter();
        var older = 0;
        list.Raise("update:v1", () => "older", () => older++);
        list.Items[0].ActivateCommand.Execute(null);       // the user reads it (which also runs its action)

        var newer = 0;
        list.Raise("update:v1", () => "newer", () => newer++);

        var entry = Assert.Single(list.Items);
        Assert.Equal("newer", entry.Text);
        Assert.True(entry.IsRead);
        Assert.False(entry.Unread);
        Assert.Equal(0, list.UnreadCount);                 // ...so the badge does not come back for it

        entry.ActivateCommand.Execute(null);
        Assert.Equal(1, newer);                            // a click runs the NEWEST action...
        Assert.Equal(1, older);                            // ...and not the one the entry was raised with
    }

    /// <summary>The badge counts the entries that have not been READ — that number is the whole reason the
    /// messages can be hidden behind a button, so it is asserted through both of its consumers: the count and
    /// the string the bell renders (<c>UnreadBadge</c>, which exists so the badge needs no int-to-text
    /// conversion), and the flag that hides the badge entirely when there is nothing left.
    ///
    /// MUTATION: in <c>NotificationCenter.Recount</c>, count every entry instead of the unread ones — the
    /// count stops falling as entries are read and both middle asserts go red.</summary>
    [Fact]
    public void TheBadgeCountsOnlyWhatHasNotBeenRead()
    {
        var list = new NotificationCenter();
        list.Raise("a", () => "a");
        list.Raise("b", () => "b");

        Assert.Equal(2, list.UnreadCount);
        Assert.Equal("2", list.UnreadBadge);
        Assert.True(list.HasUnread);

        list.Items[0].ActivateCommand.Execute(null);

        Assert.Equal(1, list.UnreadCount);
        Assert.Equal("1", list.UnreadBadge);
        Assert.False(list.Items[0].Unread);
        Assert.True(list.Items[1].Unread);

        list.Items[1].ActivateCommand.Execute(null);

        Assert.Equal(0, list.UnreadCount);
        Assert.False(list.HasUnread);                      // nothing is waiting to be read
        Assert.Equal(2, list.Items.Count);                 // ...and reading is not dismissing
    }

    /// <summary>A click on the entry is the owner's "прочитать": it marks the entry read AND runs the action the
    /// notification carries (the retry-install, the download-update). An entry with no action is still readable
    /// — the messages that only report something have nothing to run, and a click that did nothing at all would
    /// make those rows feel broken.
    ///
    /// MUTATION: drop <c>IsRead = true;</c> from <c>NotificationViewModel.Activate</c> — the flag assert goes
    /// red (and so would the badge rows above, which read through the same flag).</summary>
    [Fact]
    public void ClickingAnEntryReadsItAndRunsTheActionItCarries()
    {
        var list = new NotificationCenter();
        var clicks = 0;
        list.Raise("hardware-access-needed", () => "Grant hardware access…", () => clicks++);
        list.Raise("update:v1", () => "Update available: v1");   // no action: nothing to run

        list.Items[0].ActivateCommand.Execute(null);

        Assert.Equal(1, clicks);
        Assert.True(list.Items[0].IsRead);

        list.Items[1].ActivateCommand.Execute(null);             // must not throw, and must still read

        Assert.True(list.Items[1].IsRead);
        Assert.Equal(0, list.UnreadCount);
    }

    /// <summary>The ✕ on a row is the owner's "проигнорировать": the entry leaves the list. It is NOT the same
    /// control as reading it — an entry can be read and still be there (see the badge test), and this one is
    /// what takes it away.
    ///
    /// MUTATION: make <c>NotificationViewModel.Dismiss</c> set the read flag instead of calling the owner's
    /// remove — the entry stays on the list and this goes red.</summary>
    [Fact]
    public void DismissingRemovesTheEntry()
    {
        var list = new NotificationCenter();
        list.Raise("a", () => "a");
        Assert.False(list.IsEmpty);

        list.Items[0].DismissCommand.Execute(null);

        Assert.Empty(list.Items);
        Assert.True(list.IsEmpty);
        Assert.Equal(0, list.UnreadCount);
    }

    /// <summary>A CONDITION THE USER IGNORED IS NOT RAISED AGAIN THIS SESSION, and that is the decision this row
    /// pins. Ignoring is a decision, not a delay: a still-true condition is re-stated by its source as a matter
    /// of course (the update check runs again on its timer, the access offer is re-stated after a UI rebuild), so
    /// without this an ignored message would be back on the bell minutes later and ignoring it would not have
    /// worked. Nothing is lost by it — the condition itself is untouched (the tray item still offers the update,
    /// the module parameters are still what they are); what the user ignored is being TOLD again. The id is what
    /// makes this safe: a NEWER release is a different id (see <c>NotificationCenter.UpdateId</c>), so it is a
    /// new condition and does reach the user.
    ///
    /// MUTATION: drop the <c>_ignored.Contains(id)</c> early return from <c>NotificationCenter.Raise</c> — the
    /// second raise puts the entry back and the last assert goes red.</summary>
    [Fact]
    public void AnIgnoredConditionIsNotRaisedAgain()
    {
        var list = new NotificationCenter();
        list.Raise("update:v1", () => "Update available: v1");

        list.Items[0].DismissCommand.Execute(null);
        Assert.Empty(list.Items);

        list.Raise("update:v1", () => "Update available: v1");

        Assert.Empty(list.Items);
        Assert.Equal(0, list.UnreadCount);

        // ...while a newer release IS a new condition, which is what the version in the id buys.
        list.Raise(NotificationCenter.UpdateId("v2"), () => "Update available: v2");
        Assert.Single(list.Items);
    }

    /// <summary>The text is evaluated ON EVERY READ, which is what lets a notification outlive a language
    /// switch: the window is rebuilt in the new language and reads the entry it finds, and the entry says itself
    /// in that language because its words are a factory rather than a string captured when it was raised.
    ///
    /// The stand-in for the language is a local the factory closes over rather than <c>Loc.Use</c>: the
    /// translation table is process-wide state that other tests read while these run in parallel, and the app's
    /// own factories are <c>Loc.T</c> calls, so what is pinned here is the mechanism they depend on.
    ///
    /// MUTATION: capture the text at construction in <c>NotificationViewModel</c> (<c>private readonly string
    /// _text = text();</c>) — the second assert goes red, and with it every re-localization a rebuild relies on.</summary>
    [Fact]
    public void TheTextIsTheOneTheLanguageIsInWhenItIsRead()
    {
        var list = new NotificationCenter();
        var russian = false;
        list.Raise("update:v1", () => russian ? "Доступно обновление" : "Update available: v1");

        Assert.Equal("Update available: v1", list.Items[0].Text);

        russian = true;

        Assert.Equal("Доступно обновление", list.Items[0].Text);
    }

    /// <summary>EVERY WORD THE SHELL ITSELF SHOWS IS A KEY THAT EXISTS IN THE RUSSIAN TABLE. The keys are read
    /// out of the view rather than listed here, so a literal added to the window with no row in the table is
    /// caught too — and a missing row is silent, because an unknown key falls back to English (see <c>Loc</c>).
    ///
    /// Scope: MainWindow.axaml, the file this change puts its own literals in (the section views' literals are
    /// their own files' business, and they are not what this test is about).
    ///
    /// MUTATION: delete the <c>["Notifications"]</c> row from Localization/Strings.Ru.cs — this row names the
    /// key the bell's tooltip and the list's heading both use.</summary>
    [Fact]
    public void EveryLiteralTheShellShowsIsInTheRussianTable()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "UI", "MainWindow.axaml"));

        var keys = Regex.Matches(xaml, @"\{l:Tr\s+(?:'([^']*)'|([A-Za-z0-9_]+))\s*\}")
                        .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                        .ToArray();

        Assert.True(keys.Length >= 5, $"only {keys.Length} literals were parsed from the shell — the extraction, not the table, is probably broken");

        foreach (var key in keys)
            Assert.True(Strings.Ru.ContainsKey(key),
                        "the window shows a literal with no entry in Localization/Strings.Ru.cs, so the Russian "
                        + "build shows it in English:\n  " + key);
    }
}
