using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed partial class BaseItemTermCatalog
{
    private const string ResourceName = "PoEToolbox.Core.Assets.Translation.base_item_terms.json";
    private readonly IReadOnlyList<BaseItemTerm> _terms;

    /// <summary>
    /// 源生成上下文：反射序列化在 Native AOT 下被禁用（运行时抛 InvalidOperationException）。
    /// 原来是不带 options 的 <c>Deserialize&lt;T&gt;(stream)</c>（默认选项），这里传一份默认
    /// <see cref="JsonSerializerOptions"/> 实例，保持与改动前完全一致的读法。
    /// ⚠️ <c>base_item_terms.json</c> 的字段名是 PascalCase（与 <see cref="BaseItemTerm"/>
    /// 的构造参数名一致）⇒ 不要加 camelCase 命名策略，否则读不出来。
    /// </summary>
    [JsonSerializable(typeof(List<BaseItemTerm>))]
    internal sealed partial class BaseItemTermCatalogJsonContext : JsonSerializerContext;

    private static readonly BaseItemTermCatalogJsonContext JsonTypeInfo = new(new JsonSerializerOptions());

    private BaseItemTermCatalog(IReadOnlyList<BaseItemTerm> terms) => _terms = terms;

    public static BaseItemTermCatalog LoadDefault()
    {
        using var stream = typeof(BaseItemTermCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded translation dictionary: {ResourceName}");
        var terms = JsonSerializer.Deserialize(stream, JsonTypeInfo.ListBaseItemTerm)
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
