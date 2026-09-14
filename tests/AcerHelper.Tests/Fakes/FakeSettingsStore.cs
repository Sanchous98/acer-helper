using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ISettingsStore"/>. Holds ONE <see cref="Settings"/> instance and hands that
/// same reference back from every <see cref="Load"/> — exactly what the real store does for a session —
/// so a test can assert on the live graph the service mutates, and count the <see cref="Save"/> calls to
/// distinguish "changed something" from "changed nothing" (a per-mode preset write must persist; a
/// read that only builds a default must not).
/// </summary>
public sealed class FakeSettingsStore(Settings? settings = null) : ISettingsStore
{
    /// <summary>The live instance the service holds. Assert against this.</summary>
    public Settings Settings { get; } = settings ?? new Settings();

    public int LoadCount { get; private set; }

    /// <summary>Number of <see cref="Save"/> calls. The cheapest way to pin "this read did not mutate".</summary>
    public int SaveCount { get; private set; }

    /// <summary>The instance passed to the most recent <see cref="Save"/>, or null if never called.</summary>
    public Settings? LastSaved { get; private set; }

    public Settings Load()
    {
        LoadCount++;
        return Settings;
    }

    public void Save(Settings settings)
    {
        SaveCount++;
        LastSaved = settings;
    }
}
