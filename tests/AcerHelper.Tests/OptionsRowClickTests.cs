using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE OTHER HALF of the app-level rows' wiring. <see cref="OptionsPrimeTests"/> covers the READ half — that
/// building a row asks the hardware nothing and that its prime brings the real value — and it points the rows'
/// write delegates at the fakes those read halves use. Nothing there CLICKS a row, so which delegate a row's
/// toggle reaches was pinned by no assertion at all: transposing the clamshell and autostart delegates inside
/// <c>OptionsViewModel.TryCreate</c> left the whole suite green (measured, before this file existed).
///
/// WHAT IS PINNED HERE. Each of the four app-level rows — clamshell, autostart, Turbo-key behaviour, language —
/// is built by <see cref="OptionsViewModel.TryCreate"/> over an <see cref="OptionsSection"/> whose delegates
/// reach the real <c>LaptopService</c>, which is the shape <c>AppController.BuildUi</c> wires
/// (<c>b =&gt; _svc.SetClamshell(b)</c>, <c>b =&gt; _svc.SetAutostart(b)</c>, <c>_svc.SetTurboToggles</c>,
/// <c>_svc.SetLanguage</c>). A click on each row is then followed into the sink that row is supposed to reach:
/// the clamshell port, the autostart port, <c>Settings.TurboToggles</c>, <c>Settings.Language</c>. Every test
/// also asserts the OTHER THREE sinks were left alone, which is what makes a transposition fail rather than
/// merely a wire that goes nowhere.
///
/// WHY THE CLICKS ARE SYNCHRONOUS. Each of these four rows is built with no <c>Read</c> delegate
/// (<c>OptionsViewModel.TryCreate</c>), so a toggle applies inline through the row's own <c>OnChange</c> and a
/// pick fires the dropdown's <c>OnPick</c> directly. No prime is called here, so nothing is in flight while the
/// assertions run and no poster is needed.
///
/// WHAT THIS CANNOT REACH, named rather than implied: <c>AppController.BuildUi</c> itself, which is where the
/// delegates are handed to the section in the running app — it needs a live desktop lifetime and a refresh
/// loop, the same limit <c>LightingAdoptionTests</c> and <c>CpuPrimeTests</c> already record. What is proven is
/// that a section built the way that method builds it reaches the right sink from the right row.
/// </summary>
public class OptionsRowClickTests
{
    /// <summary>
    /// The clamshell row's switch reaches <c>SetClamshell</c> — and only it. This is the row the measured
    /// transposition moved: with <c>TryCreate</c> handing the clamshell row the autostart delegate, the write
    /// lands on <see cref="FakeAutostart"/> and this assertion is the one that notices.
    /// </summary>
    [Fact]
    public void ClickingTheClamshellRow_WritesClamshellAndNothingElse()
    {
        var s = Arrange();

        s.ClamshellRow.IsOn = true;

        Assert.Equal([true], s.Clam.SetCalls);                        // the write landed on the clamshell port
        Assert.Empty(s.Auto.SetCalls);                                // ...and not on the autostart one
        Assert.False(s.H.F.Service.TurboToggles);                     // ...nor on the Turbo-key preference
        Assert.Equal(AppLanguage.System, s.H.F.Service.Language);     // ...nor on the language preference
    }

    /// <summary>The autostart row's switch reaches <c>SetAutostart</c> — the other end of the same
    /// transposition, asserted separately so a failure names the row rather than the pair.</summary>
    [Fact]
    public void ClickingTheAutostartRow_WritesAutostartAndNothingElse()
    {
        var s = Arrange();

        s.AutostartRow.IsOn = true;

        Assert.Equal([true], s.Auto.SetCalls);
        Assert.Empty(s.Clam.SetCalls);
        Assert.False(s.H.F.Service.TurboToggles);
        Assert.Equal(AppLanguage.System, s.H.F.Service.Language);
    }

    /// <summary>The Turbo-key row persists its preference. Its delegate has the same
    /// <c>Action&lt;bool&gt;</c> signature as the two above, so it is the third member of the set a transposition
    /// can move a click between; the three assertions after the first are what say it did not move.</summary>
    [Fact]
    public void ClickingTheTurboRow_PersistsTurboTogglesAndNothingElse()
    {
        var s = Arrange();

        s.TurboRow.IsOn = true;

        Assert.True(s.H.F.Service.TurboToggles);
        Assert.Empty(s.Clam.SetCalls);
        Assert.Empty(s.Auto.SetCalls);
        Assert.Equal(AppLanguage.System, s.H.F.Service.Language);
    }

    /// <summary>
    /// The language dropdown reaches <c>SetLanguage</c>. Its delegate takes an <see cref="AppLanguage"/> and so
    /// cannot be transposed with the three bool ones by the compiler, but it CAN be wired to nothing at all —
    /// which is the mistake this pins, because the row would still move in the UI while the app kept the old
    /// preference. Index 2 is <c>AppLanguage.Russian</c> by <c>TryCreate</c>'s own list
    /// (<c>System, English, Русский</c>), and the row is found by label, so a reordering of that list shows up
    /// here as a failure rather than as a silent language drift.
    /// </summary>
    [Fact]
    public void PickingALanguage_ReachesTheServiceAndNothingElse()
    {
        var s = Arrange();

        s.LanguageRow.SelectedIndex = 2;                              // "Русский"

        Assert.Equal(AppLanguage.Russian, s.H.F.Service.Language);
        Assert.Empty(s.Clam.SetCalls);
        Assert.Empty(s.Auto.SetCalls);
        Assert.False(s.H.F.Service.TurboToggles);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An Options drawer with all four app-level rows, over a device that has the clamshell, autostart
    /// and power-profile ports those rows are gated on, and with no hardware rows (this file is about the
    /// shell's rows). <c>Assert.NotNull</c> doubles as the arrangement's own control: a section that built no
    /// rows would otherwise let every assertion below pass by there being nothing to click.</summary>
    private static Shell Arrange()
    {
        var h = new OptionsAssemblerHarness();
        var clam = new FakeClamshell();
        var auto = new FakeAutostart();
        h.F.Device.Clamshell = clam;
        h.F.Device.Autostart = auto;
        h.F.Device.PowerProfiles = new FakePowerProfiles(TestProfiles.All);   // a Turbo profile -> the Turbo row

        var vm = OptionsViewModel.TryCreate(h.F.Device, Section(h), Eventually.Sync);

        Assert.NotNull(vm);
        return new Shell(h, clam, auto, vm);
    }

    /// <summary>The section exactly as <c>AppController.BuildUi</c> fills it in — every mutation delegate
    /// pointing at the service, and nothing else in it — so what a click reaches here is what a click reaches
    /// in the app. The one difference is the language delegate: the app wraps <c>_svc.SetLanguage</c> in a
    /// method that also posts a UI rebuild (which a headless test cannot pump), and that wrapper contains no
    /// mapping this file is about.</summary>
    private static OptionsSection Section(OptionsAssemblerHarness h) => new(
        HwToggles: [], HwChoices: [], ProfileChoices: [],
        TurboToggles: h.F.Service.TurboToggles, SetTurboToggles: h.F.Service.SetTurboToggles,
        SetClamshell: h.F.Service.SetClamshell, SetAutostart: b => h.F.Service.SetAutostart(b),
        Language: h.F.Service.Language, SetLanguage: h.F.Service.SetLanguage);

    /// <summary>The arranged section with the rows looked up by LABEL, the way the user meets them: each row's
    /// text is what <c>TryCreate</c> looked the label table up with, so a row is addressed by the same string
    /// the UI shows rather than by a position in the list.</summary>
    private sealed class Shell(OptionsAssemblerHarness h, FakeClamshell clam, FakeAutostart auto, OptionsViewModel vm)
    {
        public OptionsAssemblerHarness H { get; } = h;
        public FakeClamshell Clam { get; } = clam;
        public FakeAutostart Auto { get; } = auto;

        public ToggleRowViewModel ClamshellRow { get; } =
            vm.Rows.OfType<ToggleRowViewModel>().Single(r => r.Label == Loc.T(clam.Label));
        public ToggleRowViewModel AutostartRow { get; } =
            vm.Rows.OfType<ToggleRowViewModel>().Single(r => r.Label == Loc.T(auto.Label));
        public ToggleRowViewModel TurboRow { get; } =
            vm.Rows.OfType<ToggleRowViewModel>().Single(r => r.Label == Loc.T("Turbo key toggles Turbo"));
        public ChoiceRowViewModel LanguageRow { get; } =
            vm.Rows.OfType<ChoiceRowViewModel>().Single(r => r.Label == Loc.T("Language"));
    }
}
