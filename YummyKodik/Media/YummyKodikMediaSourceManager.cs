using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace YummyKodik.Media;

/// <summary>Adapts native STRM descriptors to Jellyfin's existing playback-session lifecycle.</summary>
internal sealed class YummyKodikMediaSourceManager(IMediaSourceManager inner, YummyKodikMediaSourceProvider provider)
    : IMediaSourceManager, IDisposable
{
    private readonly IMediaSourceManager _inner = inner;

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var managed = provider.TryGetNativeGateway(item, out _);
        // Opening the selected version probes the pinned stream. The native STRM
        // refresh would independently resolve every alternate before selection.
        var sources = await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe && !managed,
            enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (managed)
        {
            foreach (var source in sources) provider.PrepareNativeSource(source);
        }
        return sources;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => _inner.GetMediaStreams(itemId);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null) => _inner.GetStaticMediaSources(item, enablePathSubstitution, user!);
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStream(request, cancellationToken);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStreamInternal(request, cancellationToken);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => _inner.GetLiveStream(id, cancellationToken);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => _inner.GetRecordingStreamMediaSources(info, cancellationToken);
    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamMediaInfo(id, cancellationToken);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    public void Dispose()
    {
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
