namespace AcerHelper.Infrastructure;

/// <summary>What a click on the update notification will actually do. Chosen from the release's assets and what
/// THIS process can perform, so the UI only has to run the plan it is handed.</summary>
public enum UpdateAction
{
    /// <summary>Nothing installable here: open the release page (the fallback for an RPM/other install, or a
    /// release with no asset we can handle).</summary>
    OpenPage,

    /// <summary>The MSI-installed Windows build: silent in-place major upgrade, then relaunch.</summary>
    UpgradeWindows,

    /// <summary>A portable/development Windows run: download the release MSI and launch the Windows Installer.</summary>
    InstallWindows,

    /// <summary>Linux under an AppImage: replace the running file in place.</summary>
    ReplaceAppImage,
}

/// <summary>The chosen action and the asset it needs (null for <see cref="UpdateAction.OpenPage"/>).</summary>
public readonly record struct UpdatePlan(UpdateAction Kind, ReleaseAsset? Asset);

/// <summary>Decides which of the platform self-updaters a found release engages. Kept PURE — every OS fact
/// arrives as a parameter rather than being probed here — so the choice can be tested with the real release's
/// asset names on any host. The one defect this closes: a Windows build that is NOT the MSI install (a portable
/// download, a development run) used to fall straight through to "open the release page", so the notification
/// pointed at GitHub and never offered to install anything. It now downloads the MSI and launches the Windows
/// Installer, which is the only install path the Windows release ships.</summary>
public static class UpdateRouter
{
    /// <param name="assets">The release's assets.</param>
    /// <param name="isWindows">Whether this process runs on Windows.</param>
    /// <param name="windowsInstalled">Whether this is the MSI-installed Windows build (<see cref="WindowsUpdater.IsSupported"/>).</param>
    /// <param name="isAppImage">Whether this is a Linux AppImage run (<see cref="AppImageUpdater.IsAppImage"/>).</param>
    public static UpdatePlan Route(IReadOnlyList<ReleaseAsset> assets, bool isWindows, bool windowsInstalled, bool isAppImage)
    {
        var msi = WindowsUpdater.PickAsset(assets);
        if (isWindows && msi != null)
            return new UpdatePlan(windowsInstalled ? UpdateAction.UpgradeWindows : UpdateAction.InstallWindows, msi);

        var appImage = AppImageUpdater.PickAsset(assets);
        if (isAppImage && appImage != null)
            return new UpdatePlan(UpdateAction.ReplaceAppImage, appImage);

        return new UpdatePlan(UpdateAction.OpenPage, null);
    }
}
