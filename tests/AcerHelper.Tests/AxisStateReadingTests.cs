using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>What the two per-mode sections READ out of the state they are handed, asserted where the state
/// arrives: <see cref="FansViewModel"/> and <see cref="GpuViewModel"/> are loaded from a
/// <see cref="FanAxisState"/>/<see cref="GpuAxisState"/> — the DOMAIN's vocabulary, because the same value
/// reaches them from two directions (the build path's stored preset, mapped at the service, and the re-apply
/// outcome, which crosses into Application).
///
/// WHY THIS FILE EXISTS, and it is a measured gap rather than a hunch. Before it, the whole suite passed with
/// <c>FansViewModel.Load</c> reading the GPU half into the CPU slider — the mutation was run and reddened
/// nothing, because every existing test reaches these view-models through production paths that no test drives
/// (the section is only built when the machine has the capability, and <c>AppController</c> is not
/// constructible in this suite). Constructing the view-model directly is what makes the mapping observable, and
/// the mapping is the part this wave changed: a state that arrives translated, from a type no other test names.
///
/// WHAT IT DOES NOT CHECK: that the values the state holds are the ones the hardware was set to — that is
/// <c>LaptopService.AxisStateOf</c>'s reading and is pinned where the presets are (HardwareReconcilerTests,
/// LaptopServicePresetTests). The subject here is only the last hop, state → what the user sees.</summary>
public class AxisStateReadingTests
{
    /// <summary>Every read <see cref="FansViewModel"/> makes of its state, with each fan's data DISTINCT in
    /// every field: the two halves differ in the fixed speed, in the curve flag and in every curve point, so a
    /// half read into the other's place cannot land on the same value by accident. The mode is Custom, which is
    /// the one behaviour that keeps both fans' curves live.</summary>
    [Fact]
    public void TheFanSectionReadsEachFanFromItsOwnHalf()
    {
        var vm = new FansViewModel(
            new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true),
            new FanAxisState(FanMode.Custom,
                new FanSettings(UseCurve: true, Curve: [11, 22, 33, 44, 55], FixedDuty: 33),
                new FanSettings(UseCurve: false, Curve: [66, 77, 88, 99, 100], FixedDuty: 77)),
            setFan: (_, _, _) => { }, setFanCurve: (_, _, _) => { }, showCurve: _ => Task.CompletedTask);

        Assert.Equal(33, vm.Cpu);
        Assert.Equal(77, vm.Gpu);
        Assert.True(vm.CpuUseCurve);
        Assert.False(vm.GpuUseCurve);
        Assert.Equal([11, 22, 33, 44, 55], vm.CpuCurve.Select(p => (int)p.Percent));
        Assert.Equal([66, 77, 88, 99, 100], vm.GpuCurve.Select(p => (int)p.Percent));
    }

    /// <summary>The behaviour flags, over the three modes a stored preset can be in. This is the other half of
    /// the state → section mapping, and the one the mode switch is about: a mode whose preset says Max must show
    /// Max, and an unconfigured one must fall back to Auto rather than inheriting the previous mode's.</summary>
    [Theory]
    [InlineData(FanMode.Auto, false, true, false)]
    [InlineData(FanMode.Max, true, false, false)]
    [InlineData(FanMode.Custom, false, false, true)]
    public void TheFanSectionShowsTheModesBehaviour(FanMode mode, bool isMax, bool isAuto, bool isCustom)
    {
        var vm = new FansViewModel(
            new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true),
            new FanAxisState(mode, new FanSettings(false, [30, 45, 60, 80, 100], 70),
                                   new FanSettings(false, [30, 45, 60, 80, 100], 70)),
            setFan: (_, _, _) => { }, setFanCurve: (_, _, _) => { }, showCurve: _ => Task.CompletedTask);

        Assert.Equal(isMax, vm.IsMax);
        Assert.Equal(isAuto, vm.IsAuto);
        Assert.Equal(isCustom, vm.IsCustom);
    }

    /// <summary>The MODE-SWITCH path, which is the one the re-apply outcome feeds: a section already showing one
    /// mode's values must show the next mode's after <c>Load</c>, in every field — including the behaviour, so a
    /// mode whose preset says Max stops being Custom. Pinned as its own fact because construction and reloading
    /// are separate code (the constructor writes the backing fields, <c>Load</c> writes the observable
    /// properties), and a test that only constructed the view-model would leave half the mapping unwatched.</summary>
    [Fact]
    public void ReloadingTheFanSectionReplacesEveryValueFromTheNewState()
    {
        var vm = new FansViewModel(
            new FanCapability(HasMax: true, HasCustom: true, HasGpuFan: true),
            new FanAxisState(FanMode.Custom,
                new FanSettings(UseCurve: true, Curve: [11, 22, 33, 44, 55], FixedDuty: 33),
                new FanSettings(UseCurve: false, Curve: [66, 77, 88, 99, 100], FixedDuty: 77)),
            setFan: (_, _, _) => { }, setFanCurve: (_, _, _) => { }, showCurve: _ => Task.CompletedTask);

        vm.Load(new FanAxisState(FanMode.Max,
                new FanSettings(UseCurve: false, Curve: [55, 44, 33, 22, 11], FixedDuty: 12),
                new FanSettings(UseCurve: true, Curve: [100, 99, 88, 77, 66], FixedDuty: 21)));

        Assert.Equal(12, vm.Cpu);
        Assert.Equal(21, vm.Gpu);
        Assert.False(vm.CpuUseCurve);
        Assert.True(vm.GpuUseCurve);
        Assert.Equal([55, 44, 33, 22, 11], vm.CpuCurve.Select(p => (int)p.Percent));
        Assert.Equal([100, 99, 88, 77, 66], vm.GpuCurve.Select(p => (int)p.Percent));
        Assert.True(vm.IsMax);
        Assert.False(vm.IsCustom);
    }

    /// <summary>The same reload for the GPU section — one line of code, but it is the line the mode switch
    /// runs, and an offsets pair read the wrong way round is exactly the failure a "both are numbers" pair
    /// invites.</summary>
    [Fact]
    public void ReloadingTheGpuSectionReplacesBothOffsets()
    {
        var vm = new GpuViewModel("NVIDIA GeForce RTX 4060 Laptop GPU", (-200, 300), (-1000, 1500),
                                  new GpuAxisState(Core: -150, Mem: 800), set: (_, _) => { });

        vm.Load(new GpuAxisState(Core: 125, Mem: -400));

        Assert.Equal(125, vm.Core);
        Assert.Equal(-400, vm.Mem);
    }

    /// <summary>The GPU section's two sliders, one per offset — again with the two values distinct, so a pair
    /// read the wrong way round cannot pass.</summary>
    [Fact]
    public void TheGpuSectionReadsBothOffsetsFromTheirOwnField()
    {
        var vm = new GpuViewModel("NVIDIA GeForce RTX 4060 Laptop GPU", (-200, 300), (-1000, 1500),
                                  new GpuAxisState(Core: -150, Mem: 800), set: (_, _) => { });

        Assert.Equal(-150, vm.Core);
        Assert.Equal(800, vm.Mem);
    }
}
