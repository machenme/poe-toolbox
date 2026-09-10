namespace PoEToolbox.Core.Translation;

public static class TextChunker
{
    public const int MaxChunkLength = 3_500;

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var chunks = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(start + MaxChunkLength, text.Length);
            if (end < text.Length)
            {
                var preferred = FindPreferredBreak(text, start, end);
                end = preferred > start ? preferred : AvoidSplittingMarker(text, start, end);
            }

            chunks.Add(text[start..end]);
            start = end;
        }
        return chunks;
    }

    private static int FindPreferredBreak(string text, int start, int end)
    {
        for (var index = end - 1; index > start; index--)
        {
            if (text[index] is '\r' or '\n' or ' ' or '.' or '!' or '?' or ';' or ':' or '。' or '！' or '？' or '；' or '：')
                return index + 1;
        }
        return start;
    }

    private static int AvoidSplittingMarker(string text, int start, int end)
    {
        var markerStart = text.LastIndexOf(TermProtector.TokenPrefix, end - 1, StringComparison.Ordinal);
        if (markerStart < start)
            return end;

        var markerEnd = text.IndexOf(
            TermProtector.TokenSuffix,
            markerStart + TermProtector.TokenPrefix.Length,
            StringComparison.Ordinal);
        if (markerEnd < 0 || markerEnd + TermProtector.TokenSuffix.Length <= end)
            return end;

        return markerStart > start ? markerStart : markerEnd + TermProtector.TokenSuffix.Length;
    }
}
