namespace AcerHelper.Domain;

/// <summary>The fan axis as a mode leaves it: which behaviour that mode is in, and each fan's own data. The
/// DOMAIN's vocabulary for the pair, and deliberately not the stored preset's field layout — a fan's data is
/// <see cref="FanSettings"/> (introduced for exactly this neutrality), and the halves are named CPU and GPU
/// only in the STORED form (Infrastructure/Composition/Settings.cs), which one reader translates
/// (<c>LaptopService.FanSettingsOf</c>).
///
/// It exists because a re-apply has to REPORT what it applied to the calling site's UI pass, and the report
/// crosses into Application — which may not name the container, because the container is Infrastructure
/// (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>). A domain value is what can cross,
/// and this one is not a second spelling of the preset: the mode is the domain's <see cref="FanMode"/> rather
/// than the stored integer, and each half is the type the fan models already take.</summary>
public readonly record struct FanAxisState(FanMode Mode, FanSettings Cpu, FanSettings Gpu);

/// <summary>The GPU axis as a mode leaves it: the two clock offsets in MHz, which are the whole of it — the
/// driver keeps no third thing on this axis. It crosses the same border as <see cref="FanAxisState"/> and for
/// the same reason.</summary>
public readonly record struct GpuAxisState(int Core, int Mem);
