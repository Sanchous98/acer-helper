using System.Runtime.InteropServices;
using HidSharp;

namespace AcerHelper.Vendors.Acer;

// Windows transport for the ENE controller: HidSharp over the Win32 HID API. Enumeration by VID/PID plus
// the feature-report length picks the right interface among the device's collections. Lazily opened.
internal sealed partial class EneHidController
{
    private HidDevice? _device;
    private HidStream? _stream;

    private partial bool OpenTransport()
    {
        _device = FindDevice();
        return _device != null;
    }

    // Runs on the controller's writer thread (never the UI thread — see EneHidController.SetFeature). The
    // synchronous SetFeature can block on a contended HID-over-I2C bus; that's fine here, it only stalls the
    // worker. On any failure drop the stream so the next write re-opens a fresh handle — otherwise a bad
    // handle opened at boot-with-display (bus busy) would stick forever (the old `_stream ??=` never re-opened).
    private partial bool WriteFeature(byte[] report)
    {
        if (_device == null) return false;
        try
        {
            (_stream ??= _device.Open()).SetFeature(report);
            return true;
        }
        catch { _stream?.Dispose(); _stream = null; return false; }
    }

    private static HidDevice? FindDevice()
    {
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(VID, PID))
                try { if (d.GetMaxFeatureReportLength() == FeatureLen) return d; }
                catch { /* skip interfaces we can't query */ }
        }
        catch { /* no device */ }
        return null;
    }

    private partial void CloseTransport() => _stream?.Dispose();

    // ---- optional HID-transport re-initialisation (experimental, boot-with-display workaround) ----
    // When the laptop is BOOTED WITH an external display attached, the ENE keyboard-backlight controller
    // (HID-over-I2C) writes arrive corrupted for the whole session (amber fallback / "half green, half orange")
    // — see the boot-with-display notes. This attempts the software analog of the physical display replug that
    // recovers it: restart the ENE I2C-HID DEVICE NODE so hidi2c re-runs its transport init (I2C-HID RESET +
    // descriptor fetch + SET_POWER), then re-open a fresh handle. Properties:
    //   * runs ONCE, as the writer thread's first action — a device restart does blocking PnP/I2C work and can
    //     be slow on the contended bus, so it must never touch the UI thread;
    //   * gated on an external display being present (SM_CMONITORS > 1) so a normal boot pays nothing and the
    //     keyboard doesn't blink for no reason;
    //   * scoped to the ENE controller's OWN devnode (ACPI\ENEK5130). On this hardware the keyboard INPUT
    //     device is a SEPARATE node (ACPI\1025174B) — verified via CfgMgr — so typing is never interrupted;
    //   * best-effort: a vetoed remove (another process holds the device) or any failure leaves the existing
    //     handle untouched and writes proceed as before.
    // HONEST CAVEAT: the corruption is ongoing physical bus contention, so a re-init may NOT fix it — it only
    // helps if the bad state was a one-time init latched at POST. It is the one user-space op that maps to the
    // physical replug, so it's worth trying. Set ReinitOnStart=false to disable.
    private static readonly bool ReinitOnStart = true;

    private partial void TryReinitTransport()
    {
        if (!ReinitOnStart) return;
        try
        {
            if (GetSystemMetrics(SM_CMONITORS) <= 1) return;   // no external display -> nothing to work around
            if (!RestartEneDevNode()) return;                   // vetoed/failed -> keep the existing handle
            // The pre-restart handle is stale (the node was removed then re-added); re-enumerate the fresh
            // device. The instance path is ACPI/firmware-fixed so it re-appears at the same path, but the HID
            // child arrival is asynchronous after CM_Setup_DevNode, so poll briefly (bounded ~5 s).
            _stream?.Dispose(); _stream = null;
            _device = null;
            for (var i = 0; i < 25 && _device == null; i++) { Thread.Sleep(200); _device = FindDevice(); }
        }
        catch { /* best-effort: fall through with whatever handle we have */ }
    }

    // Restart the ENE controller's I2C-HID device node (the PARENT of the RGB HID collection — restarting the
    // child collection alone would not re-run the transport init). Returns true if the node was actually cycled
    // (caller must then re-enumerate), false if it couldn't be located or the remove was vetoed/failed (the
    // device is then left untouched).
    private bool RestartEneDevNode()
    {
        var path = _device?.GetFileSystemName();      // \\?\hid#enek5130#<inst>#{iface-guid}
        if (path is null) return false;
        var hidId = HidPathToInstanceId(path);        // -> hid\enek5130\<inst>
        if (hidId is null) return false;
        if (CM_Locate_DevNodeW(out var hidInst, hidId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
        if (CM_Get_Parent(out var i2cInst, hidInst, 0) != CR_SUCCESS) return false;   // the hidi2c node (ACPI\ENEK5130)
        // Runtime (non-persistent) stop of the ENE subtree, then bring it back with CM_Setup_DevNode(READY).
        // UI_NOT_OK suppresses the veto dialog (we're on a background thread and act on the return code only).
        // We deliberately do NOT pass CM_REMOVE_NO_RESTART: that flag leaves the node in a state that only
        // CM_SETUP_DEVNODE_RESET clears, so pairing it with READY could leave the ENE controller stopped until
        // reboot. Without it, CM_Setup_DevNode(READY) is the documented way to restart a query-removed node.
        if (CM_Query_And_Remove_SubTreeW(i2cInst, 0, 0, 0, CM_REMOVE_UI_NOT_OK) != CR_SUCCESS) return false;
        if (CM_Setup_DevNode(i2cInst, CM_SETUP_DEVNODE_READY) != CR_SUCCESS
            && CM_Get_Parent(out var busInst, i2cInst, 0) == CR_SUCCESS)
            CM_Reenumerate_DevNode(busInst, CM_REENUMERATE_SYNCHRONOUS);   // recovery: re-add via the bus parent
        return true;
    }

    // \\?\hid#enek5130#4&2c37b939&0&0000#{4d1e55b2-...} -> hid\enek5130\4&2c37b939&0&0000 (CM_Locate is case-insensitive).
    private static string? HidPathToInstanceId(string devicePath)
    {
        var s = devicePath;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal))
            s = s[4..];
        var iface = s.LastIndexOf("#{", StringComparison.Ordinal);   // strip the trailing interface-class GUID
        if (iface >= 0) s = s[..iface];
        s = s.Replace('#', '\\');
        return s.Length > 0 ? s : null;
    }

    private const uint CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_REMOVE_UI_NOT_OK = 0x00000001;   // suppress the query-remove veto dialog
    private const uint CM_SETUP_DEVNODE_READY = 0;
    private const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;
    private const int SM_CMONITORS = 80;

    // CfgMgr32 device-node control (AOT-safe source-generated P/Invoke). pVetoType/pszVetoName are optional and
    // passed as NULL (nint 0) — we act on the return code only.
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Query_And_Remove_SubTreeW(uint dnAncestor, nint pVetoType, nint pszVetoName, uint ulNameLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Setup_DevNode(uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}
