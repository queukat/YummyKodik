// File: Kodik/KodikTitleResolver.cs

using System.Text.Json;
using System.Text.RegularExpressions;

namespace YummyKodik.Kodik
{
    /// <summary>
    /// Resolves Kodik ID and ID type for a given anime title using kodikapi.com/search.
    /// </summary>
    public static class KodikTitleResolver
    {
        private static readonly Regex NonWordRegex = new("[^\\p{L}\\p{Nd}]+", RegexOptions.Compiled);

        public static async Task<(KodikIdType IdType, string Id)> ResolveIdAsync(
            string slug,
            string title,
            KodikClient kodikClient,
            CancellationToken cancellationToken)
        {
            // We need HttpClient from the existing KodikClient via reflection is not nice,
            // so we simply create our own small HttpClient instance here for search.
            using var http = new HttpClient();

            var token = await KodikTokenProvider.GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
            var uri = $"https://kodikapi.com/search?token={Uri.EscapeDataString(token)}&title={Uri.EscapeDataString(title)}";

            using var resp = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                throw new KodikServiceException($"Kodik search failed: {resp.StatusCode}: {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                throw new KodikNoResultsException($"Kodik search returned no 'results' for title '{title}'.");
            }

            var normalizedTarget = Normalize(title);
            JsonElement? best = null;

            foreach (var item in resultsElement.EnumerateArray())
            {
                var itemTitle = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                if (Normalize(itemTitle) == normalizedTarget)
                {
                    best = item;
                    break;
                }

                best ??= item;
            }

            if (best is null)
            {
                throw new KodikNoResultsException($"Kodik search results are empty for title '{title}'.");
            }

            var idType = TryPickId(best.Value, out var id);
            if (!idType.HasValue || string.IsNullOrEmpty(id))
            {
                throw new KodikUnexpectedException($"Kodik search result for '{title}' does not contain usable ids.");
            }

            return (idType.Value, id);
        }

        private static string Normalize(string s) =>
            NonWordRegex.Replace(s ?? string.Empty, string.Empty).ToLowerInvariant();

        private static KodikIdType? TryPickId(JsonElement item, out string id)
        {
            id = string.Empty;

            if (item.TryGetProperty("shikimori_id", out var shikimori) &&
                shikimori.ValueKind != JsonValueKind.Null)
            {
                id = shikimori.GetRawText().Trim('"');
                return KodikIdType.Shikimori;
            }

            if (item.TryGetProperty("kinopoisk_id", out var kinopoisk) &&
                kinopoisk.ValueKind != JsonValueKind.Null)
            {
                id = kinopoisk.GetRawText().Trim('"');
                return KodikIdType.Kinopoisk;
            }

            if (item.TryGetProperty("imdb_id", out var imdb) &&
                imdb.ValueKind == JsonValueKind.String)
            {
                id = imdb.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(id))
                {
                    return KodikIdType.Imdb;
                }
            }

            return null;
        }
    }
}
