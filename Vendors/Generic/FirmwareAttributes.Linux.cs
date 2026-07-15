using System.IO;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Generic Linux accessor for the kernel's firmware-attributes class
/// (<c>/sys/class/firmware-attributes/&lt;device&gt;/</c>) — BIOS settings exposed as sysfs attributes with
/// <c>current_value</c>/<c>possible_values</c>/<c>type</c> files, plus an <c>authentication/</c> sub-tree.
/// Vendor-neutral: the same ABI serves Dell (dell-wmi-sysman), Lenovo (think-lmi) and HP (hp-bioscfg); the
/// vendor device supplies the device name and the attribute names it understands.
///
/// Access model (important): attribute METADATA is world-readable, but <c>current_value</c> is typically
/// root-only for BOTH read and write, so a feature is only offered when its current value is readable
/// (<see cref="CanRead"/>). Writes are additionally gated by the firmware: when a BIOS admin password is
/// configured (<see cref="RequiresPassword"/>), the kernel rejects attribute writes unless the password is
/// supplied first — which this app doesn't do — so callers should not offer those controls.</summary>
public sealed class FirmwareAttributes
{
    private readonly string _root;    // /sys/class/firmware-attributes/<device>
    private readonly string _attrs;   // <root>/attributes

    private FirmwareAttributes(string root) { _root = root; _attrs = Path.Combine(root, "attributes"); }

    /// <summary>Open a firmware-attributes device (e.g. <c>"dell-wmi-sysman"</c>); null if absent.</summary>
    public static FirmwareAttributes? TryCreate(string device)
    {
        var root = $"/sys/class/firmware-attributes/{device}";
        try { return Directory.Exists(Path.Combine(root, "attributes")) ? new FirmwareAttributes(root) : null; }
        catch { return null; }
    }

    /// <summary>True when a BIOS admin password is configured. The kernel then refuses attribute writes
    /// unless the password is first cached in <c>authentication/Admin/current_password</c> (unsupported here),
    /// so password-gated controls should be hidden rather than offered and failing with -EOPNOTSUPP.</summary>
    public bool RequiresPassword => ReadRaw(Path.Combine(_root, "authentication", "Admin", "is_enabled")) == "1";

    /// <summary>Whether the attribute exists AND its current value is readable (see class remarks).</summary>
    public bool CanRead(string attribute) => Read(attribute) != null;

    /// <summary>The attribute's current value, or null (absent / unreadable without root).</summary>
    public string? Read(string attribute) => ReadRaw(Path.Combine(_attrs, attribute, "current_value"));

    public bool Write(string attribute, string value, out string? error)
    {
        error = null;
        try { File.WriteAllText(Path.Combine(_attrs, attribute, "current_value"), value); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static string? ReadRaw(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    // ---- port factories ----
    // A BIOS attribute wired as a bool/choice in one line (the current_value read/write pair). Vendor
    // defaults are the usual firmware-attribute enum literals.

    public FlagPort Flag(string attribute, string on = "Enabled", string off = "Disabled")
        => new(() => Read(attribute) == on, v => { var ok = Write(attribute, v ? on : off, out var e); return (ok, e); });

    public ChoicePort Choice(string attribute, IReadOnlyList<ChoiceOption> options)
        => new(options, () => Read(attribute), id => { var ok = Write(attribute, id, out var e); return (ok, e); });
}
