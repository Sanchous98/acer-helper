using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// THE TURBO KEY'S SIGNATURE IS ONE THING, NOT TWO.
///
/// The key is the Acer vendor HID report <c>04 85 …</c> on both OSes: Windows matches those two bytes out of
/// RawInput on usage page 0x0088, and on Linux the same bytes arrive on the Acer HID device's hidraw node —
/// measured with the key pressed on 2026-09-21, after evdev, the journal's <c>acer_wmi</c> lines and the ACPI
/// interrupt counters had all stayed empty across the same presses:
///
/// <code>
///   /dev/hidraw8: report 04 85 ff      (08:43:11 and 08:43:15)
/// </code>
///
/// The two halves of the hotkey reader are separate files by the repo's OS split and only one is compiled per
/// TFM, so a signature written in each would be invisible drift: the key would quietly stop working on one OS.
/// The decode rows below pin the shared reading, and the wiring rows pin that BOTH halves reach for it rather
/// than spelling the bytes again.
/// </summary>
public class AcerHotkeyReportsTests
{
    /// <summary>The repository root, from the COMPILER's path: the test host's working directory is its output
    /// folder, where a relative path finds nothing.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path), $"expected a source file at {relative}");
        return File.ReadAllText(path);
    }

    /// <summary>The measured report decodes to the Turbo action — with and without the trailing byte, because the
    /// third byte is not part of the signature (the capture shows 0xff; Windows matches two bytes).</summary>
    [Theory]
    [InlineData(new byte[] { 0x04, 0x85 })]
    [InlineData(new byte[] { 0x04, 0x85, 0xff })]
    [InlineData(new byte[] { 0x04, 0x85, 0x00, 0x00, 0x00 })]
    public void TheMeasuredTurboReportDecodesToTheTurboAction(byte[] report)
        => Assert.Equal(HotkeyAction.TogglePerformance, AcerHotkeyReports.Decode(report));

    /// <summary>Everything else on that device is the power-envelope traffic, so "not a key" has to be the
    /// ordinary answer rather than an exception — including the reports that are one byte off, which is what a
    /// drifting signature would look like in the wild.</summary>
    [Theory]
    [InlineData(new byte[] { 0x04, 0x84 })]                 // one byte off
    [InlineData(new byte[] { 0x85, 0x04 })]                 // swapped
    [InlineData(new byte[] { 0xa0, 0x00, 0xa0 })]           // the EC feature frame the same device carries
    [InlineData(new byte[] { 0x04 })]                       // truncated: not enough bytes to be a key
    [InlineData(new byte[0])]
    public void AnythingElseDecodesToNothing(byte[] report)
        => Assert.Null(AcerHotkeyReports.Decode(report));

    /// <summary>Both halves must decode through the shared file. A half that spells the bytes itself would still
    /// work today and drift tomorrow, which is the failure this repo's guards exist for.</summary>
    [Theory]
    [InlineData("Infrastructure/Vendors/Acer/AcerHotkeys.Windows.cs")]
    [InlineData("Infrastructure/Vendors/Acer/AcerHotkeys.Linux.cs")]
    public void BothHalvesDecodeThroughTheSharedSignature(string file)
    {
        var source = Source(file);
        Assert.Contains("AcerHotkeyReports", source, StringComparison.Ordinal);
        // The signature's second byte must NOT be re-derived in either half: that literal is what a drifting
        // copy would contain, and neither file has any other use for it (checked when this row was written).
        Assert.DoesNotContain("0x85", source, StringComparison.Ordinal);
    }
}
