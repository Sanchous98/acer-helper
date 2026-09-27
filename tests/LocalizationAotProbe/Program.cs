using System.Globalization;
using System.Resources;

// The probe. It does exactly what a ResourceManager-based Loc would do at startup:
// create a manager over the neutral resource base name, force the culture, and read one
// string. Under Native AOT the satellite (ru/LocalizationAotProbe.resources.dll) either
// loads or it does not — and "does not" is the silent-English failure this whole exercise
// is about, so the exit code is the verdict.
//
// A plain dev run (JIT) always passes; only the AOT publish in the workflow makes this
// meaningful. That is why the workflow publishes THIS project with -p:PublishAot=true and
// runs the resulting exe.

var manager = new ResourceManager("LocalizationAotProbe.Strings", typeof(Program).Assembly);

CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("ru");

var russian = manager.GetString("Greeting", CultureInfo.GetCultureInfo("ru"));

Console.WriteLine($"ru Greeting = {russian ?? "<null>"}");

// 1) The satellite must have been found at all (a null means no .resources entry resolved).
// 2) It must be the RUSSIAN text, not an English fallback — a satellite that silently fails
//    to load resolves to the NEUTRAL string, which is exactly the bug; so comparing against
//    the neutral value is the real assertion, not just "non-null".
var neutral = manager.GetString("Greeting", CultureInfo.InvariantCulture);
if (russian is null)
{
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: no ru value resolved (satellite missing?).");
    return 2;
}
if (russian == neutral)
{
    Console.Error.WriteLine("AOT-RESX-PROBE FAIL: ru resolved to the NEUTRAL string — the satellite did not load.");
    return 3;
}
if (russian != "ПРИВЕТ-ИЗ-RESX")
{
    Console.Error.WriteLine($"AOT-RESX-PROBE FAIL: unexpected ru value '{russian}'.");
    return 4;
}

Console.WriteLine("AOT-RESX-PROBE OK: the ru satellite loaded under this build.");
return 0;
