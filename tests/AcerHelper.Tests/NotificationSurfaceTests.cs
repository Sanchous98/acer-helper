using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE THREE SOURCES THAT NOW RAISE NOTIFICATIONS, as the rest of the app reaches them, and the two properties
/// of the surface that a single view model cannot show on its own: that a REBUILD keeps the list, and that the
/// list a build is handed is the session's rather than a fresh one.
///
/// These were banners in this window before the bell existed (the update offer, the hardware-access offer, the
/// reboot instruction), so what is pinned here is what those banners were pinned for: the reboot message is the
/// only surface that survives the refresh loop, and the action its own words promise — "(click to retry)" —
/// travels with it.
/// </summary>
public class NotificationSurfaceTests
{
    private const string RebootCaption =
        "Restart your computer to finish enabling the unlocked controls (click to retry).";

    /// <summary>The bell itself: one control opens the list and closes it (like the drawer buttons), and the
    /// list is the only thing that toggles — the entries are the sources' business, not the button's.
    ///
    /// MUTATION: set <c>IsNotificationsOpen = true</c> in <c>ToggleNotifications</c> instead of the negation —
    /// the second click no longer closes the list and the last assert goes red.</summary>
    [Fact]
    public void TheBellOpensAndClosesTheList()
    {
        var vm = Dashboard(new NotificationCenter());
        Assert.False(vm.IsNotificationsOpen);          // a window opens with the drop-down away

        vm.ToggleNotificationsCommand.Execute(null);
        Assert.True(vm.IsNotificationsOpen);

        vm.ToggleNotificationsCommand.Execute(null);
        Assert.False(vm.IsNotificationsOpen);
    }

    /// <summary>THE REBOOT STATE REPLACES THE OFFER, and it carries the retry its own words promise. This is
    /// the same arrangement the single two-message banner had: once the files are in /etc the offer is over (the
    /// installer is idempotent and never offers itself again), so the user must not be left with a "grant
    /// access" row for access they already granted — and the reboot row must be there, UNREAD, because it is
    /// news the user has not seen.
    ///
    /// MUTATION: drop <c>Notifications.Ignore(NotificationCenter.HardwareAccessNeeded)</c> from
    /// <c>SetHardwareAccessRebootPending</c> — both entries are on the list and the single-entry assert goes red.</summary>
    [Fact]
    public void TheRebootStateRetiresTheOfferAndCarriesTheRetryItsWordsPromise()
    {
        var clicks = 0;
        var vm = Dashboard(new NotificationCenter());
        vm.SetHardwareAccessNeeded(() => clicks++);
        var offer = Assert.Single(vm.Notifications.Items);
        Assert.Equal("Grant hardware access…", offer.Text);

        vm.SetHardwareAccessRebootPending(() => clicks++);

        var reboot = Assert.Single(vm.Notifications.Items);
        Assert.NotEqual(offer.Id, reboot.Id);
        Assert.Equal(RebootCaption, reboot.Text);
        Assert.True(reboot.Unread);
        Assert.Equal(1, vm.Notifications.UnreadCount);

        reboot.ActivateCommand.Execute(null);

        Assert.Equal(1, clicks);                        // the click is the retry the words promise
        Assert.True(reboot.IsRead);
    }

    /// <summary>THE DEAD END THE TWO IDS EXIST TO PREVENT. The offer and the reboot instruction are different
    /// ids although only one of them is ever true at a time, and this is why: an offer the user IGNORED must not
    /// swallow the reboot instruction that follows it. Were they one id, ignoring the offer would suppress the
    /// only message that says what to do — in the state where a retry is the one recovery short of a reboot and
    /// the installer never offers itself again.
    ///
    /// MUTATION: give both states the same id (raise <c>NotificationCenter.HardwareAccessNeeded</c> in
    /// <c>SetHardwareAccessRebootPending</c>) — the reboot row is suppressed and the single-entry assert goes red.</summary>
    [Fact]
    public void AnOfferTheUserIgnoredCannotSwallowTheRebootInstruction()
    {
        var vm = Dashboard(new NotificationCenter());
        vm.SetHardwareAccessNeeded(() => { });

        vm.Notifications.Items[0].DismissCommand.Execute(null);   // the user ignores the offer
        Assert.Empty(vm.Notifications.Items);

        vm.SetHardwareAccessRebootPending(() => { });

        var reboot = Assert.Single(vm.Notifications.Items);
        Assert.Equal(RebootCaption, reboot.Text);
        Assert.True(reboot.Unread);
    }

    /// <summary>The install took and the parameters are live: the condition is over, so BOTH entries are
    /// retracted — whichever of the two the user was looking at — and neither comes back. That last part is the
    /// point: this is the branch that replaced the old banner's <c>NeedsHardwareAccess = false;</c>, and the
    /// files are in /etc by now, so a "grant access" row raised again would be a row about nothing.
    ///
    /// MUTATION: drop the second <c>Ignore</c> from <c>ClearHardwareAccess</c> — the last assert goes red when
    /// the offer is raised again.</summary>
    [Fact]
    public void TheInstallThatTookTakesTheEntryOffTheBell()
    {
        var vm = Dashboard(new NotificationCenter());
        vm.SetHardwareAccessNeeded(() => { });
        Assert.Single(vm.Notifications.Items);

        vm.ClearHardwareAccess();

        Assert.Empty(vm.Notifications.Items);
        Assert.Equal(0, vm.Notifications.UnreadCount);

        vm.SetHardwareAccessNeeded(() => { });
        vm.SetHardwareAccessRebootPending(() => { });

        Assert.Empty(vm.Notifications.Items);
    }

    /// <summary>A LANGUAGE REBUILD KEEPS THE NOTIFICATIONS AND THEIR READ FLAGS. The whole string-baked UI is
    /// destroyed and rebuilt on a switch (AppController.RebuildForLanguage), and the list is session state that
    /// lives outside that swap — so the fresh view model finds the entries it had, with read staying read, while
    /// the drop-down itself (presentation) comes up closed with the new window.
    ///
    /// MUTATION: make <c>MainViewModel</c> own its list (<c>Notifications = new NotificationCenter();</c>,
    /// ignoring the one it is handed) — the rebuilt view model then shows an empty list and the counts go red.</summary>
    [Fact]
    public void ALanguageRebuildKeepsTheNotificationsAndTheirReadFlags()
    {
        // The one list the session owns: AppController's field, handed to the view model each time BuildUi runs.
        var session = new NotificationCenter();

        var before = Dashboard(session);
        before.SetUpdate("9.9.9", () => { });
        before.SetHardwareAccessRebootPending(() => { });
        before.Notifications.Items[0].ActivateCommand.Execute(null);   // the user reads the update notice
        before.ToggleNotificationsCommand.Execute(null);               // ...with the list open

        var rebuilt = Dashboard(session);                              // what RebuildForLanguage builds

        Assert.Equal(2, rebuilt.Notifications.Items.Count);
        Assert.True(rebuilt.Notifications.Items[0].IsRead);            // read stays read
        Assert.False(rebuilt.Notifications.Items[1].IsRead);
        Assert.Equal(1, rebuilt.Notifications.UnreadCount);
        Assert.False(rebuilt.IsNotificationsOpen);                     // the drop-down is not re-opened for it
    }

    /// <summary>ONE UPDATE OFFER AT A TIME, and the offer is the newest release's. The id carries the version, so
    /// the periodic re-check of the same release refreshes the entry the user already has (and an ignored one
    /// stays ignored) while a NEWER release is a new condition that reaches the bell. The older offer is retired
    /// as the newer one is raised, and that is not tidiness: its click installs the version it was raised for, so
    /// leaving it beside the new one is a row that installs an OLDER release.
    ///
    /// MUTATIONS: use a fixed id in <c>MainViewModel.SetUpdate</c> (<c>Notifications.Raise("update", …)</c>) —
    /// the id assert goes red; drop the <c>RetireFamilyExcept</c> call — the single-entry assert goes red (and
    /// passing the wrong id as the one to spare retires the entry the raise is about to refresh, which the first
    /// single-entry assert catches).</summary>
    [Fact]
    public void ANewerReleaseSupersedesTheOfferAlreadyOnTheBell()
    {
        var vm = Dashboard(new NotificationCenter());

        vm.SetUpdate("1.0", () => { });
        vm.SetUpdate("1.0", () => { });          // the same release, found again by a later check
        var first = Assert.Single(vm.Notifications.Items);
        Assert.Equal(NotificationCenter.UpdateId("1.0"), first.Id);
        Assert.False(first.IsRead);

        vm.SetUpdate("1.1", () => { });

        var newer = Assert.Single(vm.Notifications.Items);   // ...the superseded offer is gone, not stacked
        Assert.Equal(NotificationCenter.UpdateId("1.1"), newer.Id);
        Assert.Contains("1.1", newer.Text, StringComparison.Ordinal);
        Assert.False(newer.IsRead);
    }

    /// <summary>THE LIST IS THE SESSION'S, AND THE BUILD IS GIVEN IT. This is the one half of the rebuild
    /// property that a view-model test cannot reach — <c>AppController</c> is not constructible here — so it is
    /// read out of the tree: <c>BuildUi</c> must hand its fresh view model the field, never one of its own.
    /// Getting this wrong is silent and partial: the bell still works, and only the things that matter about it
    /// (a notification surviving a rebuild, an ignored one staying ignored) quietly stop being true.
    ///
    /// MUTATION: pass <c>new NotificationCenter()</c> as that argument instead of the field — this goes red.</summary>
    [Fact]
    public void TheUiBuildHandsTheSessionListToTheViewModelItBuilds()
        => Assert.Contains("lighting, _notifications)", Source("UI/AppController.cs"), StringComparison.Ordinal);

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory (the test
    /// host runs with its working directory set to the output folder, where a relative path finds nothing).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A source file of this repository, read from the compiler's path. The existence assertion matters:
    /// a moved or renamed file must fail loudly here rather than let a "does not contain" claim pass on an empty
    /// string.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The dashboard over a machine with no capabilities, built the way the lighting tests build theirs:
    /// these rows are about the notifications, and a section that existed here would only add places for the
    /// build to be wrong.</summary>
    private static MainViewModel Dashboard(NotificationCenter notifications)
    {
        var d = new FakeDevice();
        var fan = new FanSettings(false, Fan.DefaultDuties(), 70);
        return new MainViewModel(d, new UiActions(
            new ProfileActions(_ => { }, TurboToggles: false, _ => { }, _ => ProfileTraits.Unknown),
            new FanSection(new FanAxisState(FanMode.Auto, fan, fan),
                           (_, _, _) => { }, (_, _, _) => { }, _ => Task.CompletedTask),
            new GpuSection(new GpuAxisState(0, 0), (_, _) => { }),
            new CpuSection([], null, _ => { }),
            new CoSection([], [], _ => { }),
            new BatterySection(d.Battery, null, null, null),
            new OptionsSection([], [], [], TurboToggles: false, _ => { }, _ => { }, _ => { },
                               AppLanguage.System, _ => { })), lighting: null, notifications);
    }
}
