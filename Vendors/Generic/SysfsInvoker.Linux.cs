using System.IO;

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
}
