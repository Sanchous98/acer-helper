using System.Globalization;

namespace AcerHelper.Localization;

/// <summary>The app's UI language. <see cref="System"/> follows the OS UI culture.</summary>
public enum AppLanguage { System, English, Russian }

/// <summary>
/// The whole localization core. Every language is compiled straight INTO the main assembly (a plain
/// in-memory string table) and selected at runtime — deliberately NOT via .resx satellite assemblies. The
/// shipping builds are Native AOT (see .github/workflows/build.yml), which does not reliably load satellite
/// resource assemblies (dotnet/runtime#86651), so the conventional ResourceManager+satellite approach would
/// work in a dev/JIT run yet silently fall back to English in the released AOT build. Embedding sidesteps
/// that entirely and behaves identically under JIT, AOT, single-file and trimming.
///
/// The English source text is itself the lookup key (gettext-style): a missing translation falls back to the
/// English text, so the UI is never blank and adding a language is just one more table. A live language
/// switch is handled by the app rebuilding its windows/tray/view-models (see AppController), so strings are
/// simply re-read on the next construction — no per-string change notification is needed.
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string>? _table;   // active non-English table; null = English

    /// <summary>The language as chosen (<see cref="AppLanguage.System"/> stays "System" — it is only resolved
    /// to a concrete language internally, so the setting round-trips).</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.English;

    /// <summary>Resolve and activate a language. <see cref="AppLanguage.System"/> maps to Russian on a Russian
    /// OS UI culture and English otherwise. Call once at startup (before any UI is built) and again on a live
    /// switch, before the UI is rebuilt.</summary>
    public static void Use(AppLanguage language)
    {
        Language = language;
        var effective = language == AppLanguage.System ? Detect() : language;
        _table = effective == AppLanguage.Russian ? Strings.Ru : null;
    }

    private static AppLanguage Detect()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            ? AppLanguage.Russian
            : AppLanguage.English;

    /// <summary>Translate <paramref name="text"/> (its English form is the key). An unknown key returns the
    /// text unchanged, so untranslated strings degrade to English rather than to blanks.</summary>
    public static string T(string text)
        => _table != null && _table.TryGetValue(text, out var v) ? v : text;

    /// <summary>Translate a composite/format string, then fill it with <paramref name="args"/>. The English
    /// key and the translation must share the same <c>{0}</c>… placeholders.</summary>
    public static string T(string text, params object?[] args)
        => string.Format(T(text), args);
}
