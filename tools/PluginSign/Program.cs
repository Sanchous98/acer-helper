using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Tools.PluginSign;

/// <summary>
/// THE CI PLUGIN SIGNER (docs/vendor-plugins.md §5.4, §5.5). Two modes, both driven entirely from the command
/// line so a GitHub Actions step can run them with no interactive input:
///
///   sign     — the job that produced the plugin bytes runs this: it SHA-256s the asset, builds the manifest
///              entry, signs the canonical string (§5.4) with the CI secret, self-verifies with the host's own
///              <see cref="PluginSignature"/>, and writes two artefacts: a one-entry catalog FRAGMENT (JSON) and
///              the standalone <c>.sig</c> release asset.
///   manifest — the aggregate job runs this once: it reads the per-OS fragments the sign jobs wrote and merges
///              them into the single <c>vendor-plugins.json</c> the host fetches (§5.2).
///
/// IT DOES NOT RE-IMPLEMENT THE CONTRACT. The canonical string, the ECDSA call, the wire DTOs and the JSON
/// context are the host's own files, SOURCE-INCLUDED by PluginSign.csproj (`Infrastructure/Plugins/Distribution/
/// Plugin{Signature,Catalog,PublicKeys}.cs`). This file only does argument parsing, file I/O and glue, so the
/// bytes CI signs are produced by the same code the shipped host verifies with — the drift §5.4's
/// "cross-version contract" warning is about cannot happen here.
///
/// FAIL LOUDLY, NEVER SILENTLY. A missing <c>PLUGIN_SIGNING_KEY</c>, an unreadable key, an absent/empty asset or
/// a fragment that does not verify all exit NON-ZERO with a clear message: a release with unsigned or
/// unverifiable plugins is worse than a failed build (the owner's locked decision, task D3).
/// </summary>
internal static class Program
{
    /// <summary>The environment variable holding the PKCS#8 DER (base64) signing key — the GitHub Actions secret
    /// (§5.4, §7.3 item 2). It is never a command-line argument, so it cannot leak into a process list.</summary>
    private const string SigningKeyEnv = "PLUGIN_SIGNING_KEY";

    private static int Main(string[] args)
    {
        try
        {
            var opts = Args.Parse(args);
            return opts.Mode switch
            {
                "sign" => Sign(opts),
                "manifest" => Manifest(opts),
                _ => Fail(1, $"unknown or missing mode '{opts.Mode}'. Usage: PluginSign sign ... | PluginSign manifest ..."),
            };
        }
        catch (UsageException ex)
        {
            return Fail(1, ex.Message);
        }
        catch (Exception ex)
        {
            // Any other failure (I/O, crypto, JSON) is fatal and must be visible: exit non-zero with the reason.
            return Fail(2, ex.Message);
        }
    }

    /// <summary>
    /// `sign` mode: hash <c>--asset</c>, build the manifest entry, sign it, self-verify, then write the fragment
    /// (<c>--out</c>) and the detached <c>.sig</c> (<c>--sig-out</c>).
    /// </summary>
    private static int Sign(Args o)
    {
        var assetPath = o.Require("asset");
        var outPath = o.Require("out");
        var sigOutPath = o.Require("sig-out");

        var bytes = File.ReadAllBytes(assetPath);
        if (bytes.Length == 0)
            throw new UsageException($"the plugin asset '{assetPath}' is empty — refusing to sign nothing");

        var id = o.Require("id");
        var os = o.Require("os");
        var arch = o.Get("arch") ?? "x64";
        var version = o.Require("version");
        var apiMajor = int.Parse(o.Get("api-major") ?? "1", CultureInfo.InvariantCulture);
        var apiMinor = int.Parse(o.Get("api-minor") ?? "0", CultureInfo.InvariantCulture);
        var minHost = o.Require("min-host");
        var keyId = o.Get("key-id") ?? PluginPublicKeys.ReleaseKeyId;

        // §5.2 asset naming: <AssemblyName>-<os>-<arch>.<ext>, and the assembly name is
        // "AcerHelper.Vendor.<id>". Derived from the id so the caller cannot publish an asset whose name disagrees
        // with its manifest id; --asset-name overrides for an unusual plugin.
        var assetName = o.Get("asset-name") ?? $"AcerHelper.Vendor.{id}-{os}-{arch}.{Ext(os)}";

        // The release download URLs are assembled here from a base the workflow passes (server_url/repo/tag) so
        // the host never assembles them from parts (§5.2); absent a base they are left null and the aggregate can
        // still merge (a manifest without url/sig names no fetchable asset, which the host treats as no candidate).
        var baseUrl = o.Get("base-url")?.TrimEnd('/');

        var entry = new PluginCatalogEntry
        {
            Id = id,
            Vendor = o.Get("vendor"),
            Os = os,
            Arch = arch,
            Version = version,
            Api = new PluginApiRange { Major = apiMajor, Minor = apiMinor },
            MinHost = minHost,
            MatchHint = BuildHint(o),
            Asset = assetName,
            Url = baseUrl is null ? null : $"{baseUrl}/{assetName}",
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            KeyId = keyId,
            SigAlg = o.Get("sig-alg") ?? PluginSignature.Algorithm,
        };

        // The private key is read from the environment only (never argv). A missing secret is fatal: signing a
        // release without it is the outcome this tool exists to prevent.
        var key = Environment.GetEnvironmentVariable(SigningKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
            throw new UsageException(
                $"{SigningKeyEnv} is not set. A release tag must be signed with the CI secret; refusing to " +
                "produce an unsigned manifest entry (docs/vendor-plugins.md §5.4/§5.5).");

        entry.Signature = PluginSignature.Sign(entry, key!);
        if (baseUrl is not null)
            entry.Sig = $"{baseUrl}/{assetName}.sig";

        // SELF-CHECK WITH THE HOST'S OWN VERIFIER. The whole point of the source-include is that this call is the
        // same code the shipped host runs; a false here means the two have drifted or the key is wrong, and the
        // job must stop before it publishes a manifest the host would refuse.
        if (!PluginSignature.Verify(entry))
            throw new UsageException(
                "the signature this tool produced does NOT verify under the host's PluginSignature.Verify — " +
                "refusing to write a manifest entry the host would reject.");
        if (!PluginSignature.VerifyBinary(entry, bytes))
            throw new UsageException(
                "the signed sha256 does not verify against the asset bytes — refusing to write a mismatched entry.");

        // Fragment: a one-entry catalog, so the aggregate mode reads it with the SAME source-generated context it
        // serialises the final manifest with (§5.2). Written atomically-ish: write, so a partial file is the only
        // artefact and the aggregate job's own guard catches it.
        var fragment = new PluginCatalog { Schema = 1, Plugins = [entry] };
        File.WriteAllText(outPath, JsonSerializer.Serialize(fragment, PluginCatalogJsonContext.Default.PluginCatalog));

        // The standalone `.sig` release asset (§5.2), base64 — exactly the `signature` field, published for
        // external tooling/CI. No BOM, UTF-8.
        File.WriteAllText(sigOutPath, entry.Signature);

        Console.WriteLine($"[PluginSign] signed {id}/{os}/{arch} {version} sha256={entry.Sha256} key={keyId}");
        Console.WriteLine($"[PluginSign]   {outPath}  ({assetName})");
        Console.WriteLine($"[PluginSign]   {sigOutPath}  (.sig)");
        return 0;
    }

    /// <summary>
    /// `manifest` mode: read each <c>--fragment</c> the sign jobs wrote and merge all plugin entries into one
    /// <c>vendor-plugins.json</c> (<c>--out</c>), sorted deterministically by (id, os, arch) so a re-run produces
    /// a byte-identical file and a release diff is meaningful.
    /// </summary>
    private static int Manifest(Args o)
    {
        var fragments = o.GetAll("fragment");
        if (fragments.Count == 0)
            throw new UsageException("manifest mode needs at least one --fragment <file>");

        var outPath = o.Require("out");

        var entries = new List<PluginCatalogEntry>();
        foreach (var fragmentPath in fragments)
        {
            if (!File.Exists(fragmentPath) || new FileInfo(fragmentPath).Length == 0)
                throw new UsageException($"fragment '{fragmentPath}' is missing or empty — the aggregate job " +
                                         "will not publish a manifest built from a missing per-OS sign job.");

            var text = File.ReadAllText(fragmentPath);
            var catalog = JsonSerializer.Deserialize(text, PluginCatalogJsonContext.Default.PluginCatalog)
                          ?? throw new UsageException($"fragment '{fragmentPath}' did not parse to a catalog");
            if (catalog.Plugins is null || catalog.Plugins.Count == 0)
                throw new UsageException($"fragment '{fragmentPath}' carries no plugin entries");

            // Re-verify every merged entry against the host verifier. The fragment was written by a sign job, but a
            // truncated or hand-edited fragment must not reach the release; this is cheap and fail-closed.
            foreach (var entry in catalog.Plugins)
                if (!PluginSignature.Verify(entry))
                    throw new UsageException(
                        $"entry '{entry.Id}/{entry.Os}/{entry.Arch}' from '{fragmentPath}' does not verify — " +
                        "refusing to publish it.");

            entries.AddRange(catalog.Plugins);
        }

        entries.Sort(CompareEntries);
        var manifest = new PluginCatalog { Schema = 1, Plugins = entries };
        File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, PluginCatalogJsonContext.Default.PluginCatalog));

        Console.WriteLine($"[PluginSign] wrote {outPath} with {entries.Count} entries: " +
                          string.Join(", ", entries.ConvertAll(e => $"{e.Id}/{e.Os}/{e.Arch}")));
        return 0;
    }

    /// <summary>Build the advisory matchHint (§3.2, §5.2) from repeated <c>--hint-manufacturer</c> /
    /// <c>--hint-product</c> values. No hints at all means a null hint, which imposes no constraint (the correct
    /// "no hint" behaviour); the signer never invents vendor vocabulary itself.</summary>
    private static PluginMatchHint? BuildHint(Args o)
    {
        var manufacturer = o.GetAll("hint-manufacturer");
        var product = o.GetAll("hint-product");
        if (manufacturer.Count == 0 && product.Count == 0) return null;
        return new PluginMatchHint
        {
            Manufacturer = manufacturer.Count == 0 ? null : manufacturer,
            Product = product.Count == 0 ? null : product,
        };
    }

    /// <summary>The .so/.dll extension for an OS token (§5.2). Windows loads a native DLL, Linux a .so; any
    /// non-"win" token is treated as the ELF case, mirroring the plugin csproj's "not windows" rule.</summary>
    private static string Ext(string os) => os.Equals("win", StringComparison.OrdinalIgnoreCase) ? "dll" : "so";

    private static int CompareEntries(PluginCatalogEntry a, PluginCatalogEntry b)
    {
        var c = string.CompareOrdinal(a.Id, b.Id);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Os, b.Os);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Arch, b.Arch);
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine($"[PluginSign] ERROR: {message}");
        return code;
    }

    // ---- minimal, dependency-free argument parsing -----------------------------------------------------------

    /// <summary>Thrown for a producer-side configuration error (bad args, missing secret, failed self-check). The
    /// top-level catch turns it into a clear non-zero exit; a verify failure in the loader is fail-closed, but
    /// signing is a build step where stopping loudly is correct (PluginSignature.cs's own note).</summary>
    private sealed class UsageException(string message) : Exception(message);

    /// <summary>Parsed command line: <c>--key value</c> pairs; <c>--fragment</c> may repeat. Deliberately tiny
    /// and BCL-only (no System.CommandLine, no NuGet) so the build tool carries no dependency.</summary>
    private sealed class Args
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        public string Mode { get; private init; } = "";

        public static Args Parse(string[] args)
        {
            if (args.Length == 0) throw new UsageException("no arguments; expected a mode ('sign' or 'manifest')");
            var parsed = new Args { Mode = args[0] };
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"unexpected argument '{a}' (expected --key value)");
                var key = a[2..];
                if (i + 1 >= args.Length)
                    throw new UsageException($"--{key} needs a value");
                var value = args[++i];
                if (!parsed._values.TryGetValue(key, out var list))
                    parsed._values[key] = list = [];
                list.Add(value);
            }
            return parsed;
        }

        /// <summary>A single-valued option's last value, or null when absent.</summary>
        public string? Get(string key) =>
            _values.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null;

        /// <summary>Every value of a repeatable option (e.g. <c>--fragment</c>, <c>--hint-product</c>).</summary>
        public List<string> GetAll(string key) => _values.TryGetValue(key, out var list) ? list : [];

        /// <summary>A required option, erroring clearly when it is absent or blank.</summary>
        public string Require(string key) =>
            Get(key) is { Length: > 0 } value ? value : throw new UsageException($"--{key} is required");
    }
}
