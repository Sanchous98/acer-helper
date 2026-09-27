using System.Globalization;
using System.Resources;

namespace AcerHelper.Localization;

/// <summary>The app's UI language. <see cref="System"/> follows the OS UI culture.</summary>
public enum AppLanguage { System, English, Russian }

/// <summary>
/// The whole localization core, now on standard .resx resources.
///
/// THE MODEL. Translations live in <c>Localization/Strings.ru.resx</c>, compiled by the SDK into the usual
/// satellite (<c>ru/AcerHelper.resources.dll</c>) and read through a <see cref="ResourceManager"/>. The English
/// source text is itself the lookup key (gettext-style): <see cref="T(string)"/> asks the manager for the key
/// under the active culture, and a miss returns the English text unchanged — so the UI is never blank, and
/// adding a language is one more <c>Strings.&lt;culture&gt;.resx</c> with no code change. There is deliberately
/// NO neutral entry for a key (Localization/Strings.resx is empty), so "untranslated" and "returns the English
/// key" are the same fact rather than two copies that could drift.
///
/// WHY .resx IS SAFE HERE, THOUGH IT ONCE WAS NOT. This used to be a compiled-in dictionary because Native AOT
/// was believed not to load satellite resource assemblies (dotnet/runtime#86651) — a satellite that silently
/// fails to load ships English, which is exactly the failure the design was avoiding. That assumption was
/// re-measured on .NET 10 rather than trusted: tests/LocalizationAotProbe is a tiny app with a neutral and a
/// <c>ru</c> .resx, and .github/workflows/build.yml publishes it Native AOT (the shipping configuration) and
/// RUNS it, failing the build if the satellite does not load. It passes, so the conventional model is used.
///
/// A live language switch is handled by the app rebuilding its windows/tray/view-models (see AppController),
/// so strings are simply re-read on the next construction — no per-string change notification is needed. The
/// manager is created once; the culture is read from <see cref="CultureInfo.CurrentUICulture"/> at each lookup,
/// which the switch sets (see <see cref="Use"/>).
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

    /// <summary>Resolve and activate a language. <see cref="AppLanguage.System"/> maps to Russian on a Russian
    /// OS UI culture and English otherwise. Call once at startup (before any UI is built) and again on a live
    /// switch, before the UI is rebuilt.</summary>
    public static void Use(AppLanguage language)
    {
        Language = language;
        var effective = language == AppLanguage.System ? Detect() : language;
        _culture = effective == AppLanguage.Russian ? CultureInfo.GetCultureInfo("ru") : CultureInfo.InvariantCulture;
        // ResourceManager caches by culture internally; nothing else to do. A miss under `ru` falls back to the
        // (empty) neutral file and then to the key, which is the English text — the wanted behaviour.
    }

    private static AppLanguage Detect()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            ? AppLanguage.Russian
            : AppLanguage.English;

    /// <summary>The Russian translation of an English key, or null when the key has no row in
    /// <c>Strings.ru.resx</c>. FOR THE LOCALISATION GUARDS: they assert that a literal the UI shows has a
    /// translation, and null is precisely "no row" — the silent-English failure they exist to catch.</summary>
    public static string? Ru(string key) => Manager.GetString(key, Russian);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Translate <paramref name="text"/> (its English form is the key). An unknown key returns the
    /// text unchanged, so untranslated strings degrade to English rather than to blanks.</summary>
    public static string T(string text)
        => Manager.GetString(text, _culture) ?? text;

    /// <summary>Translate a composite/format string, then fill it with <paramref name="args"/>. The English
    /// key and the translation must share the same <c>{0}</c>… placeholders.</summary>
    public static string T(string text, params object?[] args)
        => string.Format(T(text), args);
}
