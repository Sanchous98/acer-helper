using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What writing one of the battery's own controls needs from whoever can reach the transport — the
/// contract for the two charging rows that set the battery's properties («лимит/калибровка», «режим заряда»),
/// declared here and implemented by Infrastructure/Composition/LaptopService.Toggles.cs.
///
/// THE PROPERTY ITSELF CROSSES, and it is because it already is Domain state: <see cref="BatteryToggle"/> and
/// <see cref="BatteryChoice"/> are records that carry their own read and write ops (Domain/Battery.cs), so
/// Application can name them and the use case needs nothing else to build one. What the contract adds is the one
/// rule that is the LAYER's and not the record's: A THROWING WRITE IS A FAILED WRITE. The op answers both halves
/// of its own outcome, but a port that throws assigns nothing, and the app has never let that crash a refresh
/// pass — so the catch lives here, beside the sibling <c>Attempt</c> the service keeps for its ports.</summary>
public interface IBatteryControlTarget
{
    /// <summary>Write a battery on/off property and report the write: false with the transport's own reason when
    /// it refused, false with no reason when it threw (a throw assigns nothing, so there is none to read).</summary>
    (bool ok, string? error) Toggle(BatteryToggle toggle, bool on);

    /// <summary>The same for the battery's named-mode property: the value is the firmware's own opaque id.</summary>
    (bool ok, string? error) Choice(BatteryChoice choice, string id);
}

/// <summary>One battery on/off property as a use case: hand the value to the property's own write and pass its
/// answer back untouched. The rule it states is the one the contract exists for — a refusal and a throw are the
/// same <c>false</c>, and only the refusal carries words.</summary>
public sealed class SetBatteryToggle(IBatteryControlTarget target)
{
    public (bool ok, string? error) Run(BatteryToggle toggle, bool on) => target.Toggle(toggle, on);
}

/// <summary>One battery named-mode property as a use case, on the same terms as
/// <see cref="SetBatteryToggle"/>.</summary>
public sealed class SetBatteryChoice(IBatteryControlTarget target)
{
    public (bool ok, string? error) Run(BatteryChoice choice, string id) => target.Choice(choice, id);
}

/// <summary>What writing the plain (non-RGB) keyboard backlight needs from whoever owns the port — implemented by
/// Infrastructure/Composition/LaptopService.Toggles.cs. The level is the Domain's vocabulary already (an
/// <see cref="int"/> the port clamps to its own range), so nothing is converted; the contract is about the missing
/// port and the throw rule, both of which are the same as every other port write in this layer.</summary>
public interface IKeyboardBrightnessTarget
{
    /// <summary>Set the backlight to <paramref name="level"/> and report the write: false with the port's own
    /// reason when it refused, false with no reason when this machine has no backlight port at all or the port
    /// threw (a throw leaves the port's <c>LastError</c> holding an EARLIER call's words, and reporting those is
    /// the failure docs/open-decisions.md §2 exists to remove).</summary>
    (bool ok, string? error) Set(int level);
}

/// <summary>The plain keyboard backlight as a use case: write the level the slider picked and report the write.
/// Thin by design — the lighting row's own docstring records why its write verifies by read-back elsewhere — and
/// it exists so the Lighting view-model names the action rather than the service.</summary>
public sealed class SetKeyboardBrightness(IKeyboardBrightnessTarget target)
{
    public (bool ok, string? error) Run(int level) => target.Set(level);
}

/// <summary>What the autostart row needs from whoever owns the OS entry — implemented by
/// Infrastructure/Composition/LaptopService.Toggles.cs. One member, because the row only ever turns it on or off;
/// the healing of a stale entry is a separate startup act (<c>EnsureCurrent</c>) and deliberately not here.</summary>
public interface IAutostartTarget
{
    /// <summary>Register or remove the run-at-logon entry and report whether it took. A machine with no autostart
    /// port reports false, exactly as the row's read does when the property is absent.</summary>
    bool Set(bool on);
}

/// <summary>The autostart row as a use case: set the OS entry and pass the result back. The row's own
/// <c>Prime</c>/read half stays a port read, because it is a READ (see OptionsViewModel) and not an action.</summary>
public sealed class SetAutostart(IAutostartTarget target)
{
    public bool Run(bool on) => target.Set(on);
}

/// <summary>What setting the blue-light level needs from whoever owns the level in the graph and the apply
/// schedule — implemented by Infrastructure/Composition/LaptopService.Toggles.cs. TWO MEMBERS, because the level
/// is remembered and the hardware write is scheduled, and the two must not be one call: the recording must happen
/// on the caller's thread (it is what <c>ApplyStartupState</c> re-applies next run), while the write is a slow,
/// verified compositor transaction that must run on no caller's thread.</summary>
public interface IBlueLightTarget
{
    /// <summary>Record the level in the graph under the lock and persist it. Synchronous, and deliberately first —
    /// a level the user picked is remembered even if the process dies before the write lands.</summary>
    void Remember(int level);

    /// <summary>Hand the hardware write for <paramref name="level"/> to the serialized schedule.
    /// <paramref name="onApplied"/> is answered only for the newest level asked for, on the schedule's own thread;
    /// a superseded one reports nothing.</summary>
    void Schedule(int level, Action<bool>? onApplied);
}

/// <summary>The blue-light level as a use case: RECORD IT, THEN HAND THE WRITE OFF. The order is the whole
/// decision, and it is the one that keeps the app honest about a level it did not get to finish: the recorded
/// value is what the next run re-applies, so a level the user picked survives a death between the click and the
/// write. The write is not under the graph lock and not on the caller's thread.</summary>
public sealed class SetBlueLight(IBlueLightTarget target)
{
    public void Run(int level, Action<bool>? onApplied = null)
    {
        target.Remember(level);
        target.Schedule(level, onApplied);
    }
}
