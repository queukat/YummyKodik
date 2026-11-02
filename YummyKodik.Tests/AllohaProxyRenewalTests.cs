using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Alloha;
using YummyKodik.Yummy;

internal static class AllohaProxyRenewalTests
{
    private const string Proxy = "http://localhost:8096/YummyKodik/alloha-proxy";
    private const string OldRoot = "https://old.example/serial/";
    private const string NewRoot = "https://fresh.example/serial/";
    private const string Iframe = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token";

    public static void SequentialSiblingUsesRefreshedSession()
    {
        using var fixture = new Fixture();
        fixture.Read("first");
        fixture.Read("second");
        Check(fixture.Renewals == 1, $"Two old sibling IDs must share one renewal; got {fixture.Renewals}.");
        Check(fixture.Requests.Count(url => url.StartsWith(OldRoot, StringComparison.Ordinal)) == 1,
            "Only the first fragment may contact the expired origin.");
        Check(fixture.Requests.Contains(NewRoot + "second.m4s"), "The sibling must use its own refreshed URL.");
    }

    public static void UnknownSiblingRemainsUnresolvable()
    {
        using var fixture = new Fixture();
        fixture.Session.ProxyResources["orphan"] = OldRoot + "orphan.m4s";
        fixture.Session.ProxyResourceOriginalReferences["orphan"] = "orphan.m4s";
        fixture.Session.ProxyResourceParentIds["orphan"] = "missing-parent";
        fixture.Read("first");
        Check(!AllohaPlaybackService.TryResolveProxyResourceUrl(fixture.Session, "unregistered", out _),
            "Renewal must not invent a URL for an unregistered resource.");
        Check(fixture.Session.ProxyResources["orphan"] == OldRoot + "orphan.m4s",
            "A missing parent must not be guessed from a different playlist.");
        Check(fixture.Renewals == 1, "An unknown resource must not cause another renewal.");
    }

    public static void CapturedSiblingUrlUsesCurrentMapping()
    {
        using var fixture = new Fixture();
        Check(AllohaPlaybackService.TryResolveProxyResourceUrl(fixture.Session, "second", out var captured), "Expected sibling.");
        fixture.Read("first");
        fixture.Read("second", captured);
        Check(fixture.Renewals == 1 && fixture.Requests.Count(url => url.StartsWith(OldRoot, StringComparison.Ordinal)) == 1,
            "A caller holding an old URL must use the canonical renewed resource mapping before sending.");
    }

    public static void ForeignReferenceKeepsItsOrigin()
    {
        using var fixture = new Fixture();
        const string foreign = "https://other.example/foreign.m4s";
        fixture.Session.ProxyResources["foreign"] = foreign;
        fixture.Session.ProxyResourceOriginalReferences["foreign"] = foreign;
        fixture.Session.ProxyResourceParentIds["foreign"] = "playlist";
        fixture.Read("first");
        Check(AllohaPlaybackService.TryResolveProxyResourceUrl(fixture.Session, "foreign", out var url) && url == foreign,
            "An absolute foreign reference must never be moved onto the refreshed origin.");
        Check(!fixture.Requests.Contains(foreign), "Renewal must not download unrelated resources.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly AllohaPlaybackService _service;
        public List<string> Requests { get; } = new();
        public int Renewals { get; private set; }
        public AllohaPlaybackSession Session { get; } = new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ManifestUrl = OldRoot + "master.m3u8",
            ManifestText = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5309735\nvideo.m3u8",
            RefererUrl = Iframe,
            IframeUrl = Iframe,
            SelectedVoiceName = "AniDub",
            SelectedQuality = 1080,
            Source = new YummyAllohaSource
            {
                MovieToken = "movie-token", RequestToken = "req-token", TranslationId = 222,
                SeasonNumber = 1, EpisodeNumber = 3, RefererUrl = Iframe
            },
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };

        public Fixture()
        {
            Session.ProxyResources["playlist"] = OldRoot + "video.m3u8";
            Session.ProxyResourceReferers["playlist"] = OldRoot + "master.m3u8";
            Session.ProxyResourceOriginalReferences["playlist"] = "video.m3u8";
            foreach (var name in new[] { "first", "second" })
            {
                Session.ProxyResources[name] = OldRoot + name + ".m4s";
                Session.ProxyResourceReferers[name] = OldRoot + "video.m3u8";
                Session.ProxyResourceOriginalReferences[name] = name + ".m4s";
                Session.ProxyResourceParentIds[name] = "playlist";
            }
            _http = new HttpClient(new Handler(Handle));
            _service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, _http);
        }

        public void Read(string id, string? capturedUrl = null)
        {
            Check(AllohaPlaybackService.TryResolveProxyResourceUrl(Session, id, out var url), "Expected registered fragment.");
            var resource = _service.DownloadProxyResourceAsync(Session, id, capturedUrl ?? url, Proxy, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(resource.Content.Length == 4, "A renewed fragment must contain the expected media bytes.");
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (url.StartsWith(OldRoot, StringComparison.Ordinal))
                return Reply(HttpStatusCode.Forbidden, "denied");
            if (request.Method == HttpMethod.Get && url.StartsWith("https://alloha.yani.tv/?token_movie=", StringComparison.Ordinal))
            {
                Renewals++;
                return Reply(HttpStatusCode.OK, """
                    <html><head><meta name="viewporti" content="yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT"></head>
                    <script>const fileList = JSON.parse('{"active":{"id":1191328,"seasons":1,"episode":3,"id_translation":222},"all":{"t222":{"file":{"1":{"3":{"id":1191328}}}}}}');</script></html>
                    """);
            }
            if (request.Method == HttpMethod.Post && url == "https://alloha.yani.tv/bnsi/movies/1191328")
                return Reply(HttpStatusCode.OK, """
                    {"hlsSource":[{"label":"AniDub","audioId":"1","quality":{"1080":"https://fresh.example/serial/master.m3u8"}}]}
                    """);
            if (url == NewRoot + "master.m3u8")
                return Reply(HttpStatusCode.OK, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5309735\nvideo.m3u8");
            if (url == NewRoot + "first.m4s" || url == NewRoot + "second.m4s")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) };
                response.Content.Headers.ContentType = new("video/mp4");
                return response;
            }
            throw new InvalidOperationException("Unexpected test request.");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text) };
        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handle(request));
    }
}
