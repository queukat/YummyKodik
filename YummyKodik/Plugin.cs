// File: Plugin.cs

using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;

namespace YummyKodik
{
    /// <summary>
    /// Main plugin entry point.
    /// </summary>
    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly Guid _id = new("6801ee3f-27f2-4d4e-ab37-e569c025b7c5");

        public static Plugin Instance { get; private set; } = null!;

        public override string Name => "YummyKodik";

        public override Guid Id => _id;

        public override string Description =>
            "Creates Jellyfin anime series cards from YummyAnime and streams episodes from Kodik.";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Logger = logger;
            TryEnsureLocalPluginImageManifest();
        }

        public ILogger<Plugin> Logger { get; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "config",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.config.html"
            };

            // JS helper for Jellyfin Web UI injection (translation picker on series page).
            // Note: Jellyfin Web does not automatically load this script by itself.
            yield return new PluginPageInfo
            {
                Name = "seriesTranslation.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.seriesTranslation.js"
            };
        }

        private void TryEnsureLocalPluginImageManifest()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(AssemblyFilePath);
                if (string.IsNullOrWhiteSpace(pluginDir))
                {
                    return;
                }

                var logoPath = Path.Combine(pluginDir, "logo.svg");
                var metaPath = Path.Combine(pluginDir, "meta.json");
                if (!File.Exists(logoPath) || !File.Exists(metaPath))
                {
                    return;
                }

                var root = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
                if (root is null)
                {
                    return;
                }

                var currentImagePath = root["imagePath"]?.GetValue<string>();
                if (string.Equals(currentImagePath, logoPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                root["imagePath"] = logoPath;

                var json = root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(metaPath, json);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[YummyKodik] Failed to update local plugin meta.json with logo.svg path.");
            }
        }
    }
}
