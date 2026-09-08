using System.Windows.Markup;
using Drageon.Core;

namespace Drageon.App;

public static class Localization
{
    public static ILanguageService? Current { get; set; }

    public static string Get(string key) => Current?.Get(key) ?? key;
    public static string Get(string key, string fallback) => Current?.Get(key, fallback) ?? fallback;
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public string? Fallback { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Localization.Get(Key, Fallback ?? Key);
}
