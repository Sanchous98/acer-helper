using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IFanControl"/> that records every write, so a test can assert not just WHAT was
/// applied but HOW MANY times (the service must not re-drive a fan whose duty is inside the deadband, and a
/// mode switch must not push two writes where one suffices).
/// </summary>
public sealed class FakeFanControl : IFanControl
{
    public FanCapability Capability { get; set; } = new(HasMax: true, HasCustom: true, HasGpuFan: true);
    public string? LastError { get; set; }

    public bool SetModeResult { get; set; } = true;
    public bool SetCustomSpeedsResult { get; set; } = true;

    /// <summary>Every mode handed to <see cref="SetMode"/>, in order, including refused ones.</summary>
    public List<FanMode> ModeCalls { get; } = [];

    /// <summary>Every (cpu, gpu) duty% pair handed to <see cref="SetCustomSpeeds"/>, in order.</summary>
    public List<(byte Cpu, byte Gpu)> SpeedCalls { get; } = [];

    public bool SetMode(FanMode mode)
    {
        ModeCalls.Add(mode);
        return SetModeResult;
    }

    public bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent)
    {
        SpeedCalls.Add((cpuPercent, gpuPercent));
        return SetCustomSpeedsResult;
    }
}

/// <summary>Hand-written telemetry ports: no EC, no ACPI, just the snapshot the test hands in.</summary>
public sealed class FakeSensors : ISensors
{
    public SensorSnapshot Snapshot { get; set; } = new();
    public int ReadCount { get; private set; }

    public SensorSnapshot Read()
    {
        ReadCount++;
        return Snapshot;
    }
}

/// <summary>Hand-written battery telemetry: no EC, no ACPI, just the snapshot the test hands in — with its
/// reads counted, so "the battery card read the hardware" is an assertion rather than an implementation
/// detail. This used to be a port (<c>FakeBatteryInfo</c>); telemetry is the battery object's own op now
/// (Domain/Battery.cs), so a test wires it with <c>Battery.Telemetry = telemetry.Read</c>.</summary>
public sealed class FakeBatteryTelemetry
{
    public BatteryInfoSnapshot Snapshot { get; set; } = new();
    public int ReadCount { get; private set; }

    public BatteryInfoSnapshot Read()
    {
        ReadCount++;
        return Snapshot;
    }
}

/// <summary>Hand-written <see cref="IRgbDevice"/>. Zones are empty by default: the per-mode lighting
/// presets are keyed off the profile, not off any zone, so most tests need no zones at all.</summary>
public sealed class FakeRgbDevice : IRgbDevice
{
    public IReadOnlyList<RgbZone> Zones { get; set; } = [];
    public string? ProfileFollowKey { get; set; }

    public bool SetProfileFlashResult { get; set; }
    public bool BlankResult { get; set; }

    public List<AccentColor> FlashCalls { get; } = [];
    public int BlankCalls { get; private set; }

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

    /// <summary>A minimal zone: one region, no effects and no brightness read, for tests that need an RGB
    /// surface without caring what it can render.</summary>
    public static RgbZone Zone(string name, bool canFollowProfile = false) =>
        new(name, 1, [], (_, _, _, _, _) => true, canFollowProfile: canFollowProfile);
}
