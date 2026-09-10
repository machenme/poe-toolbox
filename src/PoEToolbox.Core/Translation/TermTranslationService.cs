namespace PoEToolbox.Core.Translation;

public sealed record TermTranslationResult(
    string Translation,
    IReadOnlyList<TermTranslationSpan> TermSpans,
    int ProtectedTermCount,
    int ChunkCount);

public sealed class TermTranslationService
{
    private readonly BaseItemTermCatalog _catalog;
    private readonly EdgeTranslationClient _edgeClient;

    public TermTranslationService(BaseItemTermCatalog catalog, EdgeTranslationClient edgeClient)
    {
        _catalog = catalog;
        _edgeClient = edgeClient;
    }

    public async Task<TermTranslationResult> TranslateAsync(
        string sourceText,
        TranslationLanguage source,
        TranslationLanguage target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return new TermTranslationResult(string.Empty, [], 0, 0);

        var protector = new TermProtector(_catalog.GetPairs(source, target), source);
        var protectedText = protector.Protect(sourceText);
        var chunks = TextChunker.Split(protectedText.Text);
        var translatedChunks = await _edgeClient.TranslateBatchAsync(chunks, source, target, cancellationToken);
        var translated = string.Concat(translatedChunks);
        var restored = TermProtector.Restore(translated, protectedText);
        return new TermTranslationResult(
            restored.Text,
            restored.TermSpans,
            protectedText.MatchCount,
            chunks.Count);
    }
}
