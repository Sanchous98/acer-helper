using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ICurveOptimizer"/>. <see cref="DomainList"/> is deliberately MUTABLE: the
/// per-domain presets are keyed by <see cref="VoltageDomain.Key"/> rather than by list position, and the
/// only way to prove that is to reorder the domains between a write and a read.
///
/// <see cref="Range"/> defaults to the real port's shape — <c>(Min, 0)</c>, undervolt only — so a clamp
/// assertion against it is the same assertion the shipped Ryzen backend would produce. It is settable so a
/// test can prove the clamp follows the PORT rather than any hard-coded bound.
/// </summary>
public sealed class FakeCurveOptimizer : ICurveOptimizer
{
    private readonly List<VoltageDomain> _domains = [];

    public string Name { get; set; } = "Fake CPU";
    public (int Min, int Max) Range { get; set; } = (-30, 0);
    public double MillivoltsPerCount { get; set; } = 2.5;

    /// <summary>An empty list = "this CPU takes ONE offset for everything" (the AllCore path).</summary>
    public IReadOnlyList<VoltageDomain> Domains => _domains;

    public List<VoltageDomain> DomainList => _domains;

    public string? LastError { get; set; }
    public bool SetResult { get; set; } = true;
    public bool SetDomainsResult { get; set; } = true;

    /// <summary>Every count handed to <see cref="Set"/>, in order, including refused ones.</summary>
    public List<int> SetCalls { get; } = [];

    /// <summary>A copy of every array handed to <see cref="SetDomains"/>, in order.</summary>
    public List<int[]> SetDomainsCalls { get; } = [];

    public bool Set(int counts)
    {
        SetCalls.Add(counts);
        return SetResult;
    }

    public bool SetDomains(IReadOnlyList<int> counts)
    {
        SetDomainsCalls.Add([.. counts]);
        return SetDomainsResult;
    }

    public FakeCurveOptimizer WithDomains(params VoltageDomain[] domains)
    {
        _domains.AddRange(domains);
        return this;
    }
}
