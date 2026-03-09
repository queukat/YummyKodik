// File: Kodik/KodikTokenProvider.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Kodik
{
    public static class KodikTokenProvider
    {
        // GitHub raw with current token obfuscation
        private const string OnlineModUrl =
            "https://raw.githubusercontent.com/nb557/plugins/refs/heads/main/online_mod.js";

        // Fallback token (may be limited)
        private const string FallbackScriptUrl =
            "https://kodik-add.com/add-players.min.js?v=2";

        // In-memory cache
        private static readonly SemaphoreSlim CacheGate = new(1, 1);
        private static string? _cachedToken;
        private static DateTimeOffset _cachedUntil;

        private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(12);

        private static ILogger? Logger => global::YummyKodik.Plugin.Instance?.Logger;

        /// <summary>
        /// Tries to obtain Kodik token. Uses in-memory cache by default.
        /// Throws KodikTokenException if token can't be obtained and no cached token available.
        /// </summary>
        public static async Task<string> GetTokenAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false,
            TimeSpan? cacheTtl = null,
            bool allowStaleOnFailure = true)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            var ttl = cacheTtl ?? DefaultCacheTtl;
            var now = DateTimeOffset.UtcNow;

            // Fast path (no lock)
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedToken) && now < _cachedUntil)
            {
                Logger?.LogInformation("KodikTokenProvider cache hit. expiresAtUtc={ExpiresAtUtc}", _cachedUntil);
                return _cachedToken!;
            }

            Logger?.LogInformation(
                "KodikTokenProvider resolving token. forceRefresh={ForceRefresh} allowStaleOnFailure={AllowStale}",
                forceRefresh,
                allowStaleOnFailure);

            await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check under lock
                now = DateTimeOffset.UtcNow;
                if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedToken) && now < _cachedUntil)
                {
                    Logger?.LogInformation("KodikTokenProvider cache hit (locked). expiresAtUtc={ExpiresAtUtc}", _cachedUntil);
                    return _cachedToken!;
                }

                try
                {
                    var fresh = await GetTokenNoCacheAsync(httpClient, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(fresh))
                    {
                        throw new KodikTokenException("Token resolved as empty.");
                    }

                    _cachedToken = fresh.Trim();
                    _cachedUntil = DateTimeOffset.UtcNow.Add(ttl);

                    Logger?.LogInformation(
                        "KodikTokenProvider token resolved and cached. ttlHours={TtlHours} expiresAtUtc={ExpiresAtUtc} tokenLen={Len}",
                        ttl.TotalHours,
                        _cachedUntil,
                        _cachedToken.Length);

                    return _cachedToken!;
                }
                catch (Exception ex)
                {
                    // If allowed, return stale token (better than failing the whole plugin)
                    if (allowStaleOnFailure && !string.IsNullOrWhiteSpace(_cachedToken))
                    {
                        Logger?.LogWarning(
                            ex,
                            "KodikTokenProvider failed to refresh token, using stale cached token. cachedExpiresAtUtc={ExpiresAtUtc}",
                            _cachedUntil);

                        return _cachedToken!;
                    }

                    Logger?.LogWarning(ex, "KodikTokenProvider failed to resolve token and no cached token can be used.");
                    throw;
                }
            }
            finally
            {
                CacheGate.Release();
            }
        }

        /// <summary>
        /// Safe variant: never throws. Returns Success=false with Error message.
        /// Uses cache unless forceRefresh=true.
        /// </summary>
        public static async Task<(bool Success, string? Token, string? Error)> TryGetTokenAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false,
            TimeSpan? cacheTtl = null,
            bool allowStaleOnFailure = true)
        {
            try
            {
                var token = await GetTokenAsync(
                        httpClient,
                        cancellationToken,
                        forceRefresh: forceRefresh,
                        cacheTtl: cacheTtl,
                        allowStaleOnFailure: allowStaleOnFailure)
                    .ConfigureAwait(false);

                return (true, token, null);
            }
            catch (Exception ex)
            {
                // Don't leak giant stacktraces into logs unless caller wants it
                Logger?.LogWarning(ex, "KodikTokenProvider.TryGetTokenAsync failed.");
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Drops current cached token (next call will re-fetch).
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedToken = null;
            _cachedUntil = default;
            Logger?.LogInformation("KodikTokenProvider cache invalidated.");
        }

        /// <summary>
        /// Returns current cached token if present and not expired (no IO).
        /// </summary>
        public static bool TryGetCachedToken(out string? token)
        {
            token = null;

            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedToken) && now < _cachedUntil)
            {
                token = _cachedToken;
                Logger?.LogInformation("KodikTokenProvider TryGetCachedToken hit. expiresAtUtc={ExpiresAtUtc}", _cachedUntil);
                return true;
            }

            Logger?.LogInformation("KodikTokenProvider TryGetCachedToken miss.");
            return false;
        }

        // =========================
        // Actual fetching (no cache)
        // =========================

        private static async Task<string> GetTokenNoCacheAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            // 1) Primary: online_mod.js -> token = decodeSecret([...], atob('...')) OR decodeSecret([...], '...')
            try
            {
                Logger?.LogInformation("KodikTokenProvider attempting primary token source (online_mod.js).");

                var payload = await GetSecretPayloadAsync(httpClient, cancellationToken).ConfigureAwait(false);

                // IMPORTANT: do not log payload contents, it can be used to reconstruct token.
                Logger?.LogInformation(
                    "KodikTokenProvider secret payload extracted. numbersCount={Count} passwordLen={PwdLen}",
                    payload.Numbers.Length,
                    (payload.Password ?? string.Empty).Length);

                var token = DecodeSecret(payload.Numbers, payload.Password);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    Logger?.LogInformation("KodikTokenProvider token decoded from primary source. tokenLen={Len}", token.Trim().Length);
                    return token;
                }

                Logger?.LogWarning("KodikTokenProvider decoded token from primary source is empty.");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "KodikTokenProvider primary source failed, falling back.");
                // ignore, fallback will be used
            }

            // 2) Fallback: kodik-add script token=...
            Logger?.LogInformation("KodikTokenProvider attempting fallback token source (kodik-add script). url={Url}", FallbackScriptUrl);

            var scriptResponse = await httpClient.GetAsync(FallbackScriptUrl, cancellationToken).ConfigureAwait(false);
            if (!scriptResponse.IsSuccessStatusCode)
            {
                throw new KodikTokenException($"Failed to download fallback Kodik script. HTTP {(int)scriptResponse.StatusCode}.");
            }

            var scriptBody = await scriptResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            Logger?.LogInformation(
                "KodikTokenProvider fallback script downloaded. len={Len} snippet={Snippet}",
                scriptBody.Length,
                Short(scriptBody.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal), 800));

            // token=VALUE until & or quote or whitespace
            var m = Regex.Match(
                scriptBody,
                @"token=(?<t>[^&""'\s]+)",
                RegexOptions.CultureInvariant);

            if (!m.Success)
            {
                throw new KodikTokenException("Failed to extract fallback Kodik token.");
            }

            var fallbackToken = m.Groups["t"].Value?.Trim();
            if (string.IsNullOrWhiteSpace(fallbackToken))
            {
                throw new KodikTokenException("Failed to extract fallback Kodik token (empty).");
            }

            Logger?.LogInformation("KodikTokenProvider fallback token extracted. tokenLen={Len}", fallbackToken.Length);
            return fallbackToken;
        }

        private sealed class SecretPayload
        {
            public SecretPayload(int[] numbers, string password)
            {
                Numbers = numbers;
                Password = password;
            }

            public int[] Numbers { get; }
            public string Password { get; }
        }

        private static async Task<SecretPayload> GetSecretPayloadAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            Logger?.LogInformation("KodikTokenProvider downloading online_mod.js. url={Url}", OnlineModUrl);

            var response = await httpClient.GetAsync(OnlineModUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new KodikTokenException($"Failed to download online_mod.js. HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new KodikTokenException("online_mod.js is empty.");
            }

            Logger?.LogInformation("KodikTokenProvider online_mod.js downloaded. len={Len}", body.Length);

            // Narrow scope near kodik embed marker (best-effort), but don't depend on it
            var hintIdx = body.LastIndexOf("kodikapi.com/search", StringComparison.Ordinal);
            if (hintIdx >= 0)
            {
                var start = Math.Max(0, hintIdx - 5000);
                var len = Math.Min(body.Length - start, 30000);
                var scope = body.Substring(start, len);

                Logger?.LogInformation("KodikTokenProvider attempting scoped payload extraction near marker. scopeLen={Len}", scope.Length);

                if (TryExtractSecretPayload(scope, out var payload))
                {
                    Logger?.LogInformation("KodikTokenProvider secret payload extracted from scoped region.");
                    return payload;
                }

                Logger?.LogInformation("KodikTokenProvider scoped extraction failed, attempting full body.");
            }

            if (TryExtractSecretPayload(body, out var payloadAll))
            {
                Logger?.LogInformation("KodikTokenProvider secret payload extracted from full body.");
                return payloadAll;
            }

            throw new KodikTokenException("Failed to locate Kodik decodeSecret payload in online_mod.js.");
        }

        private static bool TryExtractSecretPayload(string text, out SecretPayload payload)
        {
            payload = null!;

            // Supported forms:
            //   token = decodeSecret([..], atob('...'))
            //   var token = Utils.decodeSecret([..], '...')
            //   token = decodeSecret([..])   (password might be implicit in some builds; we'll default to "kodik")
            //
            // Prefer assignments into token variable to avoid matching unrelated decodeSecret usage.
            var re = new Regex(
                @"(?:(?:var|let|const)\s+token\s*=|token\s*=)\s*(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?<pwd>atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);

            var m = re.Match(text);
            if (!m.Success)
            {
                // Fallback: any decodeSecret([..], pwd)
                re = new Regex(
                    @"(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?<pwd>atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant);

                m = re.Match(text);
                if (!m.Success)
                {
                    return false;
                }
            }

            var listStr = m.Groups["list"].Value;
            var nums = Regex.Matches(listStr, @"\d+")
                .Cast<Match>()
                .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture))
                .ToArray();

            if (nums.Length == 0)
            {
                return false;
            }

            var password = "kodik"; // safe default

            var b64 = m.Groups["b64"]?.Value;
            if (!string.IsNullOrWhiteSpace(b64))
            {
                var decoded = DecodeAtob(b64);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    password = decoded;
                }
            }
            else
            {
                var plain1 = m.Groups["plain1"]?.Value;
                var plain2 = m.Groups["plain2"]?.Value;
                var plain = !string.IsNullOrWhiteSpace(plain1) ? plain1 : plain2;

                if (!string.IsNullOrWhiteSpace(plain))
                {
                    password = plain.Trim();
                }
            }

            payload = new SecretPayload(nums, password);
            return true;
        }

        private static string DecodeAtob(string b64)
        {
            b64 = (b64 ?? string.Empty).Trim();
            if (b64.Length == 0)
            {
                return string.Empty;
            }

            // pad base64
            var pad = (4 - (b64.Length % 4)) % 4;
            if (pad != 0)
            {
                b64 = b64 + new string('=', pad);
            }

            try
            {
                var bytes = Convert.FromBase64String(b64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        // =======================================
        // JS-compatible decodeSecret implementation
        // =======================================

        private static string DecodeSecret(IReadOnlyList<int> numbers, string password)
        {
            if (numbers == null || numbers.Count == 0)
            {
                return string.Empty;
            }

            password ??= string.Empty;
            if (password.Length == 0)
            {
                return string.Empty;
            }

            var hash = Salt("123456789" + password);

            // repeat hash to cover all numbers
            var hb = new StringBuilder(hash);
            while (hb.Length < numbers.Count)
            {
                hb.Append(hash);
            }

            var h = hb.ToString();
            var sb = new StringBuilder(numbers.Count);

            for (var i = 0; i < numbers.Count; i++)
            {
                // JS: input[i] ^ hash.charCodeAt(i)
                sb.Append((char)(numbers[i] ^ h[i]));
            }

            return sb.ToString();
        }

        private static string Salt(string input)
        {
            input ??= string.Empty;

            // JS int32 accumulator with overflow
            int hash = 0;
            foreach (var ch in input)
            {
                hash = unchecked((hash << 5) - hash + ch);
            }

            var hu = unchecked((uint)hash);

            var result = new StringBuilder(10);
            var i = 0;

            for (var j = 29; j >= 0; j -= 3)
            {
                var x = (((hu >> i) & 7u) << 3) + ((hu >> j) & 7u);

                int cc;
                if (x < 26)
                {
                    cc = 97 + (int)x;
                }
                else if (x < 52)
                {
                    cc = 39 + (int)x;
                }
                else
                {
                    cc = (int)x - 4;
                }

                result.Append((char)cc);
                i += 3;
            }

            return result.ToString();
        }

        private static string Short(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            if (s.Length <= maxLen)
            {
                return s;
            }

            return s.Substring(0, maxLen);
        }
    }
}
