using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshTitleKeySource
{
    private readonly ILogger _logger;
    private readonly Action _saveConfiguration;
    private readonly Func<PluginConfiguration>? _currentConfiguration;

    public RefreshTitleKeySource(ILogger logger, Action saveConfiguration, Func<PluginConfiguration>? currentConfiguration = null)
    {
        _logger = logger;
        _saveConfiguration = saveConfiguration;
        _currentConfiguration = currentConfiguration;
    }

    public bool UserListFetchFailed { get; private set; }

    public bool UserListFetchSucceeded { get; private set; }

    public string FailureCode { get; private set; } = string.Empty;

    public async Task<List<string>> BuildAsync(
        PluginConfiguration cfg,
        YummyClient yummyClient,
        CancellationToken cancellationToken,
        LibraryTarget? target = null)
    {
        target ??= cfg.GetLibraryTargets()[0];
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddConfiguredAnimeKeys(target.Slugs, set);
        await AddUserListAnimeKeysAsync(cfg, target, yummyClient, set, cancellationToken).ConfigureAwait(false);

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
        LibraryTarget target,
        YummyClient yummyClient,
        HashSet<string> keys,
        CancellationToken cancellationToken)
    {
        UserListFetchFailed = false;
        UserListFetchSucceeded = false;
        FailureCode = string.Empty;

        if (!target.UseUserListSubscription)
        {
            return;
        }

        var listId = Math.Max(0, target.YummyUserListId);

        try
        {
            cfg = await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            if (cfg.YummyUserId <= 0)
            {
                UserListFetchFailed = true;
                FailureCode = "user_id_required";
                _logger.LogWarning("[YummyKodik] UseUserListSubscription enabled, but YummyUserId is not set.");
                return;
            }


            var items = await FetchUserListWithRetryAsync(cfg, yummyClient, listId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[YummyKodik] User list fetched. userId={UserId} listId={ListId} items={Count}",
                cfg.YummyUserId,
                listId,
                items.Count);

            AddUserListItemKeys(items, keys);
            UserListFetchSucceeded = true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            UserListFetchFailed = true;
            FailureCode = ex is YummyAccountException accountError ? accountError.Code : "list_unavailable";
            _logger.LogError(ex,
                "[YummyKodik] Failed to fetch Yummy list {ListId} for '{Root}'; keeping configured manual titles and any proven local fallback titles.",
                listId, target.OutputRootPath);
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
        catch (YummyAccountException ex) when (ex.Code == "access_denied" && !string.IsNullOrWhiteSpace(yummyClient.GetAccessToken()))
        {
            yummyClient.SetAccessToken(null);
            cfg = await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            return await yummyClient.GetUserListAsync(cfg.YummyUserId, listId, cancellationToken).ConfigureAwait(false);
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

    private Task<PluginConfiguration> EnsureAuthenticatedAsync(PluginConfiguration cfg, YummyClient yummyClient, CancellationToken cancellationToken)
        => YummyAccountAuthentication.EnsureAsync(cfg, yummyClient, _saveConfiguration, cancellationToken, _currentConfiguration);
}
