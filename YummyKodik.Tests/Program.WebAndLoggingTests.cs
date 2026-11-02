using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
partial class Program
{
    static void YummyKodikLogFilter_DefaultsToWarning()
    {
        var defaultCfg = new PluginConfiguration();
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, defaultCfg), "Default plugin logging should suppress informational logs.");
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, defaultCfg), "Default plugin logging should keep warnings.");
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Error, defaultCfg), "Default plugin logging should keep errors.");

        var blankCfg = new PluginConfiguration
        {
            MinimumLogLevel = string.Empty
        };
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, blankCfg), "Blank log level should fall back to Warning.");

        AssertTrue(
            YummyKodikLogFilter.ShouldLog("provider", "Microsoft.Hosting", LogLevel.Information),
            "Non-YummyKodik categories should pass through the plugin filter.");
    }

    static void YummyKodikLogFilter_UsesConfiguredMinimumLevel()
    {
        var cfg = new PluginConfiguration
        {
            MinimumLogLevel = "Information"
        };
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Information, cfg), "Information level should allow informational diagnostics.");
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Debug, cfg), "Information level should still suppress Debug logs.");

        cfg.MinimumLogLevel = "debug";
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Debug, cfg), "Configured log level parsing should be case-insensitive.");
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Trace, cfg), "Debug level should suppress Trace logs.");

        cfg.MinimumLogLevel = "Error";
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, cfg), "Error level should suppress warnings.");
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Error, cfg), "Error level should keep errors.");
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Critical, cfg), "Error level should keep critical logs.");

        cfg.MinimumLogLevel = "None";
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Critical, cfg), "None should suppress all plugin logs.");

        cfg.MinimumLogLevel = "unexpected";
        AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, cfg), "Unknown log level should fall back to Warning.");
        AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, cfg), "Unknown log level fallback should keep warnings.");

        cfg.MinimumLogLevel = "Information";
        YummyKodikLogFilter.ConfigurationProvider = () => cfg;
        try
        {
            AssertTrue(
                YummyKodikLogFilter.ShouldLog("provider", "YummyKodik.Tasks.Refresh.RefreshTitleService", LogLevel.Information),
                "YummyKodik category filtering should use the current plugin configuration provider.");
            AssertFalse(
                YummyKodikLogFilter.ShouldLog("provider", "YummyKodik.Tasks.Refresh.RefreshTitleService", LogLevel.Debug),
                "YummyKodik category filtering should suppress logs below the configured provider level.");
        }
        finally
        {
            YummyKodikLogFilter.ConfigurationProvider = static () => null;
        }
    }

    static void YummyKodikLogFilter_CategoryRuleSuppressesPluginInformationLogs()
    {
        var cfg = new PluginConfiguration();
        YummyKodikLogFilter.ConfigurationProvider = () => cfg;
        var provider = new CaptureLoggerProvider();

        try
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(provider);
                builder.AddFilter("YummyKodik", YummyKodikLogFilter.ShouldLogPluginCategory);
            });

            var pluginLogger = factory.CreateLogger("YummyKodik.Plugin");
            pluginLogger.LogInformation("suppressed");
            pluginLogger.LogWarning("kept");

            var otherLogger = factory.CreateLogger("Microsoft.Hosting");
            otherLogger.LogInformation("other");

            AssertFalse(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Category rule should suppress YummyKodik information logs.");
            AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Warning), "Category rule should keep YummyKodik warnings.");
            AssertTrue(provider.Events.Any(x => x.Category == "Microsoft.Hosting" && x.Level == LogLevel.Information), "Category rule should not suppress other categories.");
        }
        finally
        {
            YummyKodikLogFilter.ConfigurationProvider = static () => null;
        }
    }

    static void YummyKodikLogger_SuppressesInformationBeforeInnerLogger()
    {
        var cfg = new PluginConfiguration();
        YummyKodikLogFilter.ConfigurationProvider = () => cfg;
        var provider = new CaptureLoggerProvider();

        try
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(provider);
            });

            var wrapped = new YummyKodikLogger(factory.CreateLogger("YummyKodik.Plugin"), "YummyKodik.Plugin");
            wrapped.LogInformation("suppressed");
            wrapped.LogWarning("kept");

            AssertFalse(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Wrapped plugin logger should suppress Information before it reaches Jellyfin.");
            AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Warning), "Wrapped plugin logger should keep Warning logs.");

            cfg.MinimumLogLevel = "Information";
            wrapped.LogInformation("enabled");
            AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Lowering the plugin level to Information should re-enable informational logs.");
        }
        finally
        {
            YummyKodikLogFilter.ConfigurationProvider = static () => null;
        }
    }

    static void RefreshPerformanceSummary_IsVisibleWithoutInformationNoise()
    {
        var cfg = new PluginConfiguration
        {
            MinimumLogLevel = "Warning",
            EnablePerformanceDebugLogging = true
        };
        YummyKodikLogFilter.ConfigurationProvider = () => cfg;
        var provider = new CaptureLoggerProvider();

        try
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(provider);
                builder.AddFilter("YummyKodik", YummyKodikLogFilter.ShouldLogPluginCategory);
            });

            var wrapped = new YummyKodikLogger(factory.CreateLogger("YummyKodik.Tasks.Refresh"), "YummyKodik.Tasks.Refresh");
            wrapped.LogInformation("ordinary information must remain hidden");

            var run = new RefreshRunMetrics(cfg.EnablePerformanceDebugLogging);
            run.AddDuration("phase.strm_snapshot", TimeSpan.FromMilliseconds(3));
            run.AddDuration("phase.runtime_backfill.pre", TimeSpan.FromMilliseconds(4));
            run.AddDuration("phase.runtime_publish.pre", TimeSpan.FromMilliseconds(5));
            run.AddDuration("phase.title_keys", TimeSpan.FromMilliseconds(6));
            run.AddDuration("phase.stale_cleanup", TimeSpan.FromMilliseconds(7));
            run.AddDuration("phase.titles", TimeSpan.FromMilliseconds(8));
            run.AddDuration("phase.runtime_backfill.post", TimeSpan.FromMilliseconds(9));
            run.AddDuration("phase.readiness_merge", TimeSpan.FromMilliseconds(10));
            run.AddCount("kodik.http_requests", 11);
            run.AddCount("kodik.search.cache_hits", 12);
            run.AddCount("skip.files_checked", 13);
            run.AddCount("io.strm_updated", 2);
            run.AddCount("titles.total", 4);
            run.AddCount("titles.finished", 4);
            run.AddCount("titles.unhandled_failed", 1);

            var title = new RefreshPerformanceMetrics(cfg.EnablePerformanceDebugLogging, run);
            title.AddCount("skip.PerVoiceAfterCatalog.Matched");
            title.LogSummary(wrapped, "Frieren", "frieren");
            run.LogSummary(wrapped);

            AssertFalse(
                provider.Events.Any(x => x.Message.Contains("ordinary information", StringComparison.Ordinal)),
                "Enabling perf summaries must not enable ordinary Information noise.");
            var summaries = provider.Events
                .Where(x => x.Message.Contains("[YummyKodik][perf]", StringComparison.Ordinal))
                .ToArray();
            AssertEqual(2, summaries.Length, "Enabled performance logging should expose title and run summaries through the Warning minimum.");
            var runSummary = summaries.Single(x => x.Message.Contains("Run ", StringComparison.Ordinal));
            AssertTrue(runSummary.Message.Contains("kodikHttpRequests=11", StringComparison.Ordinal), "Run summary should expose actual instrumented KodikClient HTTP requests.");
            AssertTrue(runSummary.Message.Contains("cacheHits=12", StringComparison.Ordinal), "Run summary should expose cache hits.");
            AssertTrue(runSummary.Message.Contains("filesChecked=13", StringComparison.Ordinal), "Run summary should expose actually hashed files.");
            AssertTrue(runSummary.Message.Contains("filesChanged=2", StringComparison.Ordinal), "Run summary should aggregate file change operations.");
            AssertTrue(runSummary.Message.Contains("phase.readiness_merge=10ms", StringComparison.Ordinal), "Run summary should contain common phase timings.");
            AssertTrue(runSummary.Message.Contains("skip.PerVoiceAfterCatalog.Matched=1", StringComparison.Ordinal), "Title skip decisions should aggregate into the run summary.");

            var beforeDisabled = provider.Events.Count;
            new RefreshPerformanceMetrics(enabled: false).LogSummary(wrapped, "Disabled", "disabled");
            new RefreshRunMetrics(enabled: false).LogSummary(wrapped);
            AssertEqual(beforeDisabled, provider.Events.Count, "Disabled performance logging should emit no summaries.");
        }
        finally
        {
            YummyKodikLogFilter.ConfigurationProvider = static () => null;
        }
    }

    static void JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose()
    {
        const string html = "<!doctype html>\r\n<html><head><title>Test</title></head><body></body></html>";
        const string scriptUrl = "/web/ConfigurationPage?name=seriesTranslation.js&v=1.0.0.0";

        var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(html, scriptUrl, out var patchedHtml);

        AssertTrue(changed, "Bootstrap script should be injected into a plain Jellyfin index.html.");
        AssertTrue(patchedHtml.Contains(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal), "Managed bootstrap start marker should be present.");
        AssertTrue(patchedHtml.Contains("ConfigurationPage?name=seriesTranslation.js", StringComparison.Ordinal), "Managed bootstrap should point to the translation widget script.");
        AssertTrue(patchedHtml.Contains("v=1.0.0.0", StringComparison.Ordinal), "Managed bootstrap should carry the cache-busting version.");
        AssertTrue(patchedHtml.IndexOf(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal) < patchedHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase), "Managed bootstrap should be inserted before </head>.");
    }

    static void JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap()
    {
        var original = string.Join(
            "\n",
            "<html><head>",
            JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=old"),
            "</head><body></body></html>");

        var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
            original,
            "/web/ConfigurationPage?name=seriesTranslation.js&v=new",
            out var patchedHtml);

        AssertTrue(changed, "Managed bootstrap should be updated when the script URL changes.");
        AssertTrue(patchedHtml.Contains("v=new", StringComparison.Ordinal), "Updated bootstrap should contain the new version.");
        AssertFalse(patchedHtml.Contains("v=old", StringComparison.Ordinal), "Updated bootstrap should replace the old version.");
    }

    static void JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap()
    {
        var original = string.Join(
            "\n",
            "<html><head>",
            JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=stable"),
            "</head><body></body></html>");

        var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
            original,
            "/web/ConfigurationPage?name=seriesTranslation.js&v=stable",
            out var patchedHtml);

        AssertFalse(changed, "Managed bootstrap should not be duplicated when it is already current.");
        AssertEqual(original, patchedHtml, "Unchanged bootstrap should leave index.html intact.");
    }

    static void JellyfinWebIndexPatcher_UpgradesBothScriptsWithoutDuplicates()
    {
        const string translationUrl = "/web/ConfigurationPage?name=seriesTranslation.js&v=build-new";
        const string playbackUrl = "/web/ConfigurationPage?name=playbackBuffer.js&v=build-new";
        var original = "<html><head>\r\n"
            + JellyfinWebIndexPatcher.BuildManagedSnippet(
                "/web/ConfigurationPage?name=seriesTranslation.js&v=build-old", "\r\n")
            + "\r\n</head><body></body></html>";

        var upgraded = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
            original, translationUrl, out var patched, playbackUrl);
        AssertTrue(upgraded, "An installed single-script bootstrap must upgrade in place to both web helpers.");
        AssertFalse(patched.Contains("build-old", StringComparison.Ordinal), "The previous build key must be removed during upgrade.");
        AssertTrue(patched.Contains("name=playbackBuffer.js&amp;v=build-new", StringComparison.Ordinal), "Playback buffering must have the current escaped build cache key.");
        AssertTrue(patched.Contains("name=seriesTranslation.js&amp;v=build-new", StringComparison.Ordinal), "The translation script must share the current build cache key.");
        AssertEqual(2, patched.Split("<script defer=\"defer\"", StringSplitOptions.None).Length - 1, "Both web helpers must load using defer.");
        AssertTrue(patched.IndexOf("name=playbackBuffer.js", StringComparison.Ordinal)
            < patched.IndexOf("name=seriesTranslation.js", StringComparison.Ordinal), "The playback hook should install before the translation helper.");

        var repeated = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
            patched, translationUrl, out var unchanged, playbackUrl);
        AssertFalse(repeated, "Applying the same two-script bootstrap must be idempotent.");
        AssertEqual(patched, unchanged, "An unchanged bootstrap must retain the exact existing HTML.");
        AssertEqual(1, unchanged.Split("name=playbackBuffer.js", StringSplitOptions.None).Length - 1, "The playback helper must occur exactly once.");
        AssertEqual(1, unchanged.Split("name=seriesTranslation.js", StringSplitOptions.None).Length - 1, "The translation helper must occur exactly once.");

        using var embeddedPlayback = typeof(NfoBuilder).Assembly.GetManifestResourceStream("YummyKodik.Web.playbackBuffer.js");
        AssertTrue(embeddedPlayback is not null, "The playback helper referenced by the bootstrap must be embedded in the plugin.");
    }

    static void SeriesTranslationScript_AcceptsJellyfinPascalCaseTranslationOptions()
    {
        using var stream = typeof(NfoBuilder).Assembly.GetManifestResourceStream(
            "YummyKodik.Web.seriesTranslation.js");
        AssertTrue(stream is not null, "The embedded translation widget script should exist.");

        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        AssertTrue(
            script.Contains("readValue(t, \"id\", \"Id\")", StringComparison.Ordinal),
            "The widget should accept Jellyfin's PascalCase translation Id.");
        AssertTrue(
            script.Contains("readValue(t, \"name\", \"Name\")", StringComparison.Ordinal),
            "The widget should accept Jellyfin's PascalCase translation Name.");
        AssertTrue(
            script.Contains("readValue(t, \"type\", \"Type\")", StringComparison.Ordinal),
            "The widget should accept Jellyfin's PascalCase translation Type.");
        AssertTrue(
            script.Contains("ykRequest: Date.now().toString()", StringComparison.Ordinal),
            "Translation API requests should bypass stale browser responses.");
        AssertTrue(
            script.Contains("\"✓ \" + item.label", StringComparison.Ordinal),
            "The selected widget voice should have an explicit visual marker.");
    }
}
