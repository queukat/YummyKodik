// File: Kodik/KodikTokenProvider.cs

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Kodik
{
    public static class KodikTokenProvider
    {
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
            Logger?.LogInformation(
                "KodikTokenProvider attempting token refresh via shared resolver. onlineModUrl={OnlineModUrl} fallbackUrl={FallbackUrl}",
                KodikTokenResolver.OnlineModUrl,
                KodikTokenResolver.FallbackScriptUrl);

            var token = await KodikTokenResolver.ResolveTokenAsync(httpClient, cancellationToken).ConfigureAwait(false);
            Logger?.LogInformation("KodikTokenProvider token resolved via shared resolver. tokenLen={Len}", token.Length);
            return token;
        }
    }
}
