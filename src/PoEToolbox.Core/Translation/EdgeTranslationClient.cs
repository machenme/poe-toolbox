using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PoEToolbox.Core.Translation;

public sealed class TranslationException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class EdgeTranslationClient
{
    private const string Endpoint = "https://edge.microsoft.com/translate/translatetext";
    private const int MaxBatchSize = 50;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36" } },
    };
    private static readonly SemaphoreSlim RequestScheduleLock = new(1, 1);
    private static DateTimeOffset _nextRequestAtUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationLanguage source,
        TranslationLanguage target,
        CancellationToken cancellationToken)
    {
        if (source == target)
            throw new TranslationException("Source and target languages must differ.");

        var results = new List<string>(texts.Count);
        foreach (var batch in texts.Chunk(MaxBatchSize))
            results.AddRange(await RequestBatchAsync(batch, source, target, cancellationToken));
        return results;
    }

    private static async Task<IReadOnlyList<string>> RequestBatchAsync(
        IReadOnlyList<string> texts,
        TranslationLanguage source,
        TranslationLanguage target,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForRequestTurnAsync(cancellationToken);

            try
            {
                var uri = $"{Endpoint}?from={source.ToEdgeCode()}&to={target.ToEdgeCode()}&isEnterpriseClient=false";
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(JsonSerializer.Serialize(texts), Encoding.UTF8, "application/json"),
                };
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await Http.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == 3)
                        throw new TranslationException("Edge translation was rate-limited (HTTP 429).");
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new TranslationException($"Edge translation failed with HTTP {(int)response.StatusCode}.");

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                return ParseResponse(content, texts.Count);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException("Unable to reach the Edge translation service.", ex);
            }
        }

        throw new TranslationException("Edge translation failed after retrying.");
    }

    private static async Task WaitForRequestTurnAsync(CancellationToken cancellationToken)
    {
        await RequestScheduleLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var requestAt = _nextRequestAtUtc > now ? _nextRequestAtUtc : now;
            _nextRequestAtUtc = requestAt.AddSeconds(3);
            var wait = requestAt - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
        }
        finally
        {
            RequestScheduleLock.Release();
        }
    }

    private static IReadOnlyList<string> ParseResponse(Stream stream, int expectedCount)
    {
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != expectedCount)
            throw new TranslationException("Edge translation returned an unexpected number of results.");

        var translations = new List<string>(expectedCount);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("translations", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() == 0
                || !values[0].TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                throw new TranslationException("Edge translation returned an invalid response.");
            }
            translations.Add(text.GetString()!);
        }
        return translations;
    }
}
