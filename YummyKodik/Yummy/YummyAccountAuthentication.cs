using YummyKodik.Configuration;

namespace YummyKodik.Yummy;

public sealed class YummyAccountException(string code) : UnauthorizedAccessException(code)
{
    public string Code { get; } = code;
}

/// <summary>Shared token recovery policy for scheduled refresh and explicit account sign-in.</summary>
internal static class YummyAccountAuthentication
{

    internal static async Task<PluginConfiguration> EnsureAsync(PluginConfiguration cfg, YummyClient client, Action save, CancellationToken ct, Func<PluginConfiguration>? current = null)
    {
        await Plugin.ConfigurationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cfg = current?.Invoke() ?? cfg;
            if (!string.IsNullOrWhiteSpace(client.GetAccessToken()) && client.GetAccessToken() == cfg.YummyAccessToken) return cfg;
            client.SetAccessToken(cfg.YummyAccessToken);
            if (!string.IsNullOrWhiteSpace(cfg.YummyAccessToken))
            {
                try
                {
                    var token = await client.RefreshTokenAsync(ct).ConfigureAwait(false);
                    SetStatus(cfg, "connected", save, () => cfg.YummyAccessToken = token);
                    return cfg;
                }
                catch (YummyAccountException ex) when (ex.Code == "access_denied") { }
            }

            if (string.IsNullOrWhiteSpace(cfg.YummyLogin) || string.IsNullOrWhiteSpace(cfg.YummyPassword))
            {
                if (string.IsNullOrWhiteSpace(cfg.YummyAccessToken)) return cfg; // Public lists remain supported.
                throw new YummyAccountException("sign_in_required");
            }
            var loginToken = await client.LoginAsync(cfg.YummyLogin, cfg.YummyPassword, null, ct).ConfigureAwait(false);
            SetStatus(cfg, "connected", save, () => cfg.YummyAccessToken = loginToken);
            return cfg;
        }
        catch (YummyAccountException ex)
        {
            SetStatus(cfg, ex.Code, save);
            throw;
        }
        finally { Plugin.ConfigurationGate.Release(); }
    }

    internal static void SetStatus(PluginConfiguration cfg, string status, Action save, Action? update = null)
    {
        var old = (cfg.YummyAccessToken, cfg.YummyLogin, cfg.YummyPassword, cfg.YummyUserId,
            cfg.YummyRecaptchaResponse, cfg.YummyAccountStatus, cfg.YummyAccountCheckedUtc);
        try
        {
            update?.Invoke();
            cfg.YummyAccountStatus = status;
            cfg.YummyAccountCheckedUtc = DateTime.UtcNow;
            save();
        }
        catch (Exception ex)
        {
            (cfg.YummyAccessToken, cfg.YummyLogin, cfg.YummyPassword, cfg.YummyUserId,
                cfg.YummyRecaptchaResponse, cfg.YummyAccountStatus, cfg.YummyAccountCheckedUtc) = old;
            throw new IOException("Yummy account settings could not be saved. Sign in again after checking server storage.", ex);
        }
    }
}
