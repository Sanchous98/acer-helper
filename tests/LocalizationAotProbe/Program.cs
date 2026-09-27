using AcerHelper.Localization;

// The probe. It drives the APP'S REAL Loc and the APP'S REAL .resx (see the csproj), published Native AOT —
// the shipping configuration. Under AOT the `ru` satellite either loads or it does not, and "does not" is the
// silent-English failure the whole question is about, so the exit code is the verdict.
//
// A plain dev run (JIT) passes too; only the AOT publish in the workflow makes this meaningful, which is why
// that step is the authority.

// 1) Force Russian and read a key that exists in Strings.ru.resx. Under AOT this is the satellite lookup.
Loc.Use(AppLanguage.Russian);
var options = Loc.T("Options");

Console.WriteLine($"ru 'Options' = {options}");

if (options != "Параметры")
{
    // Either the satellite did not load (Loc fell back to the English key) or the row is wrong. Both are the
    // exact silent-localisation failure this probe exists to catch.
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: 'Options' did not resolve to the Russian row — the ru satellite " +
                            $"did not load under Native AOT (got '{options}').");
    return 2;
}

// 2) A formatting key, to catch a satellite that loads but whose placeholders were mangled.
Loc.Use(AppLanguage.Russian);
var profile = Loc.T("Profile: {0}", "Тест");
if (profile != "Профиль: Тест")
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: format key resolved wrong ('{profile}').");
    return 3;
}

// 3) A key with NO row must fall back to the English source text, never blank — the app's stated contract.
Loc.Use(AppLanguage.Russian);
const string Missing = "no-such-key-xyz";
if (Loc.T(Missing) != Missing)
{
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: an unknown key did not fall back to its English text.");
    return 4;
}

// 4) English must return the key itself (no neutral rows, by design).
Loc.Use(AppLanguage.English);
if (Loc.T("Options") != "Options")
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: English did not return the key ('{Loc.T("Options")}').");
    return 5;
}

Console.WriteLine("AOT-RESX-PROBE OK: the app's ru satellite loaded under this build.");
return 0;
