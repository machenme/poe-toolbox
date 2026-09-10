using System.Text.Json;

namespace PoEToolbox.Core.Translation;

public sealed record BaseItemTerm(
    string Id,
    string English,
    string SimplifiedChinese,
    string TraditionalChinese)
{
    public string GetName(TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => English,
        TranslationLanguage.SimplifiedChinese => SimplifiedChinese,
        TranslationLanguage.TraditionalChinese => TraditionalChinese,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}

public sealed record TermPair(string Source, string Target);

public sealed class BaseItemTermCatalog
{
    private const string ResourceName = "PoEToolbox.Core.Assets.Translation.base_item_terms.json";
    private readonly IReadOnlyList<BaseItemTerm> _terms;

    private BaseItemTermCatalog(IReadOnlyList<BaseItemTerm> terms) => _terms = terms;

    public static BaseItemTermCatalog LoadDefault()
    {
        using var stream = typeof(BaseItemTermCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded translation dictionary: {ResourceName}");
        var terms = JsonSerializer.Deserialize<List<BaseItemTerm>>(stream)
            ?? throw new InvalidOperationException("The embedded translation dictionary is empty.");
        return new BaseItemTermCatalog(terms);
    }

    public IReadOnlyList<TermPair> GetPairs(TranslationLanguage source, TranslationLanguage target)
    {
        if (source == target)
            throw new ArgumentException("Source and target languages must differ.");

        return _terms
            .Select(term => new TermPair(term.GetName(source), term.GetName(target)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Source) && !string.IsNullOrWhiteSpace(pair.Target))
            .GroupBy(pair => pair.Source, StringComparer.OrdinalIgnoreCase)
            // A source phrase with conflicting target names cannot be resolved from copied text alone.
            .Where(group => group.Select(pair => pair.Target).Distinct(StringComparer.Ordinal).Take(2).Count() == 1)
            .Select(group => group.First())
            .ToList();
    }
}
