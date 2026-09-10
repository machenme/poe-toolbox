namespace PoEToolbox.Core.Translation;

public enum TranslationLanguage
{
    English,
    SimplifiedChinese,
    TraditionalChinese,
}

public static class TranslationLanguageExtensions
{
    public static string ToEdgeCode(this TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => "en",
        TranslationLanguage.SimplifiedChinese => "zh-Hans",
        TranslationLanguage.TraditionalChinese => "zh-Hant",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}
