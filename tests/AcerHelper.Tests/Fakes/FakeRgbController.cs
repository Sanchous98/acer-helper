using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;

namespace AcerHelper.Tests.Fakes;

/// <summary>Hand-written <see cref="IRgbController"/> — the transport brick <see cref="RgbDevice"/> aggregates.
/// Every capability defaults to the port's own default (unsupported / no key), so a test declares only what the
/// controller under test is supposed to answer.
///
/// <see cref="FlashCalls"/> and <see cref="BlankCalls"/> record the CALLS, not just the results, and that is the
/// point: <c>RgbDevice</c> folds the controllers with a bitwise <c>|</c>, so a controller that is asked and
/// declines looks EXACTLY like one that was never asked as far as the return value goes — only the call record
/// can tell them apart.</summary>
public sealed class FakeRgbController : IRgbController
{
    public IReadOnlyList<RgbZone> Zones { get; set; } = [];
    public string? ProfileFollowKey { get; set; }

    public bool SetProfileFlashResult { get; set; }
    public bool BlankResult { get; set; }
    public bool ThrowOnDispose { get; set; }

    public List<AccentColor> FlashCalls { get; } = [];
    public int BlankCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public bool SetProfileFlash(AccentColor color)
    {
        FlashCalls.Add(color);
        return SetProfileFlashResult;
    }

    public bool Blank()
    {
        BlankCalls++;
        return BlankResult;
    }

    public void Dispose()
    {
        DisposeCalls++;
        if (ThrowOnDispose) throw new InvalidOperationException("this controller's teardown fails");
    }

    /// <summary>A single-zone controller, for tests that only care how many zones it contributes.</summary>
    public static FakeRgbController WithZone(string name) => new() { Zones = [Zone(name)] };

    /// <summary>A minimal zone — no sub-zones, one effect, so it is a plain region to concatenate.
    /// (An effect-less zone is fine for the concatenation itself: <c>RgbDevice</c> takes whatever the controller
    /// advertises without inspecting it. The lighting SECTION is stricter — <c>LightingViewModel</c> builds a
    /// panel only for a zone with effects — so a test about panels wants a zone with one.)</summary>
    public static RgbZone Zone(string name, bool canFollowProfile = false) =>
        new(name, 1, [], (_, _, _, _, _) => true, canFollowProfile: canFollowProfile);
}
