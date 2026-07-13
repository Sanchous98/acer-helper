using System;

namespace AcerHelper.Localization;

/// <summary>XAML markup extension: <c>{l:Tr 'English text'}</c> resolves to the current language's
/// translation of that text (the English source is the key — see <see cref="Loc"/>). It is evaluated when
/// the XAML is loaded, so it reflects whatever language was active when the window was built — exactly right
/// for the app's rebuild-on-switch model (a new window picks up the new language). Avalonia recognises the
/// class by its public <c>ProvideValue</c> method and strips the "Extension" suffix, hence <c>{l:Tr …}</c>.</summary>
public sealed class TrExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    /// <summary>The English source text to translate (the positional argument of the extension).</summary>
    public string Key { get; set; } = "";

    public object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
