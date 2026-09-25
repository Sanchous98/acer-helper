using System.Reflection;
using AcerHelper.Infrastructure.Vendors.Asus;
using AcerHelper.Localization;

namespace AcerHelper.Tests;

/// <summary>
/// Every Phase-3 user-facing sentence is a localization key that exists in the Russian table. The keys are read
/// off the message classes by reflection rather than listed here, so a message added without a translation
/// reddens this test instead of silently shipping English to the Russian build — the same rule Cardwire's and
/// HardwareAccess's consent tests keep.
/// </summary>
public class AsusPhase3LocalizationTests
{
    private static IEnumerable<string> Keys(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
               .Where(f => f.IsLiteral && f.FieldType == typeof(string))
               .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryPhase3MessageHasARussianEntry()
    {
        var keys = Keys(typeof(AsusArmouryMessages))
                   .Concat(Keys(typeof(AsusFanCurveMessages)))
                   .Distinct()
                   .ToArray();

        Assert.True(keys.Length >= 18, $"only {keys.Length} Phase-3 keys were collected — the extraction is broken");

        foreach (var key in keys)
            Assert.True(Strings.Ru.ContainsKey(key),
                "this Phase-3 message has no entry in Localization/Strings.Ru.cs, so the Russian build shows it "
                + "in English:\n  " + key);
    }
}
