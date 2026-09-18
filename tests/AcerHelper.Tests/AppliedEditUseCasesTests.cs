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
/// the contracts hold the doing, and a stub is therefore enough to see each decision. The wiring — that
/// <c>LaptopService.SetFanCurve</c> and its siblings reach these use cases — is the existing service tests'
/// business (<c>LaptopServicePresetTests</c>, <c>LaptopServiceCoTests</c>, <c>DeclaredSettingTests</c>), which
/// passed unedited when the bodies moved.
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

    private static readonly int[] CpuCurve = [10, 20, 30];
    private static readonly int[] GpuCurve = [40, 50, 60];

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
        new FanSettings(UseCurve: true, Curve: CpuCurve, FixedDuty: 42),
        new FanSettings(UseCurve: false, Curve: GpuCurve, FixedDuty: 84)));

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
        var points = new[] { 70, 80, 90 };

        ApplyFanCurve.Run(gpu: true, use: true, points, axis);

        var edited = Assert.Single(axis.CurveEdits);
        Assert.Equal(FanMode.Max, edited.Mode);
        Assert.True(edited.Gpu.UseCurve);
        Assert.Equal([70, 80, 90], edited.Gpu.Curve);
        Assert.Equal(84, edited.Gpu.FixedDuty);              // a curve edit does not touch a speed
        Assert.True(edited.Cpu.UseCurve);                    // ...and the other fan is untouched entirely
        Assert.Equal([10, 20, 30], edited.Cpu.Curve);
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

        ApplyFanCurve.Run(gpu: false, use: false, [1, 2, 3], axis);

        var edited = Assert.Single(axis.CurveEdits);
        Assert.False(edited.Cpu.UseCurve);
        Assert.Equal([1, 2, 3], edited.Cpu.Curve);
        Assert.True(edited.Gpu.UseCurve is false);
        Assert.Equal([40, 50, 60], edited.Gpu.Curve);
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

        ApplyFanCurve.Run(gpu: true, use: true, [1], curveAxis);
        ApplyFanSelection.Run(FanMode.Auto, 1, 2, selectionAxis);

        Assert.Empty(curveAxis.SelectionEdits);
        Assert.Empty(selectionAxis.CurveEdits);
    }

    /// <summary>The curve array is passed through VERBATIM — the same instance the caller handed over, not a copy.
    /// Nothing between the UI and the stored graph has ever validated its length or its contents, and the fan model
    /// is the thing that tolerates a null, short or over-long one, so a copy here would be a rule nobody asked for
    /// — and would quietly change the array the preset holds.
    ///
    /// MUTATION THAT REDDENS IT: <c>Curve = [.. points]</c> instead of <c>Curve = points</c>.</summary>
    [Fact]
    public void ACurveEdit_HandsTheCallersArrayOver_NotACopy()
    {
        var axis = ArrangedAxis();
        var points = new[] { 70, 80, 90 };

        ApplyFanCurve.Run(gpu: true, use: true, points, axis);

        Assert.Same(points, Assert.Single(axis.CurveEdits).Gpu.Curve);
        Assert.Same(CpuCurve, Assert.Single(axis.CurveEdits).Cpu.Curve);
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

        ApplyFanSelection.Run(FanMode.Auto, 55, 66, axis);

        var edited = Assert.Single(axis.SelectionEdits);
        Assert.Equal(FanMode.Auto, edited.Mode);
        Assert.Equal(55, edited.Cpu.FixedDuty);
        Assert.Equal(66, edited.Gpu.FixedDuty);
        Assert.True(edited.Cpu.UseCurve);
        Assert.False(edited.Gpu.UseCurve);
        Assert.Same(CpuCurve, edited.Cpu.Curve);
        Assert.Same(GpuCurve, edited.Gpu.Curve);
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

        ApplyFanSelection.Run(FanMode.Max, 55, 66, axis);

        Assert.Equal(42, axis.Stored().Cpu.FixedDuty);   // the value the edit was built on
        Assert.Equal(FanMode.Max, Assert.Single(axis.SelectionEdits).Mode);
    }

    // ------------------------------------------------------------------ the per-mode value edits

    private sealed class StubGpuOffsets : IGpuOffsetsTarget
    {
        public (bool ok, string? error) Result { get; set; } = (true, null);
        public List<string> Calls { get; } = [];
        public GpuAxisState? Remembered { get; private set; }
        public GpuAxisState? Written { get; private set; }

        public void Store(GpuAxisState state) { Calls.Add("store"); Remembered = state; }
        public (bool ok, string? error) Apply(GpuAxisState state) { Calls.Add("apply"); Written = state; return Result; }
    }

    /// <summary>The GPU offsets are REMEMBERED BEFORE THEY ARE WRITTEN, and the caller is told what the write said
    /// rather than what the store did. The order is the reason a refused write still leaves the user's setting in
    /// the file — and it is the asymmetry the co-axis deliberately does not share (a refused store there stops the
    /// write instead).
    ///
    /// MUTATION THAT REDDENS IT: <c>Apply</c> before <c>Store</c> in <c>ApplyGpuOffsets.Run</c>, or returning
    /// <c>(true, null)</c> instead of the write's own answer.</summary>
    [Fact]
    public void GpuOffsets_AreRememberedBeforeTheyAreWritten_AndTheWriteIsWhatIsReported()
    {
        var target = new StubGpuOffsets { Result = (false, "no dGPU") };
        var pair = new GpuAxisState(-150, 800);

        var r = ApplyGpuOffsets.Run(pair, target);

        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Equal(pair, target.Remembered);
        Assert.Equal(pair, target.Written);
        Assert.Equal((false, "no dGPU"), r);
    }

    private sealed class StubCpuPowerOverlay : ICpuPowerOverlayTarget
    {
        public (bool ok, string? error) Result { get; set; } = (true, null);
        public List<string> Calls { get; } = [];
        public string? Remembered { get; private set; }
        public string? Written { get; private set; }

        public void Store(string id) { Calls.Add("store"); Remembered = id; }
        public (bool ok, string? error) Apply(string id) { Calls.Add("apply"); Written = id; return Result; }
    }

    /// <summary>The same rule on the overlay axis, which is a string rather than a pair and whose stored entry is
    /// what the RE-APPLY reads on the next mode switch — so an overlay that was never remembered is one this app
    /// will never put back, while one that was remembered and refused is one it will try again at the next boot.
    ///
    /// MUTATION THAT REDDENS IT: <c>Store</c> after <c>Apply</c> in <c>ApplyCpuPowerOverlay.Run</c>.</summary>
    [Fact]
    public void TheCpuPowerOverlay_IsRememberedBeforeItIsWritten()
    {
        var target = new StubCpuPowerOverlay { Result = (false, "overlay not present") };

        var r = ApplyCpuPowerOverlay.Run("best-performance", target);

        Assert.Equal(["store", "apply"], target.Calls);
        Assert.Equal("best-performance", target.Remembered);
        Assert.Equal("best-performance", target.Written);
        Assert.Equal((false, "overlay not present"), r);
    }

    private sealed class StubUndervolt : IUndervoltTarget
    {
        /// <summary>What the axis remembers for a given edit — the clamp and the rails fork are the
        /// implementation's, so a test controls them from here. <c>null</c> is "this axis would not remember
        /// it".</summary>
        public Func<IReadOnlyList<int>, IReadOnlyList<int>?> Remembered { get; set; } = counts => [.. counts];
        public (bool ok, string? error) Result { get; set; } = (true, null);
        public List<string> Calls { get; } = [];
        public IReadOnlyList<int>? RememberedFrom { get; private set; }
        public IReadOnlyList<int>? Written { get; private set; }

        public IReadOnlyList<int>? Store(IReadOnlyList<int> counts)
        {
            Calls.Add("store"); RememberedFrom = counts; return Remembered(counts);
        }

        public (bool ok, string? error) Apply(IReadOnlyList<int> counts)
        {
            Calls.Add("apply"); Written = counts; return Result;
        }
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

        var r = ApplyUndervolt.Run([], target);

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

        var r = ApplyUndervolt.Run([-50, -60], target);

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

        var r = ApplyUndervolt.Run([-5], target);

        Assert.Equal((false, (string?)null), r);
        Assert.Equal(["store"], target.Calls);
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

        ApplyDeclaredSetting.Run(Declared(), "1", target);

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

        var ex = Assert.Throws<SettingNotAppliedException>(() => ApplyDeclaredSetting.Run(Declared(), "1", target));

        Assert.Equal("FnLock", ex.Key);
        Assert.Equal("Access Denied", ex.Reason);
        Assert.Equal(["write"], target.Calls);
    }
}
