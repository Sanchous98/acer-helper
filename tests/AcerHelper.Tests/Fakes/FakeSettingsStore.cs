using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ISettingsStore"/>. <see cref="Load"/> builds the session's model exactly as the real
/// store builds it — <c>new Settings(declaredSettings, persisted)</c> — and keeps it in <see cref="Settings"/>,
/// which is the live graph the service mutates: so a test asserts on the model itself, and counts the
/// <see cref="Save"/> calls to distinguish "changed something" from "changed nothing" (a per-mode preset write
/// must persist; a read that only builds a default must not).
///
/// <paramref name="persisted"/> is the values a test wants to start from, standing in for the file. It is NOT the
/// model: the constructor takes its members over into the instance this fake then hands out, which is why the
/// tests that assert on "the stored preset" read it off <see cref="Settings"/> rather than off the instance they
/// passed in. The service loads once, at construction, so one session has exactly one model — as the real store
/// does.
/// </summary>
public sealed class FakeSettingsStore(Settings? persisted = null) : ISettingsStore
{
    /// <summary>The live graph the service holds — the model <see cref="Load"/> built. Before the service
    /// exists this is the empty placeholder the property starts as; the fixture builds the service in the same
    /// breath that creates this store, so nothing reads that state.</summary>
    public Settings Settings { get; private set; } = new();

    public int LoadCount { get; private set; }

    /// <summary>Number of <see cref="Save"/> calls. The cheapest way to pin "this read did not mutate".</summary>
    public int SaveCount { get; private set; }

    /// <summary>The instance passed to the most recent <see cref="Save"/>, or null if never called.</summary>
    public Settings? LastSaved { get; private set; }

    public Settings Load(IReadOnlyList<SettingDeclaration> declaredSettings)
    {
        LoadCount++;
        return Settings = new Settings(declaredSettings, persisted);
    }

    public void Save(Settings settings)
    {
        SaveCount++;
        LastSaved = settings;
    }
}
