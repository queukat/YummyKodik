using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.WebUtilities;
using YummyKodik.Configuration;
using YummyKodik.Util;

namespace YummyKodik.Media;

public sealed partial class YummyKodikMediaSourceProvider
{
    internal static readonly string NativeOpenPrefix =
        typeof(YummyKodikMediaSourceProvider).FullName!.GetMD5().ToString("N") + "_";

    private static bool HasExplicitNativeVoice(string gateway)
    {
        return (YummyKodikStreamUri.TryParseRequest(gateway, out var request) && !string.IsNullOrWhiteSpace(request.VoiceName)) ||
            (TryParseLogicalUri(gateway, out _, out _, out _, out var translation) && !string.IsNullOrWhiteSpace(translation));
    }

    internal bool TryGetNativeGateway(BaseItem item, out string gateway)
    {
        gateway = string.Empty;
        if (Plugin.Instance == null || string.IsNullOrWhiteSpace(item.Path) ||
            !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
            !Plugin.Instance.Configuration.GetLibraryTargets().Any(root => root.ContainsPath(item.Path)) ||
            !TryGetLogicalUri(item, out var text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !YummyKodikStreamUri.TryParseRequest(text, out var request) || request.Episode is not > 0)
        {
            return false;
        }

        // Keep the generated provider/episode/voice query, but use the canonical
        // process-local origin. No caller-supplied remote endpoint is fetched.
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["format"] = "hls";
        gateway = QueryHelpers.AddQueryString(
            _internalJellyfinUrlProvider.GetBaseUrl().TrimEnd('/') + "/YummyKodik/stream", query);
        return true;
    }

    internal bool PrepareNativeSource(MediaSourceInfo source)
    {
        if (!Guid.TryParse(source.Id, out var id) || _libraryManager.GetItemById(id) is not { } item ||
            !TryGetNativeGateway(item, out _)) return false;

        source.RequiresOpening = true;
        source.RequiresClosing = true;
        source.OpenToken = NativeOpenPrefix + id.ToString("N");
        return true;
    }

    private async Task<ILiveStream> OpenNativeMediaSourceAsync(string openToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sourceManager == null || !Guid.TryParseExact(openToken, "N", out var id) ||
            _libraryManager.GetItemById(id) is not { } item || !TryGetNativeGateway(item, out var gateway))
        {
            throw new InvalidOperationException("The requested managed episode is no longer available.");
        }

        var manager = _sourceManager();
        var original = manager.GetStaticMediaSources(item, false)
            .FirstOrDefault(source => Guid.TryParse(source.Id, out var sourceId) && sourceId == id)
            ?? throw new InvalidOperationException("The requested episode version is no longer available.");
        var resolved = await HlsPlaybackManifestResolver.ResolveAsync(
            _httpClientFactory.CreateClient(HttpClientNames.PlaybackManifest), gateway, cancellationToken).ConfigureAwait(false);
        var source = YummyKodikMediaSourceFactory.Build(new MediaSourceBuildOptions
        {
            ItemId = id.ToString("N"), Name = original.Name, Url = resolved.Url,
            Container = "m3u8", RunTimeTicks = resolved.RunTimeTicks, SupportsDirectPlay = false
        });
        source.Id = original.Id;
        source.LiveStreamId = Guid.NewGuid().ToString("N");
        source.RequiresClosing = true;

        // Probe this session, not cached STRM metadata or a new gateway resolution.
        // VOD probing preserves track indexes and avoids the live-TV probe cache.
        await manager.AddMediaInfoWithProbe(source, false, null!, false, false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video && stream.Index >= 0) ||
            !source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Audio && stream.Index >= 0))
        {
            throw new InvalidOperationException("The selected episode did not provide usable video and audio streams.");
        }
        source.RunTimeTicks = resolved.RunTimeTicks;
        await PublishResolvedRunTimeAsync(item, new[] { source }, cancellationToken).ConfigureAwait(false);
        return new NativePlaybackStream(source);
    }

    internal sealed class NativePlaybackStream(MediaSourceInfo source) : ILiveStream
    {
        public int ConsumerCount { get; set; } = 1;
        public string OriginalStreamId { get; set; } = source.Id;
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; } = source;
        public string UniqueId { get; } = source.LiveStreamId;
        public Task Open(CancellationToken openCancellationToken)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        // Provider proxies already own their bounded idle expiry; this object owns
        // only the host playback binding, and has no background workers or handles.
        public Task Close() => Task.CompletedTask;
        public Stream GetStream() => throw new NotSupportedException("Playback uses the pinned HTTP proxy.");
        public void Dispose() { }
    }
}
