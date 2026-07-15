using System.IO;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Generic Linux sysfs transport scoped to a base directory — the Linux analogue of
/// <see cref="WmiInvoker"/>: a thin, vendor-agnostic accessor that vendor feature partials receive in
/// their constructor and use to read/write nodes. It knows nothing about Acer or Linuwu-Sense; the
/// composition root supplies the concrete directory. Reads work as the user; WRITES need write access to
/// the node (root, or a udev rule) — a failed write is surfaced via <paramref name="error"/>, not thrown.</summary>
public sealed class SysfsInvoker(string baseDir)
{
    /// <summary>Whether the base directory exists (composition gate — is this interface present?).</summary>
    public bool Available => Directory.Exists(baseDir);

    /// <summary>Whether a specific node exists (a feature is only offered when its file is present).</summary>
    public bool Has(string file) => File.Exists(Path.Combine(baseDir, file));

    /// <summary>Whether the node exists AND this process may write it (probe-open; nothing is written —
    /// sysfs attrs only act on an actual write). Gate features whose whole point is the write on this, so
    /// the UI never offers a control that fails "permission denied" on every use.</summary>
    public bool CanWrite(string file)
    {
        try
        {
            using var _ = new FileStream(Path.Combine(baseDir, file), FileMode.Open, FileAccess.Write);
            return true;
        }
        catch { return false; }
    }

    public string? Read(string file)
    {
        try
        {
            var path = Path.Combine(baseDir, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    public bool Write(string file, string value, out string? error)
    {
        error = null;
        try { File.WriteAllText(Path.Combine(baseDir, file), value); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // ---- port factories ----
    // The common shape — a bool/choice backed by reading and writing ONE node — as a one-liner, so a vendor
    // InitVendor wires a plain sysfs knob declaratively instead of a Get/Set method pair. Bespoke encodings
    // (bit-packing, bracket-parsed lists, value normalisation) keep their named methods.

    /// <summary>A boolean node (writes <paramref name="on"/>/<paramref name="off"/>, reads true when equal to
    /// <paramref name="on"/>).</summary>
    public FlagPort Flag(string node, string on = "1", string off = "0")
        => new(() => Read(node) == on, v => { var ok = Write(node, v ? on : off, out var e); return (ok, e); });

    /// <summary>A pick-one node whose stored value is the option id verbatim (read/write pass through).</summary>
    public ChoicePort Choice(string node, IReadOnlyList<ChoiceOption> options)
        => new(options, () => Read(node), id => { var ok = Write(node, id, out var e); return (ok, e); });
}
