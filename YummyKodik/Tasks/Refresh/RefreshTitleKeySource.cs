using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshTitleKeySource
{
    private readonly ILogger _logger;
    private readonly Action _saveConfiguration;

    public RefreshTitleKeySource(ILogger logger, Action saveConfiguration)
    {
        _logger = logger;
        _saveConfiguration = saveConfiguration;
    }

    public async Task<List<string>> BuildAsync(
        PluginConfiguration cfg,
        YummyClient yummyClient,
        CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddConfiguredAnimeKeys(cfg.Slugs, set);
        await AddUserListAnimeKeysAsync(cfg, yummyClient, set, cancellationToken).ConfigureAwait(false);

        return set.ToList();
    }

    private static void AddConfiguredAnimeKeys(IEnumerable<string>? slugs, HashSet<string> keys)
    {
        if (slugs == null)
        {
            return;
        }

        foreach (var slug in slugs)
        {
            var key = RefreshPathUtilities.NormalizeKey(slug);
            if (!string.IsNullOrEmpty(key))
            {
                keys.Add(key);
            }
        }
    }

    private async Task AddUserListAnimeKeysAsync(
        PluginConfiguration cfg,
        YummyClient yummyClient,
        HashSet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!cfg.UseUserListSubscription)
        {
            return;
        }

        if (cfg.YummyUserId <= 0)
        {
            _logger.LogWarning("[YummyKodik] UseUserListSubscription enabled, but YummyUserId is not set.");
            return;
        }

        var listId = cfg.YummyUserListId < 0 ? 0 : cfg.YummyUserListId;

        try
        {
            await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            var items = await FetchUserListWithRetryAsync(cfg, yummyClient, listId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[YummyKodik] User list fetched. userId={UserId} listId={ListId} items={Count}",
                cfg.YummyUserId,
                listId,
                items.Count);

            AddUserListItemKeys(items, keys);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YummyKodik] Failed to fetch user list, falling back to manual slugs only.");
        }
    }

    private async Task<IReadOnlyList<YummyUserListItem>> FetchUserListWithRetryAsync(
        PluginConfiguration cfg,
        YummyClient yummyClient,
        int listId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await yummyClient
                .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) when (!string.IsNullOrWhiteSpace(yummyClient.GetAccessToken()))
        {
            _logger.LogWarning(ex, "[YummyKodik] User list unauthorized, trying token refresh and retry.");
            await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

            return await yummyClient
                .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void AddUserListItemKeys(IEnumerable<YummyUserListItem> items, HashSet<string> keys)
    {
        foreach (var item in items)
        {
            var key = RefreshPathUtilities.NormalizeKey(item.AnimeUrl);
            if (!string.IsNullOrEmpty(key))
            {
                keys.Add(key);
                continue;
            }

            if (item.AnimeId > 0)
            {
                keys.Add(item.AnimeId.ToString());
            }
        }
    }

    private async Task EnsureAuthenticatedAsync(
        PluginConfiguration cfg,
        YummyClient yummyClient,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cfg.YummyAccessToken))
        {
            yummyClient.SetAccessToken(cfg.YummyAccessToken);

            try
            {
                var refreshed = await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                if (!string.Equals(cfg.YummyAccessToken, refreshed, StringComparison.Ordinal))
                {
                    cfg.YummyAccessToken = refreshed;
                    _saveConfiguration();
                    _logger.LogInformation("[YummyKodik] Yummy access token refreshed and saved.");
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[YummyKodik] Token refresh failed, will try login if credentials exist.");
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.YummyLogin) && !string.IsNullOrWhiteSpace(cfg.YummyPassword))
        {
            _logger.LogInformation("[YummyKodik] Logging in to Yummy to obtain user token.");

            var token = await yummyClient.LoginAsync(
                    cfg.YummyLogin.Trim(),
                    cfg.YummyPassword,
                    string.IsNullOrWhiteSpace(cfg.YummyRecaptchaResponse) ? null : cfg.YummyRecaptchaResponse.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(token))
            {
                cfg.YummyAccessToken = token;
                _saveConfiguration();
                _logger.LogInformation("[YummyKodik] Yummy access token obtained and saved.");
            }

            return;
        }

        _logger.LogWarning(
            "[YummyKodik] User list subscription enabled but no auth is configured. Set YummyAccessToken or YummyLogin and YummyPassword.");
    }
}
