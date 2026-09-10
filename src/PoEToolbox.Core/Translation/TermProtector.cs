using System.Text;
using System.Text.RegularExpressions;

namespace PoEToolbox.Core.Translation;

public sealed record ProtectedText(string Text, IReadOnlyDictionary<string, string> Replacements, int MatchCount);
public sealed record TermTranslationSpan(int Start, int Length);
public sealed record RestoredText(string Text, IReadOnlyList<TermTranslationSpan> TermSpans);

/// <summary>Protects known game terms from machine translation and restores their exact target names afterwards.</summary>
public sealed class TermProtector
{
    internal const string TokenPrefix = "ZZPOETERM";
    internal const string TokenSuffix = "ZZ";
    private static readonly Regex PlaceholderPattern = new(@"ZZPOETERM\d{6}ZZ", RegexOptions.CultureInvariant);
    private readonly TrieNode _root = new();
    private readonly TranslationLanguage _sourceLanguage;

    public TermProtector(IEnumerable<TermPair> pairs, TranslationLanguage sourceLanguage)
    {
        _sourceLanguage = sourceLanguage;
        foreach (var pair in pairs)
            Add(pair);
    }

    public ProtectedText Protect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var output = new StringBuilder(text.Length);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var matchNumber = 0;

        for (var index = 0; index < text.Length;)
        {
            var match = FindLongestMatch(text, index);
            if (match is null)
            {
                output.Append(text[index++]);
                continue;
            }

            var token = $"{TokenPrefix}{matchNumber++:D6}{TokenSuffix}";
            output.Append(token);
            replacements.Add(token, match.Target);
            index += match.Length;
        }

        return new ProtectedText(output.ToString(), replacements, matchNumber);
    }

    public static RestoredText Restore(string translatedText, ProtectedText protectedText)
    {
        ArgumentNullException.ThrowIfNull(translatedText);

        var restored = new StringBuilder(translatedText.Length);
        var spans = new List<TermTranslationSpan>(protectedText.Replacements.Count);
        var cursor = 0;
        foreach (System.Text.RegularExpressions.Match match in PlaceholderPattern.Matches(translatedText))
        {
            restored.Append(translatedText, cursor, match.Index - cursor);
            if (!protectedText.Replacements.TryGetValue(match.Value, out var target))
                throw new TranslationException("The translation response contains an unknown game-term marker.");

            spans.Add(new TermTranslationSpan(restored.Length, target.Length));
            restored.Append(target);
            cursor = match.Index + match.Length;
        }
        restored.Append(translatedText, cursor, translatedText.Length - cursor);

        if (spans.Count != protectedText.Replacements.Count)
            throw new TranslationException("The translation service changed a protected game-term marker.");

        return new RestoredText(restored.ToString(), spans);
    }

    private void Add(TermPair pair)
    {
        var node = _root;
        foreach (var character in pair.Source)
        {
            var key = Normalize(character);
            if (!node.Children.TryGetValue(key, out var next))
            {
                next = new TrieNode();
                node.Children.Add(key, next);
            }
            node = next;
        }

        node.Target ??= pair.Target;
        node.SourceLength = pair.Source.Length;
    }

    private TermMatch? FindLongestMatch(string text, int start)
    {
        var node = _root;
        TermMatch? longest = null;
        for (var index = start; index < text.Length; index++)
        {
            if (!node.Children.TryGetValue(Normalize(text[index]), out node))
                break;

            if (node.Target is not null
                && IsBoundaryMatch(text, start, index + 1))
            {
                longest = new TermMatch(node.SourceLength, node.Target);
            }
        }
        return longest;
    }

    private bool IsBoundaryMatch(string text, int start, int end)
    {
        if (_sourceLanguage != TranslationLanguage.English)
            return true;

        return (start == 0 || !IsAsciiWord(text[start - 1]))
            && (end == text.Length || !IsAsciiWord(text[end]));
    }

    private static char Normalize(char character) => char.ToUpperInvariant(character);
    private static bool IsAsciiWord(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode> Children { get; } = [];
        public string? Target { get; set; }
        public int SourceLength { get; set; }
    }

    private sealed record TermMatch(int Length, string Target);
}
