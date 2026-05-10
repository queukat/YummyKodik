using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Web;

public sealed class JellyfinWebSeriesTranslationBootstrapHostedService : IHostedService
{
    private readonly ILogger<JellyfinWebSeriesTranslationBootstrapHostedService> _logger;

    public JellyfinWebSeriesTranslationBootstrapHostedService(
        ILogger<JellyfinWebSeriesTranslationBootstrapHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TryEnsureBootstrapScript();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    internal static string? ResolveWebIndexPath(string? baseDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        var candidates = new[]
        {
            Path.Combine(root, "jellyfin-web", "index.html"),
            Path.Combine(root, "wwwroot", "index.html")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void TryEnsureBootstrapScript()
    {
        try
        {
            var indexPath = ResolveWebIndexPath();
            if (string.IsNullOrWhiteSpace(indexPath))
            {
                _logger.LogDebug("[YummyKodik] Jellyfin Web index.html was not found. Translation widget bootstrap was skipped.");
                return;
            }

            var html = File.ReadAllText(indexPath);
            var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
            var scriptUrl = $"/web/ConfigurationPage?name=seriesTranslation.js&v={Uri.EscapeDataString(version)}";

            if (!JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(html, scriptUrl, out var patchedHtml))
            {
                _logger.LogDebug("[YummyKodik] Jellyfin Web already includes the translation widget bootstrap.");
                return;
            }

            File.WriteAllText(indexPath, patchedHtml, new UTF8Encoding(false));
            _logger.LogInformation("[YummyKodik] Injected translation widget bootstrap into Jellyfin Web: {IndexPath}", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[YummyKodik] Failed to inject translation widget bootstrap into Jellyfin Web.");
        }
    }
}
