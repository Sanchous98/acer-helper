using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// A LIGHTING CONTROLLER THAT IS NOT ANSWERING MUST NOT DELETE THE LIGHTING SECTION.
///
/// The owner's report: «пропал экран Подсветки вообще». The ENE controller on that machine had been left wedged
/// by a failed firmware handshake (its ACPI parent in Code 10, <c>STATUS_DEVICE_POWER_FAILURE</c>), and the app's
/// chain of consequences was: <c>EneHidController</c>'s constructor returned early when its probe failed → no
/// zones → <c>AcerDevice.WireRgb</c> adopted no lighting → <c>AppController.BuildUi</c> built no
/// <c>LightingViewModel</c> → the page did not exist, and nothing could bring it back but restarting the app.
///
/// The fix is stated in two halves, and both are pinned here:
///  * the ZONES come from the model (<see cref="EneHidController"/>'s constructor), so the section is built and
///    keeps showing the user's stored values even while the transport is silent — the assertion is that panels
///    exist on an unreachable device;
///  * the DEVICE says so (<see cref="IRgbDevice.Reachable"/>, <see cref="LightingViewModel.ControllerUnavailable"/>),
///    so the section is honest instead of silently doing nothing — and it corrects itself, because the answer is
///    the last write's outcome rather than a fact fixed at startup.
///
/// The behavioural half is driven with fakes (a real <see cref="EneHidController"/> would touch HidSharp and could
/// write to the test machine's own keyboard). The constructor's early return is therefore guarded at the source,
/// in the style <c>LightingReapplyFlashTests</c> already uses for absences that cannot be called.
/// </summary>
public class LightingControllerUnavailableTests
{
    private const string ZoneName = "Keyboard";

    // ---------------------------------------------------------------- the section survives a silent transport

    /// <summary>THE REGRESSION ITSELF. An unreachable device still builds its panels — the section exists, the
    /// stored values are shown, and only the note is added. Before the fix this device produced no zones, so
    /// there was no section at all and the user had no way to see that the settings were still there.</summary>
    [Fact]
    public void TheSectionIsStillBuiltWhenTheControllerDoesNotAnswer()
    {
        var vm = Section(reachable: false);

        Assert.Single(vm.Panels);
        Assert.True(vm.HasZones);
        Assert.True(vm.ControllerUnavailable);
    }

    /// <summary>...and it is silent on a device whose transport does answer. The flag must be a statement about
    /// the hardware, not a property of the section, or every machine would be told its lighting is broken.</summary>
    [Fact]
    public void AnAnsweringControllerShowsNoNote()
    {
        var vm = Section(reachable: true);

        Assert.Single(vm.Panels);
        Assert.False(vm.ControllerUnavailable);
    }

    /// <summary>THE NOTE IS CORRECTED BY USE, in both directions. <see cref="IRgbDevice.Reachable"/> is what the
    /// writer thread recorded on its last write, so a controller that comes back (the app's own re-open succeeds)
    /// stops being reported as broken, and one that dies starts being reported — with no timer, no hardware read,
    /// and no assumption frozen at startup. The refresh pass is the caller
    /// (<see cref="MainViewModel.SyncLightingController"/>).</summary>
    [Fact]
    public void TheNoteFollowsTheTransport_InBothDirections()
    {
        var controller = new FakeRgbController { Zones = [Zone()], Reachable = false };
        var vm = Section(controller);

        Assert.True(vm.ControllerUnavailable);

        controller.Reachable = true;                 // a write landed
        vm.SyncControllerState();
        Assert.False(vm.ControllerUnavailable);

        controller.Reachable = false;                // ...and then it stopped answering again
        vm.SyncControllerState();
        Assert.True(vm.ControllerUnavailable);
    }

    /// <summary>A plain (non-RGB) backlight has no RGB transport to warn about: its device has no zones, so the
    /// section is the brightness slider alone and the note would be a statement about something the machine does
    /// not have. Gates on <c>HasZones</c>, not on the device's answer.</summary>
    [Fact]
    public void APlainBacklightDeviceNeverShowsTheNote()
    {
        var port = new FakeKeyboardBrightness();
        var vm = new LightingViewModel(
            new RgbDevice(new FakeRgbController { Reachable = false }),
            FakeLightZones.Empty(), followsProfile: false, saveFollowsProfile: _ => { },
            backlight: port, applyBacklight: l => port.Set(l));

        Assert.Empty(vm.Panels);
        Assert.NotNull(vm.Backlight);
        Assert.False(vm.ControllerUnavailable);
    }

    // ---------------------------------------------------------------- the aggregate

    /// <summary>One silent transport is enough to make some of the aggregated zones dead, and the UI has no way
    /// to know which — so the device reports unreachable if ANY of its controllers is. Vacuously true with no
    /// controllers (a machine with no RGB at all).</summary>
    [Fact]
    public void TheDeviceIsUnreachableWhenAnyTransportIsSilent()
    {
        using var allAnswering = new RgbDevice(
            new FakeRgbController { Reachable = true },
            new FakeRgbController { Reachable = true });
        Assert.True(allAnswering.Reachable);

        using var oneSilent = new RgbDevice(
            new FakeRgbController { Reachable = true },
            new FakeRgbController { Reachable = false });
        Assert.False(oneSilent.Reachable);

        using var none = new RgbDevice();
        Assert.True(none.Reachable);
    }

    // ---------------------------------------------------------------- the source-side half

    /// <summary>The ENE constructor must PROBE, not obey: the probe's answer is recorded (it is what
    /// <c>Reachable</c> reports at startup) but it cannot skip the zones, because that is what made the section
    /// disappear. MUTATION THAT REDDENS IT: restoring <c>if (!OpenTransport()) return;</c>.</summary>
    [Fact]
    public void TheEneConstructorCannotSkipTheZonesWhenTheProbeFails()
    {
        var source = Source("Infrastructure/Vendors/Acer/EneHidController.cs");
        var constructor = Between(source, "public EneHidController(", "_zones.Add");

        Assert.Contains("OpenTransport()", constructor);   // the probe is still made...
        Assert.DoesNotContain("return;", constructor);     // ...and it cannot end the construction
    }

    /// <summary>The Windows transport must re-enumerate when the device was not there: that async re-open is what
    /// brings the lighting back WITHOUT an app restart (the old code returned false forever once the initial
    /// <c>_device</c> was null). MUTATION THAT REDDENS IT: restoring <c>if (_device == null) return false;</c>.</summary>
    [Fact]
    public void TheWindowsWriterReProbesForADeviceItDidNotFind()
    {
        var source = Source("Infrastructure/Vendors/Acer/EneHidController.Windows.cs");

        Assert.Contains("EnsureDevice()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_device == null) return false;", source, StringComparison.Ordinal);
    }

    /// <summary>...and the writer's OUTCOME has to reach the flag, or the note could never correct itself. One
    /// assignment in the worker, not one per transport, so both OSes answer the same question.</summary>
    [Fact]
    public void TheWriterRecordsWhetherTheReportLanded()
    {
        var source = Source("Infrastructure/Vendors/Acer/EneHidController.cs");

        Assert.Contains("_reachable = WriteFeature(report)", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static LightingViewModel Section(bool reachable)
        => Section(new FakeRgbController { Zones = [Zone()], Reachable = reachable });

    private static LightingViewModel Section(IRgbController controller)
        => new(new RgbDevice(controller), FakeLightZones.Of(ZoneName, LightZoneState.Default),
               followsProfile: false, _ => { });

    /// <summary>One static zone that applies to nothing, so no test here can reach a device.</summary>
    private static RgbZone Zone()
        => new(ZoneName, 1, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
               (_, _, _, _, _) => true);

    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path),
                    $"expected a source file at {relative} - a guard that reads nothing passes for the wrong reason");
        return File.ReadAllText(path);
    }

    /// <summary>The text between two markers, exclusive of both — the constructor's body, named by its signature
    /// and by the first thing it builds.</summary>
    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the marker {from} was not found");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"the marker {to} was not found after {from}");
        return text[(start + from.Length)..end];
    }
}
