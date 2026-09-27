using System.IO;
using System.Security.Cryptography;

namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// THE LOCAL PER-USER PLUGIN STORE (docs/vendor-plugins.md §5.3, §5.3.1) — where a downloaded plugin lives, how it
/// is recorded so STARTUP verification is a LOCAL hash check, and how it is installed ATOMICALLY. It is
/// deliberately NETWORK-FREE, GitHub-free and wiring-free: task D2 downloads the bytes and calls
/// <see cref="Install"/>; this layer only decides where they go and whether what is on disk is trustworthy.
///
/// PATHS, AND WHY THEY REUSE <c>DeviceFactory</c>'s DERIVATION. §5.3.1 names
/// <c>%AppData%\AcerHelper\plugins</c> on Windows and <c>~/.local/share/acer-helper/plugins</c> on Linux.
/// <c>DeviceFactory.PerUserPluginCacheDir</c> (<c>DeviceFactory.cs:172-176</c>) already derives that root as
/// <c>Environment.GetFolderPath(ApplicationData)</c> + <c>"AcerHelper","plugins"</c> — and
/// <c>ApplicationData</c> resolves to the XDG data dir on Linux, which is exactly the <c>~/.local/share</c> in
/// the doc. Copying that expression here rather than re-deriving it from an OS check is the whole point: the
/// cache writer and the loader enumerator must be the SAME directory or a downloaded plugin would never be
/// found. This class therefore owns the one shared root helper (<see cref="DefaultRoot"/>) and D2 should move
/// <c>DeviceFactory</c> onto it; <c>DeviceFactory</c> is NOT edited in this slice.
///
/// LAYOUT (§5.3.1, with the major axis added per §5.3 step 3 / §3.8):
/// <code>
///   &lt;root&gt;/&lt;plugin-id&gt;/v&lt;api-major&gt;/&lt;asset-file&gt;
///   &lt;root&gt;/&lt;plugin-id&gt;/v&lt;api-major&gt;/installed.json
/// </code>
/// The doc shows <c>&lt;root&gt;/&lt;plugin-id&gt;/&lt;asset-file&gt;</c>; the <c>v&lt;major&gt;</c> segment is the
/// key the design requires ("the cache is keyed by pluginId AND major, so both majors' builds can coexist during
/// the window", §5.3 step 3; §3.8.3). A subfolder under the existing per-id folder is chosen over a
/// <c>&lt;id&gt;-v&lt;major&gt;</c> flat name because it keeps the doc's "one folder per plugin id" shape and
/// keeps <c>DeviceFactory</c>'s recursive scan (<c>DeviceFactory.cs:157</c>) correct without knowing the key
/// scheme. A mis-keyed directory is harmless: the loader trusts <c>ah_plugin_id</c>, not the folder (§4.1.1).
///
/// THE VERIFY SPLIT (documented where it is enforced):
///   * <see cref="VerifyInstalled(PluginInstallRecord)"/> owns the LOCAL INTEGRITY half — re-hash the cached
///     asset and compare it to the record's <c>sha256</c>. At-rest corruption or tampering flips it to false.
///   * The AUTHENTICITY half — the detached signature over the manifest entry (§5.4) — belongs to
///     <see cref="PluginSignature"/> and needs the <see cref="PluginCatalogEntry"/> the host fetched, which is
///     NOT cached. <see cref="VerifyInstalled(PluginInstallRecord, PluginCatalogEntry)"/> runs BOTH: it binds
///     record↔entry, then calls <see cref="PluginSignature.VerifyBinary"/> (signature → manifest hash → binary).
/// <see cref="Install"/> runs the full chain BEFORE touching the disk, so a cached asset that has a record was
/// verified end to end at the moment it was written.
///
/// NO EXCEPTION ESCAPES. A missing/corrupt <c>installed.json</c>, an unreadable directory, a hash mismatch and a
/// failed signature all degrade to <c>null</c>/<c>false</c> — the same fail-closed style as
/// <see cref="PluginSignature"/> and the loader (§4.1.1, §4.3 "never a crash"). A verify path that can throw is
/// a crash an attacker chooses the timing of.
///
/// DEPENDENCY-FREE: BCL only (<see cref="File"/>, <see cref="SHA256"/>, source-gen JSON), AOT-safe.
/// </summary>
internal sealed class PluginCache
{
    /// <summary>Construct against an EXPLICIT root — the only constructor, so a test can point it at a temp
    /// directory and never touch the real <c>%AppData%</c>. Production uses <see cref="DefaultRoot"/>.</summary>
    /// <param name="root">The cache root, absolute. Must not be null/blank; the directory may not exist yet
    /// (the first install creates it).</param>
    public PluginCache(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("a cache root is required", nameof(root));
        Root = root;
    }

    /// <summary>The cache root this instance writes under (§5.3.1). Absolute.</summary>
    public string Root { get; }

    /// <summary>
    /// The real per-user cache root — the ONE shared derivation §5.3.1 asks for, byte-for-byte the expression
    /// <c>DeviceFactory.PerUserPluginCacheDir</c> uses today (<c>DeviceFactory.cs:172-176</c>):
    /// <c>ApplicationData</c> + <c>"AcerHelper","plugins"</c>. It is a static method rather than a property so a
    /// test can call it without constructing an instance, and it returns null — never a partial path — when the
    /// OS exposes no such folder, which the caller treats as "no cache". D2 should move <c>DeviceFactory</c> onto
    /// this helper so writer and reader cannot drift.
    /// </summary>
    public static string? DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "AcerHelper", "plugins");
    }

    /// <summary>
    /// Read the <c>installed.json</c> record for a plugin id + API major, or null when there is none. A missing
    /// file, an unreadable directory or malformed JSON all return null with no throw: "not installed" is the
    /// ordinary state, not an error (§5.3.1). The record is returned as-is; the caller decides whether to trust
    /// it by running <see cref="VerifyInstalled(PluginInstallRecord)"/>.
    /// </summary>
    public PluginInstallRecord? FindInstalled(string? pluginId, int apiMajor)
    {
        try
        {
            var recordPath = RecordPath(pluginId, apiMajor);
            if (recordPath is null || !File.Exists(recordPath)) return null;

            var json = File.ReadAllText(recordPath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return System.Text.Json.JsonSerializer.Deserialize(
                json, PluginInstallRecordJsonContext.Default.PluginInstallRecord);
        }
        catch
        {
            // Corrupt JSON, a permissions fault, a race with a concurrent uninstall — all "not installed".
            return null;
        }
    }

    /// <summary>
    /// The absolute on-disk asset path the record names, or null when the record is malformed or the file is
    /// missing. The path is built from the record's id + major (never from <c>asset</c> alone) and the asset is
    /// reduced to a single sanitised segment, so a hostile record cannot escape the cache tree.
    /// </summary>
    public string? ResolveInstalledPath(PluginInstallRecord? record)
    {
        try
        {
            if (record?.Id is null || record.Api is null || record.Asset is null) return null;
            var dir = PluginDirectory(record.Id, record.Api.Major);
            var file = SanitizeAssetName(record.Asset);
            if (dir is null || file is null) return null;

            var path = Path.Combine(dir, file);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// THE LOCAL, NETWORK-FREE INTEGRITY CHECK (§5.3 step 5): re-read the cached asset and compare its SHA-256 to
    /// the record's. A tampered or truncated byte makes this false. It does NOT verify the signature — the record
    /// carries no signature, because the signature lives in the fetched manifest (§5.4) — so the authenticity
    /// half is a separate concern; see <see cref="VerifyInstalled(PluginInstallRecord, PluginCatalogEntry)"/> for
    /// the full chain. Returns false on any missing/unreadable input, never throws.
    /// </summary>
    public bool VerifyInstalled(PluginInstallRecord? record)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(record?.Sha256)) return false;
            var path = ResolveInstalledPath(record);
            if (path is null) return false;

            var bytes = File.ReadAllBytes(path);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            // Case-insensitive: the manifest's spelling is a producer detail, the bytes are the fact (§5.4).
            return string.Equals(actual, record!.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// THE FULL §5.4 CHAIN at rest: bind the cached record to the manifest <paramref name="entry"/> and then run
    /// <see cref="PluginSignature.VerifyBinary"/> over the cached bytes, which itself checks the detached
    /// signature AND that the entry's <c>sha256</c> equals the bytes. This is the overload a startup that still
    /// holds the release manifest should call; the single-argument overload is the cheaper local hash re-check.
    ///
    /// The binding is required, not decorative: without it a valid signature over entry A could be paired with a
    /// record/asset for entry B. Identity (id + api.major) and the hash + asset name must agree before the
    /// signature is even consulted, so a swapped entry fails closed. Returns false on any mismatch or read fault.
    /// </summary>
    public bool VerifyInstalled(PluginInstallRecord? record, PluginCatalogEntry? entry)
    {
        try
        {
            if (record is null || entry is null) return false;

            // Record ↔ entry must describe the same cached artefact before the signature is trusted.
            if (!string.Equals(record.Id, entry.Id, StringComparison.Ordinal)) return false;
            if (record.Api is null || entry.Api is null || record.Api.Major != entry.Api.Major) return false;
            if (!string.Equals(record.Sha256?.Trim(), entry.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(record.Asset, entry.Asset, StringComparison.Ordinal)) return false;

            var path = ResolveInstalledPath(record);
            if (path is null) return false;

            var bytes = File.ReadAllBytes(path);
            return PluginSignature.VerifyBinary(entry, bytes); // signature → manifest hash → binary
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Install downloaded plugin bytes into the cache (§5.3 step 4, §5.3.1), atomically and only after the full
    /// §5.4 chain passes. ORDER IS THE SAFETY PROPERTY: <see cref="PluginSignature.VerifyBinary"/> runs FIRST, on
    /// the in-memory bytes, so a bad signature or a wrong hash returns false having touched NOTHING — a previously
    /// installed good copy survives a failed update attempt intact. Only then is the directory created and the
    /// asset written under a temp name in that SAME directory and <c>File.Move</c>-d over the target (the same
    /// idiom as <c>AppImageUpdater.ReplaceAsync</c>, <c>AppImageUpdater.cs:31,46</c>, so the move is on one
    /// filesystem and a crash mid-write can never leave a half plugin under the real name, §5.3.1). The
    /// <c>installed.json</c> sidecar is written the same way AFTER the asset, so a crash between the two leaves a
    /// stale/mismatching record that <see cref="VerifyInstalled(PluginInstallRecord)"/> reports as false — fail
    /// closed, and D2 simply re-downloads.
    ///
    /// Returns true only when the asset and its record are both in place. Never throws.
    /// </summary>
    public bool Install(PluginCatalogEntry? entry, byte[]? bytes)
    {
        try
        {
            if (entry is null || bytes is null) return false;
            if (entry.Id is null || entry.Api is null || entry.Asset is null) return false;

            var dir = PluginDirectory(entry.Id, entry.Api.Major);
            var assetName = SanitizeAssetName(entry.Asset);
            if (dir is null || assetName is null) return false;

            // VERIFY BEFORE WRITING. This is the guarantee that a refused install leaves the existing good copy
            // untouched: nothing below runs unless the detached signature and the bytes' SHA-256 both check out.
            if (!PluginSignature.VerifyBinary(entry, bytes)) return false;

            Directory.CreateDirectory(dir);
            var assetPath = Path.Combine(dir, assetName);
            var recordPath = Path.Combine(dir, RecordFileName);

            WriteAtomic(assetPath, bytes);
            WriteAtomic(recordPath, BuildRecordBytes(entry, assetName));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Install from a downloaded TEMP FILE path — the shape D2's downloader produces (a temp file then a move, as
    /// <c>WindowsUpdater.DownloadAsync</c> does, <c>WindowsUpdater.cs:59-80</c>). The bytes are read once and
    /// handed to <see cref="Install(PluginCatalogEntry, byte[])"/>, so the verify-before-write and atomic-write
    /// rules are identical; this overload only spares the caller an explicit read. The caller owns the temp file
    /// (this method never deletes it). Returns false on any read/verify/install failure.
    /// </summary>
    public bool Install(PluginCatalogEntry? entry, string? downloadedFilePath)
    {
        try
        {
            if (entry is null || string.IsNullOrWhiteSpace(downloadedFilePath) || !File.Exists(downloadedFilePath))
                return false;
            return Install(entry, File.ReadAllBytes(downloadedFilePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The sidecar's fixed file name inside the id+major directory (§5.3.1).</summary>
    internal const string RecordFileName = "installed.json";

    /// <summary>
    /// The directory for one plugin id + API major: <c>&lt;root&gt;/&lt;id&gt;/v&lt;major&gt;</c>. Returns null
    /// when the id is not a safe single path segment (it is opaque manifest input, §5.2) — an id containing a
    /// separator or <c>..</c> must never be able to point the cache outside its root. The major is an int and is
    /// therefore always safe to render.
    /// </summary>
    internal string? PluginDirectory(string? pluginId, int apiMajor)
    {
        var id = SanitizeSegment(pluginId);
        return id is null ? null : Path.Combine(Root, id, "v" + apiMajor.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The path of <c>installed.json</c> for an id + major, or null when the id is unsafe.</summary>
    internal string? RecordPath(string? pluginId, int apiMajor)
    {
        var dir = PluginDirectory(pluginId, apiMajor);
        return dir is null ? null : Path.Combine(dir, RecordFileName);
    }

    /// <summary>
    /// Reduce a manifest token (the plugin id, or the asset name) to a single safe path segment. Rejects the
    /// empty string, any string equal to <c>"."</c>/<c>".."</c>, and anything containing a directory separator or
    /// a character <see cref="Path.GetInvalidFileNameChars"/> forbids. Rejecting rather than sanitising is the
    /// fail-closed choice: silently rewriting an id would make the cache folder disagree with the id the loader
    /// later reads from <c>ah_plugin_id</c> (§4.1.1), producing exactly the misnamed-folder bug the design says
    /// must be caught at load, not trusted.
    /// </summary>
    private static string? SanitizeSegment(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token is "." or "..") return null;
        if (token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        // IndexOfAny already covers the platform separator, but DirectorySeparatorChar is checked explicitly in
        // case a future platform's invalid-char set ever omits it.
        if (token.Contains(Path.DirectorySeparatorChar) || token.Contains(Path.AltDirectorySeparatorChar)) return null;
        return token;
    }

    /// <summary>The asset name as a bare file name (same guard as <see cref="SanitizeSegment"/>).</summary>
    private static string? SanitizeAssetName(string? asset) => SanitizeSegment(asset);

    /// <summary>Build the <c>installed.json</c> bytes from a verified entry. The record copies the manifest facts
    /// the signature authenticated (<c>id</c>, <c>version</c>, <c>api</c>, <c>sha256</c>, <c>keyId</c>) plus the
    /// install timestamp; it adds no field the signature did not cover except the clock, which is local
    /// bookkeeping. Source-gen serialisation keeps it AOT-safe.</summary>
    private static byte[] BuildRecordBytes(PluginCatalogEntry entry, string assetName)
    {
        var record = new PluginInstallRecord
        {
            Id = entry.Id,
            Asset = assetName,
            Version = entry.Version,
            Api = entry.Api,
            Sha256 = entry.Sha256?.Trim().ToLowerInvariant(),
            KeyId = entry.KeyId,
            InstalledAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            record, PluginInstallRecordJsonContext.Default.PluginInstallRecord);
    }

    /// <summary>
    /// Write <paramref name="bytes"/> to <paramref name="target"/> atomically: a GUID-named temp file in the
    /// SAME directory (same filesystem, so the move is atomic) then <c>File.Move(..., overwrite: true)</c>. This
    /// is the <c>AppImageUpdater.ReplaceAsync</c> idiom (<c>AppImageUpdater.cs:31,46</c>) that §5.3.1 cites. The
    /// temp file is removed on any failure so a refused write never leaves junk that the loader's recursive scan
    /// (<c>DeviceFactory.cs:157</c>) could try to load.
    /// </summary>
    private static void WriteAtomic(string target, byte[] bytes)
    {
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw; // the caller's catch turns this into the documented "false"
        }
    }
}
