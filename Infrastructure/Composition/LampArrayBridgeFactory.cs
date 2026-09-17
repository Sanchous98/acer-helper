using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>Composition's answer to <see cref="IDynamicLightingFactory"/>: it holds the OS transport this
/// machine produced at composition time and builds the bridge over whatever zones the device advertises.
///
/// It is four lines, and it earns its place: building the bridge is the one act that would otherwise force
/// Application to name <see cref="LampArrayBridge"/> and <see cref="ILampArrayTransport"/>, and Application must
/// not depend on Infrastructure. The construction stays here, where naming Infrastructure is the job.</summary>
internal sealed class LampArrayBridgeFactory(ILampArrayTransport transport) : IDynamicLightingFactory
{
    public IDynamicLighting Create(IRgbDevice rgb, Func<RgbZone, bool> include)
        => new LampArrayBridge(rgb, transport, include);
}
