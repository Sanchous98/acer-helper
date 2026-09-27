using AcerHelper.Localization;

// The probe. It drives the APP'S REAL Loc and the APP'S REAL .resx (see the csproj), published Native AOT —
// the shipping configuration. Under AOT the `ru` satellite either loads or it does not, and "does not" is the
// silent-English failure the whole question is about, so the exit code is the verdict.
//
// A plain dev run (JIT) passes too; only the AOT publish in the workflow makes this meaningful, which is why
// that step is the authority.

// 1) Force Russian and read a key that exists in Strings.ru.resx. Under AOT this is the satellite lookup.
Loc.Use(AppLanguage.Russian);
var options = Loc.T("nav.options");

Console.WriteLine($"ru 'nav.options' = {options}");

if (options != "Параметры")
{
    // Either the satellite did not load (Loc fell back to the neutral English) or the row is wrong. Both are
    // the exact silent-localisation failure this probe exists to catch.
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: 'nav.options' did not resolve to the Russian row — the ru " +
                            $"satellite did not load under Native AOT (got '{options}').");
    return 2;
}

// 2) A formatting key, to catch a satellite that loads but whose placeholders were mangled.
var profile = Loc.T("profile.status", "Тест");
if (profile != "Профиль: Тест")
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: format key resolved wrong ('{profile}').");
    return 3;
}

// 3) English must return the ENGLISH sentence from the NEUTRAL Strings.resx — proving the neutral file loads,
//    not just the satellite. (Before the neutral file carried entries this returned the key itself.)
Loc.Use(AppLanguage.English);
if (Loc.T("nav.options") != "Options")
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: English did not resolve to the neutral sentence ('{Loc.T("nav.options")}').");
    return 4;
}

// 4) A key with NO row in ANY file must return itself, never blank — the app's stated contract.
const string Missing = "no.such.key";
if (Loc.T(Missing) != Missing)
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: an unknown key did not fall back to itself ('{Loc.T(Missing)}').");
    return 5;
}

// 5) The guard hook must read the ru satellite DIRECTLY: an unknown key is null, never the neutral English.
//    (If the neutral text could stand in, the localisation guards could never fail.)
if (Loc.Ru(Missing) is not null || Loc.Ru("nav.options") is null)
{
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: Loc.Ru does not read the ru satellite directly.");
    return 6;
}

Console.WriteLine("AOT-RESX-PROBE OK: the app's ru satellite and neutral English both loaded under this build.");
return 0;
