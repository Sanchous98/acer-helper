using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The family of APPLIED EDITS as use cases, each pinned through its own contract with a STUB — no
/// <c>LaptopService</c>, no settings graph, no port.
///
/// WHY A STUB RATHER THAN THE SERVICE, and it is the reason the family was split this way at all. Every rule below
/// used to be the first lines of a method body on <c>LaptopService</c>, where the only way to observe it was to
/// build a machine, arrange a store and read the graph back — which is how a rule that is about the EDIT ends up
/// being asserted through three layers that have nothing to do with it. The use cases hold the decisions (which
/// half of a fan an edit names, what survives it, what is remembered before it is written, what a refusal does),
/// the contracts hold the doing, and a stub is therefore enough to see each decision. The wiring — that the
/// use cases reach the service's own targets (<see cref="IFanAxisTarget"/> and its siblings) — is the existing
/// service tests' business (<c>LaptopServicePresetTests</c>, <c>LaptopServiceCoTests</c>,
/// <c>DeclaredSettingTests</c>), which passed unedited when the bodies moved.
///
/// THE STUBS RECORD WHAT THEY WERE ASKED, in order, because the order is load-bearing on three of these axes: an
/// edit that is written before it is remembered reports an undo the app did not make, and an edit that is
/// remembered before a refusal has already lied to the file.
///
/// WHAT A STUB CANNOT SEE, stated here rather than left implied: the atomicity of a fan edit (the stored write,
/// the deadband clear and the EC call are one hold of the graph lock) and the state of that graph after a real
/// write are both the implementation's, and neither is observable from this side. Those are pinned by the service
/// tests and by <c>docs/open-decisions.md</c> §3's recorded holds.
/// </summary>
public class AppliedEditUseCasesTests
{
    // ------------------------------------------------------------------ fans: the curve edit

    // A real curve is exactly one duty% per anchor (Domain/Fan.cs enforces it at construction), so these are
    // five entries and not three: a shorter array is not a curve the model admits, and a test fixture that used
    // one would be asserting a state that cannot exist.
    private static readonly int[] CpuCurve = [10, 20, 30, 40, 50];
    private static readonly int[] GpuCurve = [40, 50, 60, 70, 80];

    /// <summary>A fan axis that hands back one arranged state and records every edit it is asked to make. The two
    /// edit kinds are recorded SEPARATELY, so "this edit reached the axis as a curve edit" is an assertion rather
    /// than a naming convention.</summary>
    private sealed class StubFanAxis(FanAxisState stored) : IFanAxisTarget
    {
        public List<FanAxisState> CurveEdits { get; } = [];
        public List<FanAxisState> SelectionEdits { get; } = [];

        public FanAxisState Stored() => stored;
        public void ReplaceCurve(FanAxisState state) => CurveEdits.Add(state);
        public void ReplaceSelection(FanAxisState state) => SelectionEdits.Add(state);
    }

    /// <summary>Both fans configured DIFFERENTLY — a curve on the CPU and none on the GPU, different curves and
    /// different fixed speeds — so an assertion that a half survived cannot pass because the two halves happened
    /// to be equal.</summary>
    private static StubFanAxis ArrangedAxis() => new(new FanAxisState(FanMode.Max,
        new FanSettings(useCurve: true, curve: CpuCurve, fixedDuty: 42),
        new FanSettings(useCurve: false, curve: GpuCurve, fixedDuty: 84)));

    /// <summary>A curve edit names ONE fan: the GPU half takes the new curve and switch, and the CPU half — its
    /// curve, its switch and its fixed speed — is the stored one, field for field. The mode is carried too, because
    /// a fan edit is not a mode edit.
    ///
    /// MUTATION THAT REDDENS IT: editing the CPU half when <c>gpu</c> is true (a swap of the two halves, which is
    /// exactly the mistake the stored preset's layout invites — its two halves are named Cpu and Gpu and nothing in
    /// Domain/AxisState.cs repeats that order).</summary>
    [Fact]
    public void ACurveEdit_NamesOneFanAndCarriesTheRestOfTheModeOver()
    {
        var axis = ArrangedAxis();
        var points = new[] { 70, 80, 90, 95, 100 };

        new ApplyFanCurve(axis).Run(gpu: true, use: true, points);

        var edited = Assert.Single(axis.CurveEdits);
        Assert.Equal(FanMode.Max, edited.Mode);
        Assert.True(edited.Gpu.UseCurve);
        Assert.Equal([70, 80, 90, 95, 100], edited.Gpu.Curve);
        Assert.Equal(84, edited.Gpu.FixedDuty);              // a curve edit does not touch a speed
        Assert.True(edited.Cpu.UseCurve);                    // ...and the other fan is untouched entirely
        Assert.Equal([10, 20, 30, 40, 50], edited.Cpu.Curve);
        Assert.Equal(42, edited.Cpu.FixedDuty);
    }

    /// <summary>The other direction, so the pair above cannot be satisfied by an edit that always writes the same
    /// half.
    ///
    /// MUTATION THAT REDDENS IT: dropping the <c>gpu</c> parameter's branch and always editing the GPU half — the
    /// test above stays green under that mutation, which is why both directions exist.</summary>
    [Fact]
    public void ACurveEdit_OnTheCpuFan_LeavesTheGpuHalfAlone()
    {
        var axis = ArrangedAxis();

        new ApplyFanCurve(axis).Run(gpu: false, use: false, [1, 2, 3, 4, 5]);

        var edited = Assert.Single(axis.CurveEdits);
        Assert.False(edited.Cpu.UseCurve);
        Assert.Equal([1, 2, 3, 4, 5], edited.Cpu.Curve);
        Assert.True(edited.Gpu.UseCurve is false);
        Assert.Equal([40, 50, 60, 70, 80], edited.Gpu.Curve);
        Assert.Equal(84, edited.Gpu.FixedDuty);
    }

    /// <summary>A curve edit leaves the axis through the CURVE door. The two are separate because the EC is asked
    /// something different after each — a curve edit drives the curves, a selection pushes a mode — so an edit that
    /// arrived as the wrong kind would apply the wrong thing without changing the stored state at all.
    ///
    /// MUTATION THAT REDDENS IT: calling <c>ReplaceSelection</c> at the end of <c>ApplyFanCurve.Run</c>, or
    /// <c>ReplaceCurve</c> at the end of <c>ApplyFanSelection.Run</c> (each reddens one of this pair).</summary>
    [Fact]
    public void EachEditReachesTheAxisAsItsOwnKind()
    {
        var curveAxis = ArrangedAxis();
        var selectionAxis = ArrangedAxis();

        new ApplyFanCurve(curveAxis).Run(gpu: true, use: true, [1, 2, 3, 4, 5]);
        new ApplyFanSelection(selectionAxis).Run(FanMode.Auto, 1, 2);

        Assert.Empty(curveAxis.SelectionEdits);
        Assert.Empty(selectionAxis.CurveEdits);
    }

    /// <summary>REWRITTEN — this asserted the OPPOSITE until the curve's rule moved into the model, and the two
    /// cannot both hold. It used to be "the curve array is passed through VERBATIM: <c>Assert.Same(points, …)</c>",
    /// which pinned the aliasing as a feature: the state the use case hands the axis held the CALLER's array, and
    /// (because <c>Stored</c> read the preset directly) the array the preset held as well. What replaces it is the
    /// rule that made that unrepresentable: <see cref="FanSettings"/> copies the curve it is given, so the value
    /// that leaves this use case shares no array with the caller, with the stored state it was read from, or with
    /// the stored state it is written to. The values are of course unchanged — a copy is a copy.
    ///
    /// MUTATION THAT REDDENS IT: handing the array through instead of through the constructor —
    /// <c>stored.Gpu with { Curve = points }</c> as the use case used to read — which makes <c>NotSame</c> below
    /// fail on the array AND on the stored half.</summary>
    [Fact]
    public void ACurveEdit_HandsTheModelACopy_NotTheCallersArray()
    {
        var axis = ArrangedAxis();
        var points = new[] { 70, 80, 90, 95, 100 };

        new ApplyFanCurve(axis).Run(gpu: true, use: true, points);

        var edited = Assert.Single(axis.CurveEdits);
        Assert.Equal(points, edited.Gpu.Curve);
        Assert.NotSame(points, edited.Gpu.Curve);
        Assert.Equal(CpuCurve, edited.Cpu.Curve);
        Assert.NotSame(CpuCurve, edited.Cpu.Curve);   // the untouched half too: it was copied out of the read
    }

    // ------------------------------------------------------------------ fans: the selection edit

    /// <summary>A selection changes the mode and the two fixed speeds and NOTHING else: each fan keeps its own
    /// curve switch and its own curve array. This is the rule the service's own docstring states ("per-fan curve
    /// settings are preserved") and the one thing a selection must not do.
    ///
    /// MUTATION THAT REDDENS IT: building the new state as <c>new FanAxisState(mode, default, default)</c> rather
    /// than <c>stored with { … }</c>, which is the shape a fresh write would have — the two curves are then gone and
    /// the switch on the CPU fan silently turns off. Note what does NOT redden it: swapping the two speeds, which
    /// the identity of the two halves cannot catch here and which <c>LaptopServicePresetTests</c> pins against the
    /// stored preset.</summary>
    [Fact]
    public void ASelection_ChangesTheModeAndTheSpeeds_AndCarriesEachFansCurveOver()
    {
        var axis = ArrangedAxis();

        new ApplyFanSelection(axis).Run(FanMode.Auto, 55, 66);

        var edited = Assert.Single(axis.SelectionEdits);
        Assert.Equal(FanMode.Auto, edited.Mode);
        Assert.Equal(55, edited.Cpu.FixedDuty);
        Assert.Equal(66, edited.Gpu.FixedDuty);
        Assert.True(edited.Cpu.UseCurve);
        Assert.False(edited.Gpu.UseCurve);
        Assert.Equal(CpuCurve, edited.Cpu.Curve);
        Assert.Equal(GpuCurve, edited.Gpu.Curve);
        // Carried over as VALUES, not as the caller's instances: the fixed speeds are the only fields this edit
        // means to change, and the arrays are copies the same way a curve edit's are.
        Assert.NotSame(CpuCurve, edited.Cpu.Curve);
        Assert.NotSame(GpuCurve, edited.Gpu.Curve);
    }

    /// <summary>The stored state is READ and not assumed: a selection on a mode that was never configured must start
    /// from the preset's own defaults rather than from zeroes, because the two speeds it does not name are the ones
    /// a Custom selection will actually drive the fans at.
    ///
    /// MUTATION THAT REDDENS IT: <c>target.Stored()</c> replaced by a literal <c>new FanAxisState(FanMode.Auto,
    /// default, default)</c> — the CPU fan's curve switch reads false and its speed 0 in the assertion below.</summary>
    [Fact]
    public void ASelection_StartsFromWhatTheAxisRemembers()
    {
        var axis = ArrangedAxis();

        new ApplyFanSelection(axis).Run(FanMode.Max, 55, 66);

        Assert.Equal(42, axis.Stored().Cpu.FixedDuty);   // the value the edit was built on
        Assert.Equal(FanMode.Max, Assert.Single(axis.SelectionEdits).Mode);
    }

    // ------------------------------------------------------------------ the per-mode value edits

    private sealed class StubGpuOffsets : IGpuOffsetsTarget
    {
        public bool HasPortResult { get; set; } = true;
        /// <summary>Set to make a PRESENT port refuse — the write throws this from <see cref="Apply"/>, exactly
        /// the shape the real target now has.</summary>
        public Exception? ThrowOnApply { get; set; }
        public List<string> Calls { get; } = [];
        public GpuAxisState? Remembered { get; private set; }
        public GpuAxisState? Written { get; private set; }

        public bool HasPort => HasPortResult;
        public void Store(GpuAxisState state) { Calls.Add("store"); Remembered = state; }
        public void Apply(GpuAxisState state)
        {
            Calls.Add("apply"); Written = state;
            if (ThrowOnApply is { } ex) throw ex;
        }
    }

    /// <summary>The GPU offsets are REMEMBERED BEFORE THEY ARE WRITTEN, and a present driver's refusal now
    /// THROWS <see cref="PortWriteFailedException"/> — carrying the driver's own words — rather than returning a
    /// pair. The order is the reason a refused write still leaves the user's setting in the file.
    ///
    /// MUTATION THAT REDDENS IT: <c>Apply</c> before <c>Store</c> in <c>ApplyGpuOffsets.Run</c>; or catching the
    /// throw inside the use case and returning a value, which would make the <c>Assert.Throws</c> below fail.</summary>
    [Fact]
    public void GpuOffsets_AreRememberedBeforeTheyAreWritten_AndARefusalThrows()
    {
        var target = new StubGpuOffsets { ThrowOnApply = new PortWriteFailedException("GPU offsets", "no dGPU") };
        var pair = new GpuAxisState(-150, 800);

        var ex = Assert.Throws<PortWriteFailedException>(() => new ApplyGpuOffsets(target).Run(pair));

        Assert.Equal("GPU offsets", ex.Operation);
        Assert.Equal("no dGPU", ex.Reason);
        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Equal(pair, target.Remembered);
        Assert.Equal(pair, target.Written);
    }

    /// <summary>AN ABSENT PORT IS NOT A REFUSAL: <see cref="IGpuOffsetsTarget.HasPort"/> false returns the same
    /// <c>false</c> the UI has always read, the preset is still stored (store-before-write), and
    /// <see cref="IGpuOffsetsTarget.Apply"/> is NEVER reached — so an absent machine can never be mistaken for a
    /// present driver that refused.</summary>
    [Fact]
    public void GpuOffsets_WithNoPort_ReturnFalse_StoreAndNeverWrite()
    {
        var target = new StubGpuOffsets { HasPortResult = false };

        var ok = new ApplyGpuOffsets(target).Run(new GpuAxisState(-150, 800));

        Assert.False(ok);
        Assert.Equal(["store"], target.Calls);
        Assert.Null(target.Written);
    }

    private sealed class StubCpuPowerOverlay : ICpuPowerOverlayTarget
    {
        public bool HasPortResult { get; set; } = true;
        public Exception? ThrowOnApply { get; set; }
        public List<string> Calls { get; } = [];
        public string? Remembered { get; private set; }
        public string? Written { get; private set; }

        public bool HasPort => HasPortResult;
        public void Store(string id) { Calls.Add("store"); Remembered = id; }
        public void Apply(string id)
        {
            Calls.Add("apply"); Written = id;
            if (ThrowOnApply is { } ex) throw ex;
        }
    }

    /// <summary>The same rule on the overlay axis, which is a string rather than a pair and whose stored entry is
    /// what the RE-APPLY reads on the next mode switch — so an overlay that was never remembered is one this app
    /// will never put back, while one that was remembered and refused is one it will try again at the next boot.
    /// A present port's refusal THROWS and the store has already happened when it does.
    ///
    /// MUTATION THAT REDDENS IT: <c>Store</c> after <c>Apply</c> in <c>ApplyCpuPowerOverlay.Run</c>.</summary>
    [Fact]
    public void TheCpuPowerOverlay_IsRememberedBeforeItIsWritten_AndARefusalThrows()
    {
        var target = new StubCpuPowerOverlay { ThrowOnApply = new PortWriteFailedException("CPU power overlay", "overlay not present") };

        var ex = Assert.Throws<PortWriteFailedException>(() => new ApplyCpuPowerOverlay(target).Run("best-performance"));

        Assert.Equal("overlay not present", ex.Reason);
        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Equal("best-performance", target.Remembered);
        Assert.Equal("best-performance", target.Written);
    }

    /// <summary>No CPU-power port is a capability fact: the choice is still stored and <c>false</c> comes back,
    /// and the port is never written.</summary>
    [Fact]
    public void TheCpuPowerOverlay_WithNoPort_ReturnsFalse_StoreAndNeverWrite()
    {
        var target = new StubCpuPowerOverlay { HasPortResult = false };

        Assert.False(new ApplyCpuPowerOverlay(target).Run("best-performance"));

        Assert.Equal(["store"], target.Calls);
        Assert.Null(target.Written);
    }

    private sealed class StubGpuPower : IGpuPowerTarget
    {
        public bool HasPortResult { get; set; } = true;
        public Exception? ThrowOnApply { get; set; }
        public List<string> Calls { get; } = [];
        public GpuPowerLevel? Remembered { get; private set; }
        public GpuPowerLevel? Written { get; private set; }

        public bool HasPort => HasPortResult;
        public void Store(GpuPowerLevel? level) { Calls.Add("store"); Remembered = level; }
        public void Apply(GpuPowerLevel? level)
        {
            Calls.Add("apply"); Written = level;
            if (ThrowOnApply is { } ex) throw ex;
        }
    }

    /// <summary>The GPU power level follows the SAME remember-before-write order as the offsets and the overlay,
    /// and a present port's refusal THROWS after the store has happened — so a refused pick still leaves the
    /// user's choice in the file for the next boot. NULL ("follow the profile") is a value the store records and
    /// the target is still asked to apply, because "clear the override" is a real edit; the target decides that a
    /// null has nothing to write.
    ///
    /// MUTATION THAT REDDENS IT: <c>Apply</c> before <c>Store</c> in <c>ApplyGpuPower.Run</c>.</summary>
    [Fact]
    public void TheGpuPowerLevel_IsRememberedBeforeItIsWritten_AndARefusalThrows()
    {
        var target = new StubGpuPower { ThrowOnApply = new PortWriteFailedException("GPU power level", "channel busy") };

        var ex = Assert.Throws<PortWriteFailedException>(() => new ApplyGpuPower(target).Run(GpuPowerLevel.Turbo));

        Assert.Equal("channel busy", ex.Reason);
        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Equal(GpuPowerLevel.Turbo, target.Remembered);
        Assert.Equal(GpuPowerLevel.Turbo, target.Written);
    }

    /// <summary>No envelope port is a capability fact: the choice is still stored and <c>false</c> comes back,
    /// and the port is never written — the same shape the offsets and the overlay have.</summary>
    [Fact]
    public void TheGpuPowerLevel_WithNoPort_ReturnsFalse_StoreAndNeverWrite()
    {
        var target = new StubGpuPower { HasPortResult = false };

        Assert.False(new ApplyGpuPower(target).Run(GpuPowerLevel.Balanced));

        Assert.Equal(["store"], target.Calls);
        Assert.Null(target.Written);
    }

    /// <summary>"Follow the profile" is a real edit, not a no-op: the store records the null (clearing an
    /// override), and a present port is still asked to apply it — the target is where "a null has nothing to
    /// write" lives, so this use case does not second-guess it.</summary>
    [Fact]
    public void ClearingToFollowTheProfile_IsStoredAndHandedToTheTarget()
    {
        var target = new StubGpuPower();

        Assert.True(new ApplyGpuPower(target).Run(null));

        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Null(target.Remembered);
        Assert.Null(target.Written);
    }

    private sealed class StubUndervolt : IUndervoltTarget
    {
        /// <summary>What the axis remembers for a given edit — the clamp and the rails fork are the
        /// implementation's, so a test controls them from here. <c>null</c> is "this axis would not remember
        /// it" (also the absent-port answer).</summary>
        public Func<IReadOnlyList<int>, IReadOnlyList<int>?> Remembered { get; set; } = counts => [.. counts];
        /// <summary>Set to make a PRESENT SMU refuse — the write throws this from <see cref="Apply"/>.</summary>
        public Exception? ThrowOnApply { get; set; }
        public List<string> Calls { get; } = [];
        public IReadOnlyList<int>? RememberedFrom { get; private set; }
        public IReadOnlyList<int>? Written { get; private set; }

        public IReadOnlyList<int>? Store(IReadOnlyList<int> counts)
        {
            Calls.Add("store"); RememberedFrom = counts; return Remembered(counts);
        }

        public void Apply(IReadOnlyList<int> counts)
        {
            Calls.Add("apply"); Written = counts;
            if (ThrowOnApply is { } ex) throw ex;
        }
    }

    /// <summary>A gate that is never busy and simply runs the write — the use case now owns ITS gate, so a test
    /// that means to pin the edit's rules (not the sweep exclusion, which the service tests cover) passes an open
    /// one. The exclusion itself is pinned in <c>UndervoltSweepServiceTests</c>.</summary>
    private sealed class StubGate : ITuningGate
    {
        public bool SweepActive => false;
        public (bool ok, string? error) Guard(Func<(bool ok, string? error)> write) => write();
    }

    /// <summary>An empty edit is a refusal that touches nothing: no reason, nothing remembered, no SMU traffic.
    /// It is a rule about the EDIT rather than about the CPU, which is why it is stated in the use case — the
    /// per-rail path ACCEPTS an empty list (that is how a machine with no rails is disarmed) and the two must not
    /// be confused.
    ///
    /// MUTATION THAT REDDENS IT: removing the <c>counts.Count == 0</c> guard from <c>ApplyUndervolt.Run</c>, after
    /// which the stub records both calls and the refusal is gone.</summary>
    [Fact]
    public void AnEmptyUndervoltEdit_IsRefused_AndNeitherRememberedNorWritten()
    {
        var target = new StubUndervolt();

        var r = new ApplyUndervolt(target, new StubGate()).Run([]);

        Assert.Equal((false, (string?)null), r);
        Assert.Empty(target.Calls);
    }

    /// <summary>WHAT IS WRITTEN IS WHAT WAS REMEMBERED. The axis hands the offsets back — clamped against this
    /// CPU's rails and in the shape it takes — and the use case writes those rather than the caller's numbers, so
    /// a value that was brought inside its bounds on the way in cannot leave as the unbounded one.
    ///
    /// MUTATION THAT REDDENS IT: <c>target.Apply(counts)</c> instead of <c>target.Apply(remembered)</c>; the
    /// assertion on what the SMU was given then reads the caller's <c>[-50, -60]</c> and the app claims an
    /// undervolt the hardware never got — the exact failure the clamp exists to prevent.</summary>
    [Fact]
    public void TheUndervoltWritten_IsTheOneThatWasRemembered_NotTheOneHandedOver()
    {
        var target = new StubUndervolt { Remembered = _ => [-5, -6] };

        var r = new ApplyUndervolt(target, new StubGate()).Run([-50, -60]);

        Assert.Equal([-50, -60], target.RememberedFrom);
        Assert.Equal([-5, -6], target.Written);
        Assert.True(r.ok);
    }

    /// <summary>An edit the axis would not remember is NEVER WRITTEN, and the caller reads the same
    /// <c>(false, null)</c> it has always read for a count this CPU cannot take. The counts are not
    /// clamped-and-sent anyway: a value the axis refuses to file is not one it will be asked to put back.
    ///
    /// MUTATION THAT REDDENS IT: <c>target.Store(counts) ?? counts</c> — the write then happens with the caller's
    /// numbers and the stub records <c>["store", "apply"]</c>.</summary>
    [Fact]
    public void AnUndervoltTheAxisWouldNotRemember_IsNeverWritten()
    {
        var target = new StubUndervolt { Remembered = _ => null };

        var r = new ApplyUndervolt(target, new StubGate()).Run([-5]);

        Assert.Equal((false, (string?)null), r);
        Assert.Equal(["store"], target.Calls);
    }

    /// <summary>A PRESENT SMU that refuses now THROWS <see cref="PortWriteFailedException"/>, carrying its own
    /// words, rather than returning <c>(false, error)</c>. The remember has already happened when it does, which is
    /// the store-before-write order this use case states; the exception escapes the use case and the gate (the UI's
    /// <c>SetCo</c> catches it at its boundary, the boot re-apply turns it into its non-verdict outcome).
    ///
    /// MUTATION THAT REDDENS IT: catching the exception inside <c>ApplyUndervolt.Run</c> and returning
    /// <c>(false, error)</c> — the <c>Assert.Throws</c> then fails.</summary>
    [Fact]
    public void AnUndervoltThePresentSmuRefused_Throws_AfterRemembering()
    {
        var target = new StubUndervolt { ThrowOnApply = new PortWriteFailedException("Curve Optimizer offsets", "SMU refused") };

        var ex = Assert.Throws<PortWriteFailedException>(() => new ApplyUndervolt(target, new StubGate()).Run([-12]));

        Assert.Equal("Curve Optimizer offsets", ex.Operation);
        Assert.Equal("SMU refused", ex.Reason);
        Assert.Equal(["store", "apply"], target.Calls);      // remembered first, then the write that threw
    }

    // ------------------------------------------------------------------ a declared setting

    private sealed class StubDeclaredSetting : IDeclaredSettingTarget
    {
        public Exception? ThrowOnWrite { get; set; }
        public List<string> Calls { get; } = [];
        public List<(string Key, string Value)> Recorded { get; } = [];

        public void Write(SettingDeclaration setting, string value)
        {
            Calls.Add("write");
            if (ThrowOnWrite is { } ex) throw ex;
        }

        public void Remember(SettingDeclaration setting, string value)
        {
            Calls.Add("remember");
            Recorded.Add((setting.Key, value));
        }

        public void Persist() => Calls.Add("persist");
    }

    private static FlagSetting Declared() => new() { Key = "FnLock", Port = new FakeFlagPort() };

    /// <summary>The hardware write comes FIRST and the record follows it, under the option's own key, and only then
    /// is the graph written out. The order is the guarantee rather than a belt after it: recording the attempt and
    /// correcting it afterwards would leave a value the hardware refused in the file, and the next boot would show a
    /// setting this machine is not in.
    ///
    /// MUTATION THAT REDDENS IT: moving <c>Remember</c> above <c>Write</c> in <c>ApplyDeclaredSetting.Run</c> —
    /// which is also what makes the test below fail, and that is the point of the pair.</summary>
    [Fact]
    public void ADeclaredSetting_IsWrittenBeforeItIsRemembered_AndTheFileFollowsBoth()
    {
        var target = new StubDeclaredSetting();

        new ApplyDeclaredSetting(target).Run(Declared(), "1");

        Assert.Equal(["write", "remember", "persist"], target.Calls);
        Assert.Equal([("FnLock", "1")], target.Recorded);
    }

    /// <summary>A refusal — the transport's, or "this machine does not declare it", which is the same exception
    /// from the same call — escapes to the caller and NOTHING is recorded and NOTHING is saved. The use case has no
    /// catch on purpose: the reason the exception carries is information (which setting, and the transport's own
    /// words) and the sentence the user reads is composed by the row from its own label.
    ///
    /// MUTATION THAT REDDENS IT: wrapping the write in a <c>try</c>/<c>catch</c> that carries on — the stub then
    /// records all three calls and no exception is thrown; and, independently, moving <c>Remember</c> first, which
    /// records the value the hardware refused.</summary>
    [Fact]
    public void ADeclaredSettingTheHardwareRefused_IsNeverRemembered_AndNothingIsSaved()
    {
        var target = new StubDeclaredSetting { ThrowOnWrite = new SettingNotAppliedException("FnLock", "Access Denied") };

        var ex = Assert.Throws<SettingNotAppliedException>(() => new ApplyDeclaredSetting(target).Run(Declared(), "1"));

        Assert.Equal("FnLock", ex.Key);
        Assert.Equal("Access Denied", ex.Reason);
        Assert.Equal(["write"], target.Calls);
    }
}
