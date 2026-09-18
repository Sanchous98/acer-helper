using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A machine built the way a backend builds one — <see cref="Device"/> with nothing attached until a test asks
/// for it. EVERY port is a settable property defaulting to <c>null</c> = "this machine does not have that
/// feature", so a test declares exactly the hardware its scenario needs and nothing else:
///
///     var f = new LaptopServiceFixture(declare: d => d.Declare("lcd_override", new FakeFlagPort()));
///     f.Device.PowerProfiles = new FakePowerProfiles(profiles);
///     f.Device.FanControl    = new FakeFanControl();
///
/// Ports are read lazily by <c>LaptopService</c> on each call, so assigning them after the service is
/// constructed is correct. <see cref="Disposed"/> records the teardown call: nothing here owns a handle, and no
/// test may touch real hardware.
///
/// IT IS A SUBCLASS AND NOT THE REAL BACKEND, which matters for the same reason it always did: the generic
/// backend's constructor PROBES the OS (<c>GenericBattery.TryCreate</c>, <c>InitPlatform</c>), so a test that
/// built one would read real hardware from a unit test. This class fills the machine's slots by hand instead,
/// and adds the one thing a fake backend needs beyond that — a PUBLIC <see cref="Declare"/>, where a real
/// backend calls the protected one from inside its own construction.
///
/// <see cref="Battery"/> is the one slot that is NOT a nullable port, because it is not a port: the battery is
/// the domain object that declares its own properties one by one (Domain/Battery.cs), so a test arranges it
/// through that object — <c>Battery.ChargeLimit = new FakeFlagPort().AsBatteryToggle()</c>.
///
/// THE DECLARED SETTINGS ARE THE SAME KIND OF THING, one step further out: <see cref="Declare"/> is this fake's
/// probe — what a real backend's <c>InitVendor</c> does with its own key — and the list it fills is read off the
/// machine when the service is built, which hands it to the settings MODEL that holds and switches it
/// (Infrastructure/Composition/Settings.cs). An empty machine is the canonical "this machine has nothing".
///
/// <see cref="Declare"/> IS ORDER-SENSITIVE, unlike the ports above: the model copies the set when it is
/// CONSTRUCTED, so every declaration has to be made before the fixture builds the service (the fixture's
/// <c>declare</c> argument is where they go). A declaration made later changes this list and nothing else.
/// </summary>
public sealed class FakeDevice : Device
{
    /// <summary>Declare an on/off setting, as a backend does: the key is the backend's own name for it and is
    /// what the UI's label table is looked up with, so a test that asserts on a row's label must use the key the
    /// real backend uses (Acer's <c>lcd_override</c>, Dell's <c>FnLock</c>).</summary>
    public FlagSetting Declare(string key, IFlagPort port, bool readbackVerifiesWrite = true)
    {
        var setting = new FlagSetting { Key = key, Port = port, ReadbackVerifiesWrite = readbackVerifiesWrite };
        Declare(setting);
        return setting;
    }

    /// <summary>Declare a pick-one setting, as a backend does.</summary>
    public ChoiceSetting Declare(string key, IChoicePort port)
    {
        var setting = new ChoiceSetting { Key = key, Port = port };
        Declare(setting);
        return setting;
    }

    public bool Disposed { get; private set; }

    /// <summary>Records the teardown instead of performing one — see the class note above: nothing here owns a
    /// handle, and the assertion that cares is about the CALL (<c>LaptopService.Dispose</c> reaching the
    /// machine), not about anything being released. An OVERRIDE and not a <c>new</c> member: the caller reaches
    /// this through a <see cref="Device"/>-typed field, and hiding the base would leave <see cref="Disposed"/>
    /// false however the service called it.</summary>
    public override void Dispose() => Disposed = true;
}
