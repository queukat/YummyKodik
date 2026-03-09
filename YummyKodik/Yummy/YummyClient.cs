// File: Yummy/YummyClient.cs

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YummyKodik.Yummy
{
    /// <summary>
    /// Minimal YummyAnime (api.yani.tv) client.
    /// Supports public metadata endpoints and private user endpoints (lists) with Bearer token.
    /// </summary>
    public sealed class YummyClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _clientId;
        private readonly string _baseUrl;

        private string _accessToken = string.Empty;

        public YummyClient(HttpClient httpClient, string? clientId, string baseUrl)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _clientId = clientId?.Trim() ?? string.Empty;
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        public void SetAccessToken(string? token)
        {
            _accessToken = token?.Trim() ?? string.Empty;
        }

        public string GetAccessToken() => _accessToken;

        /// <summary>
        /// Gets anime metadata by alias or numeric ID.
        /// Uses GET /anime/{url}?need_videos=false
        /// Retries transient upstream errors (including Cloudflare 522) a few times.
        /// </summary>
        public async Task<YummyAnimeResponse> GetAnimeAsync(
            string urlOrAliasOrId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(urlOrAliasOrId))
            {
                throw new ArgumentException("Anime url/alias/id must be non-empty.", nameof(urlOrAliasOrId));
            }

            var key = ExtractSlugOrId(urlOrAliasOrId);
            var requestUri = $"{_baseUrl}/anime/{Uri.EscapeDataString(key)}?need_videos=false";

            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                ApplyHeaders(request, includeBearer: false);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var wrapper = JsonSerializer.Deserialize<YummyAnimeGetWrapper>(content, options);

                    if (wrapper?.Response == null)
                    {
                        throw new InvalidOperationException("Yummy API response does not contain 'response' object.");
                    }

                    return wrapper.Response;
                }

                var code = (int)response.StatusCode;

                // retry for transient errors
                // NOTE: HttpStatusCode doesn't have a named constant for 522, so we check int.
                if ((code == 522 || code == 504 || code == 503 || code == 502) && attempt < 2)
                {
                    var delaySeconds = 2 * (attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ThrowTypedError(response.StatusCode, content);
            }

            throw new InvalidOperationException("Unreachable.");
        }

        /// <summary>
        /// Gets user list items. Requires Authorization Bearer token.
        /// GET /users/{id}/lists/{list_id}
        /// </summary>
        public async Task<IReadOnlyList<YummyUserListItem>> GetUserListAsync(
            int userId,
            int listId,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "UserId must be positive.");
            }

            if (listId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(listId), "ListId must be non negative.");
            }

            var requestUri = $"{_baseUrl}/users/{userId}/lists/{listId}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            ApplyHeaders(request, includeBearer: true);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ThrowTypedError(response.StatusCode, content);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<YummyUserListWrapper>(content, options);

            return wrapper?.Response ?? new List<YummyUserListItem>(0);
        }

        /// <summary>
        /// Login and obtain user access token.
        /// POST /profile/login
        /// </summary>
        public async Task<string> LoginAsync(
            string login,
            string password,
            string? recaptchaResponse = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                throw new ArgumentException("Login must be non-empty.", nameof(login));
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Password must be non-empty.", nameof(password));
            }

            var requestUri = $"{_baseUrl}/profile/login";

            var payload = new YummyLoginRequest
            {
                NeedJson = true,
                Login = login.Trim(),
                Password = password,
                RecaptchaResponse = recaptchaResponse?.Trim() ?? string.Empty
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(payload)
            };

            ApplyHeaders(request, includeBearer: false);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ThrowTypedError(response.StatusCode, content);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<YummyLoginWrapper>(content, options);

            if (wrapper?.Response == null || !wrapper.Response.Success || string.IsNullOrWhiteSpace(wrapper.Response.Token))
            {
                throw new InvalidOperationException("Yummy login did not return a valid token.");
            }

            _accessToken = wrapper.Response.Token.Trim();
            return _accessToken;
        }

        /// <summary>
        /// Refresh user access token.
        /// GET /profile/token
        /// Requires Authorization Bearer token.
        /// </summary>
        public async Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new InvalidOperationException("Access token is not set, cannot refresh.");
            }

            var requestUri = $"{_baseUrl}/profile/token";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            ApplyHeaders(request, includeBearer: true);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ThrowTypedError(response.StatusCode, content);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<YummyTokenWrapper>(content, options);

            var token = wrapper?.Response?.Token?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Yummy token refresh did not return a valid token.");
            }

            _accessToken = token;
            return _accessToken;
        }

        private void ApplyHeaders(HttpRequestMessage request, bool includeBearer)
        {
            if (!string.IsNullOrEmpty(_clientId))
            {
                request.Headers.TryAddWithoutValidation("X-Application", _clientId);
            }

            if (includeBearer && !string.IsNullOrWhiteSpace(_accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            }
        }

        private static void ThrowTypedError(HttpStatusCode status, string content)
        {
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException($"Yummy API returned {status}: {content}");
            }

            throw new InvalidOperationException($"Yummy API returned {status}: {content}");
        }

        public static string ExtractSlugOrId(string urlOrSlugOrId)
        {
            var s = (urlOrSlugOrId ?? string.Empty).Trim();
            s = s.Trim('"', '\'', '“', '”');

            if (s.Length == 0)
            {
                return string.Empty;
            }

            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            {
                return s.Trim('/');
            }

            var qFromQuery = TryGetQueryParam(uri.Query, "q");
            if (!string.IsNullOrWhiteSpace(qFromQuery))
            {
                return qFromQuery.Trim().Trim('/');
            }

            var path = uri.AbsolutePath ?? string.Empty;
            var idxAnime = path.IndexOf("/anime/", StringComparison.OrdinalIgnoreCase);
            if (idxAnime >= 0)
            {
                var sub = path.Substring(idxAnime + "/anime/".Length);
                return sub.Trim('/');
            }

            var idxItem = path.IndexOf("/item/", StringComparison.OrdinalIgnoreCase);
            if (idxItem >= 0)
            {
                var sub = path.Substring(idxItem + "/item/".Length);
                return sub.Trim('/');
            }

            var trimmedPath = path.Trim('/');
            if (trimmedPath.Length == 0)
            {
                return string.Empty;
            }

            var lastSlash = trimmedPath.LastIndexOf('/');
            return lastSlash >= 0 ? trimmedPath.Substring(lastSlash + 1) : trimmedPath;
        }

        public static string PickBestPosterUrl(YummyAnimeResponse anime)
        {
            var poster = anime.Poster;
            if (poster == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(poster.Fullsize))
            {
                return NormalizePosterUrl(poster.Fullsize);
            }

            if (!string.IsNullOrEmpty(poster.Big))
            {
                return NormalizePosterUrl(poster.Big);
            }

            if (!string.IsNullOrEmpty(poster.Mega))
            {
                return NormalizePosterUrl(poster.Mega);
            }

            if (!string.IsNullOrEmpty(poster.Huge))
            {
                return NormalizePosterUrl(poster.Huge);
            }

            if (!string.IsNullOrEmpty(poster.Medium))
            {
                return NormalizePosterUrl(poster.Medium);
            }

            if (!string.IsNullOrEmpty(poster.Small))
            {
                return NormalizePosterUrl(poster.Small);
            }

            return string.Empty;
        }

        private static string NormalizePosterUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + trimmed;
            }

            return trimmed;
        }

        private static string? TryGetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            var q = query;
            if (q.StartsWith("?", StringComparison.Ordinal))
            {
                q = q.Substring(1);
            }

            if (q.Length == 0)
            {
                return null;
            }

            var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var k = part.Substring(0, eq);
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var v = part.Substring(eq + 1);
                if (v.Length == 0)
                {
                    return string.Empty;
                }

                return Uri.UnescapeDataString(v.Replace("+", " ", StringComparison.Ordinal));
            }

            return null;
        }
    }
}
