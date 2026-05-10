using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YummyKodik.Shikimori;

public sealed class ShikimoriGraphQlClient
{
    private const string DefaultEndpoint = "https://shikimori.io/api/graphql";

    private const string AnimeQuery = """
                                      query GetAnime($ids: String!) {
                                        animes(ids: $ids) {
                                          id
                                          name
                                          russian
                                          kind
                                          related {
                                            relationKind
                                            anime {
                                              id
                                              name
                                              russian
                                              kind
                                            }
                                          }
                                        }
                                      }
                                      """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly Dictionary<long, ShikimoriAnimeResponse?> _animeCache = new();

    public ShikimoriGraphQlClient(HttpClient httpClient, string? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
    }

    public async Task<ShikimoriSeriesLayoutInfo?> TryResolveSeriesLayoutAsync(
        long shikimoriId,
        CancellationToken cancellationToken = default)
    {
        if (shikimoriId <= 0)
        {
            return null;
        }

        var currentToRoot = new List<ShikimoriSeriesLayoutNode>();
        var seen = new HashSet<long>();
        var currentId = shikimoriId;

        while (currentId > 0 && seen.Add(currentId))
        {
            var anime = await GetAnimeAsync(currentId, cancellationToken).ConfigureAwait(false);
            if (anime == null)
            {
                break;
            }

            currentToRoot.Add(ToLayoutNode(anime));

            var prequelId = anime.Related?
                .Where(x => string.Equals(x.RelationKind, "prequel", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Anime)
                .Where(x => x != null && ShikimoriSeriesLayoutResolver.IsMainlineKind(x.Kind))
                .Select(x => ParseId(x!.Id))
                .FirstOrDefault(x => x > 0) ?? 0;

            if (prequelId <= 0)
            {
                break;
            }

            currentId = prequelId;
        }

        if (currentToRoot.Count == 0)
        {
            return null;
        }

        currentToRoot.Reverse();
        return ShikimoriSeriesLayoutResolver.BuildFromMainlineChain(currentToRoot);
    }

    private async Task<ShikimoriAnimeResponse?> GetAnimeAsync(long shikimoriId, CancellationToken cancellationToken)
    {
        if (_animeCache.TryGetValue(shikimoriId, out var cached))
        {
            return cached;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                query = AnimeQuery,
                variables = new
                {
                    ids = shikimoriId.ToString(CultureInfo.InvariantCulture)
                }
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shikimori GraphQL request failed. status={(int)response.StatusCode} body={TrimForLog(body)}");
        }

        var envelope = JsonSerializer.Deserialize<GraphQlEnvelope>(body, JsonOptions);
        if (envelope?.Errors?.Count > 0)
        {
            throw new InvalidOperationException(
                "Shikimori GraphQL returned errors: " +
                string.Join("; ", envelope.Errors.Select(x => x.Message).Where(x => !string.IsNullOrWhiteSpace(x))));
        }

        var anime = envelope?.Data?.Animes?.FirstOrDefault();
        _animeCache[shikimoriId] = anime;
        return anime;
    }

    private static ShikimoriSeriesLayoutNode ToLayoutNode(ShikimoriAnimeResponse anime)
    {
        return new ShikimoriSeriesLayoutNode
        {
            Id = ParseId(anime.Id),
            RussianTitle = anime.Russian ?? string.Empty,
            Name = anime.Name ?? string.Empty,
            Kind = anime.Kind ?? string.Empty
        };
    }

    private static long ParseId(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static string TrimForLog(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 400 ? normalized : normalized[..400];
    }

    private sealed class GraphQlEnvelope
    {
        [JsonPropertyName("data")]
        public GraphQlData? Data { get; set; }

        [JsonPropertyName("errors")]
        public List<GraphQlError>? Errors { get; set; }
    }

    private sealed class GraphQlData
    {
        [JsonPropertyName("animes")]
        public List<ShikimoriAnimeResponse>? Animes { get; set; }
    }

    private sealed class GraphQlError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class ShikimoriAnimeResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("russian")]
        public string Russian { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("related")]
        public List<ShikimoriRelatedResponse>? Related { get; set; }
    }

    private sealed class ShikimoriRelatedResponse
    {
        [JsonPropertyName("relationKind")]
        public string RelationKind { get; set; } = string.Empty;

        [JsonPropertyName("anime")]
        public ShikimoriRelatedAnimeResponse? Anime { get; set; }
    }

    private sealed class ShikimoriRelatedAnimeResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("russian")]
        public string Russian { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;
    }
}
