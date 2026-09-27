using System.Globalization;
using System.Resources;

namespace AcerHelper.Localization;

/// <summary>The app's UI language. <see cref="System"/> follows the OS UI culture.</summary>
public enum AppLanguage { System, English, Russian }

/// <summary>
/// The whole localization core, on standard .resx resources.
///
/// THE MODEL. Call sites name a <b>neutral key</b> (<c>nav.options</c>, <c>uv.confirm_body</c>, …) and
/// <see cref="T(string)"/> resolves it. The two files are the two ends of that key:
/// <list type="bullet">
/// <item><c>Localization/Strings.resx</c> — the NEUTRAL file, which holds the English sentence for every key.
/// It is also the fallback: a key missing from a language falls back here, so the UI is never blank.</item>
/// <item><c>Localization/Strings.ru.resx</c> — the Russian translation, compiled by the SDK into the satellite
/// (<c>ru/AcerHelper.resources.dll</c>).</item>
/// </list>
/// A key that exists in NEITHER file returns itself (a visible <c>nav.foo</c> is a bug that shows the key rather
/// than a blank), and adding a language is one more <c>Strings.&lt;culture&gt;.resx</c> with no code change. The
/// keys are deliberately symbolic rather than the English text (which they used to be): a key is stable while
/// the English wording is edited, so a copy change no longer renames a resource, and the neutral file is the one
/// readable home of the English.
///
/// WHY .resx IS SAFE HERE, THOUGH IT ONCE WAS NOT. This used to be a compiled-in dictionary because Native AOT
/// was believed not to load satellite resource assemblies (dotnet/runtime#86651) — a satellite that silently
/// fails to load ships English, which is exactly the failure the design was avoiding. That assumption was
/// re-measured on .NET 10 rather than trusted: tests/LocalizationAotProbe drives the app's REAL <see cref="Loc"/>
/// and REAL .resx, and .github/workflows/build.yml publishes it Native AOT (the shipping configuration) and
/// RUNS it, failing the build if the satellite does not load. It passes, so the conventional model is used.
///
/// A live language switch is handled by the app rebuilding its windows/tray/view-models (see AppController),
/// so strings are simply re-read on the next construction — no per-string change notification is needed. The
/// manager is created once; the active culture is held as a FIELD (see <see cref="_culture"/>), set by
/// <see cref="Use"/>.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager =
        new("AcerHelper.Localization.Strings", typeof(Loc).Assembly);

    /// <summary>The language as chosen (<see cref="AppLanguage.System"/> stays "System" — it is only resolved
    /// to a concrete language internally, so the setting round-trips).</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.English;

    /// <summary>The culture lookups run under, held as a FIELD rather than read from
    /// <see cref="CultureInfo.CurrentUICulture"/>. CurrentUICulture is per-THREAD, and this app looks strings up
    /// from more than one thread (the refresh pass composes status messages off the UI thread), so a thread-local
    /// culture would let a background thread render in a different language than the UI — a regression the old
    /// single shared table could not have. The value is written only by <see cref="Use"/> on the UI thread at
    /// startup and on a live switch, and read on every lookup; the reference assignment is atomic, so no lock is
    /// needed for the one fact it carries.</summary>
    private static CultureInfo _culture = CultureInfo.InvariantCulture;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Resolve and activate a language. <see cref="AppLanguage.System"/> maps to Russian on a Russian
    /// OS UI culture and English otherwise. Call once at startup (before any UI is built) and again on a live
    /// switch, before the UI is rebuilt.</summary>
    public static void Use(AppLanguage language)
    {
        Language = language;
        var effective = language == AppLanguage.System ? Detect() : language;
        // English is the invariant culture, i.e. the NEUTRAL Strings.resx — that file IS the English. Russian is
        // its own culture, which resolves the ru satellite and falls back to the neutral English for any key.
        _culture = effective == AppLanguage.Russian ? Russian : CultureInfo.InvariantCulture;
        // ResourceManager caches by culture internally; nothing else to do.
    }

    private static AppLanguage Detect()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            ? AppLanguage.Russian
            : AppLanguage.English;

    /// <summary>The RUSSIAN row for a key, or null when <c>Strings.ru.resx</c> has none. FOR THE LOCALISATION
    /// GUARDS: they assert that a literal the UI shows has a Russian translation, and null is precisely "no
    /// row". It reads the ru resource set DIRECTLY, with parent fallback disabled — the neutral English file
    /// must not stand in for a missing Russian row, or the guard could never fail.</summary>
    public static string? Ru(string key)
        => Manager.GetResourceSet(Russian, createIfNotExists: true, tryParents: false)?.GetString(key);

    /// <summary>Translate a neutral key. A key with no entry in the active language falls back to the neutral
    /// English, and a key in no file at all returns itself — never blank.</summary>
    public static string T(string key)
        => Manager.GetString(key, _culture) ?? key;

    /// <summary>Translate a neutral key, then fill it with <paramref name="args"/>. The English and the
    /// translation must share the same <c>{0}</c>… placeholders.</summary>
    public static string T(string key, params object?[] args)
        => string.Format(T(key), args);
}
