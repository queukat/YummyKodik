using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Alloha;

internal sealed class AllohaHeadlessStreamTokenResolver
{
    private static readonly TimeSpan DevToolsReadyTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StateReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    private readonly ILogger _logger;

    public AllohaHeadlessStreamTokenResolver(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ResolveStreamTokenAsync(
        string iframeUrl,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        var browserPath = FindBrowserExecutable();
        if (string.IsNullOrWhiteSpace(browserPath))
        {
            _logger.LogWarning("Alloha stream token resolver could not find a supported headless browser.");
            return null;
        }

        var debugPort = ReserveFreeTcpPort();
        var userDataDir = Path.Combine(
            Path.GetTempPath(),
            "YummyKodik",
            "alloha-browser",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(userDataDir);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = BuildBrowserArguments(debugPort, userDataDir),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Headless browser did not start.");
            }

            var target = await WaitForPageTargetAsync(debugPort, process, cancellationToken).ConfigureAwait(false);
            await using var client = await DevToolsPageClient.ConnectAsync(target.WebSocketDebuggerUrl, cancellationToken)
                .ConfigureAwait(false);

            await client.SendAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
            await client.SendAsync("Runtime.enable", null, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(
                    "Page.addScriptToEvaluateOnNewDocument",
                    new
                    {
                        source = BuildRequestCaptureScript()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await client.SendAsync(
                    "Page.navigate",
                    new
                    {
                        url = BuildWrapperPageUrl(iframeUrl)
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var state = await WaitForStreamTokenAsync(client, preferredQuality, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(state.StreamToken))
            {
                return null;
            }

            _logger.LogInformation(
                "Alloha stream token resolved via headless browser. quality={Quality} iframe={IframeUrl}",
                state.CurrentQuality > 0 ? state.CurrentQuality : preferredQuality,
                iframeUrl);

            return state.StreamToken.Trim();
        }
        finally
        {
            await CleanupBrowserProcessAsync(process).ConfigureAwait(false);
            TryDeleteDirectory(userDataDir);
        }
    }

    private static string BuildBrowserArguments(int debugPort, string userDataDir)
    {
        return string.Join(
            " ",
            "--headless=new",
            "--disable-gpu",
            "--mute-audio",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-networking",
            "--disable-blink-features=AutomationControlled",
            "--disable-extensions",
            "--disable-sync",
            "--metrics-recording-only",
            "--autoplay-policy=no-user-gesture-required",
            "--window-size=1280,720",
            $"--user-agent=\"{BrowserUserAgent}\"",
            $"--remote-debugging-port={debugPort}",
            $"--user-data-dir=\"{userDataDir}\"",
            "about:blank");
    }

    private static string BuildWrapperPageUrl(string iframeUrl)
    {
        var safeIframeUrl = WebUtility.HtmlEncode((iframeUrl ?? string.Empty).Trim());
        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <title>YummyKodik Alloha Wrapper</title>
              <style>
                html, body {
                  margin: 0;
                  width: 100%;
                  height: 100%;
                  background: #000;
                  overflow: hidden;
                }

                iframe {
                  border: 0;
                  width: 100vw;
                  height: 100vh;
                }
              </style>
            </head>
            <body>
              <iframe
                src="{{safeIframeUrl}}"
                allow="autoplay; fullscreen"
                referrerpolicy="origin"
                loading="eager"></iframe>
            </body>
            </html>
            """;

        return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
    }

    private static string BuildRequestCaptureScript()
    {
        return """
            (() => {
              const capture = window.__yummyAllohaCapture = window.__yummyAllohaCapture || {
                tokens: [],
                requests: [],
                lastState: {
                  ready: false,
                  reason: 'booting',
                  currentQuality: 0,
                  desiredQuality: 0,
                  streamToken: '',
                  currentManifestUrls: [],
                  expectedManifestUrls: [],
                  capturedRequestUrls: [],
                  capturedTokenCount: 0
                }
              };
              const isTopFrame = (() => {
                try {
                  return window === window.top;
                } catch {
                  return false;
                }
              })();

              const trim = value => typeof value === 'string' ? value.trim() : '';
              const buildRequestPreview = () =>
                Array.isArray(capture.requests)
                  ? capture.requests.map(item => item && typeof item.url === 'string' ? item.url : '').filter(Boolean).slice(-6)
                  : [];

              const mergePayload = payload => {
                if (!payload || typeof payload !== 'object') {
                  return;
                }

                if (payload.kind === 'token') {
                  const token = trim(payload.token);
                  if (token && !capture.tokens.includes(token)) {
                    capture.tokens.push(token);
                    if (capture.tokens.length > 16) {
                      capture.tokens.splice(0, capture.tokens.length - 16);
                    }
                  }

                  return;
                }

                if (payload.kind === 'request' && payload.request && typeof payload.request === 'object') {
                  const request = {
                    url: trim(payload.request.url),
                    method: trim(payload.request.method).toUpperCase(),
                    acceptsControls: trim(payload.request.acceptsControls)
                  };

                  capture.requests.push(request);
                  if (capture.requests.length > 24) {
                    capture.requests.splice(0, capture.requests.length - 24);
                  }

                  if (request.acceptsControls && !capture.tokens.includes(request.acceptsControls)) {
                    capture.tokens.push(request.acceptsControls);
                    if (capture.tokens.length > 16) {
                      capture.tokens.splice(0, capture.tokens.length - 16);
                    }
                  }

                  return;
                }

                if (payload.kind === 'state' && payload.state && typeof payload.state === 'object') {
                  const state = payload.state;
                  capture.lastState = {
                    ready: !!state.ready,
                    reason: trim(state.reason),
                    currentQuality: Number(state.currentQuality || 0),
                    desiredQuality: Number(state.desiredQuality || 0),
                    streamToken: trim(state.streamToken),
                    currentManifestUrls: Array.isArray(state.currentManifestUrls)
                      ? state.currentManifestUrls.map(value => trim(value)).filter(Boolean).slice(0, 8)
                      : [],
                    expectedManifestUrls: Array.isArray(state.expectedManifestUrls)
                      ? state.expectedManifestUrls.map(value => trim(value)).filter(Boolean).slice(0, 8)
                      : [],
                    capturedRequestUrls: buildRequestPreview(),
                    capturedTokenCount: capture.tokens.length
                  };
                }
              };

              if (isTopFrame) {
                if (!capture.topListenerInstalled) {
                  window.addEventListener('message', event => {
                    const payload = event && event.data && event.data.__yummyAlloha;
                    mergePayload(payload);
                  });
                  capture.topListenerInstalled = true;
                }

                return;
              }

              try {
                Object.defineProperty(navigator, 'webdriver', {
                  configurable: true,
                  get: () => undefined
                });
              } catch {
              }

              const postToTop = payload => {
                try {
                  window.top.postMessage({ __yummyAlloha: payload }, '*');
                } catch {
                }
              };

              const readHeaders = source => {
                const result = {};
                if (!source) {
                  return result;
                }

                try {
                  if (typeof Headers !== 'undefined' && source instanceof Headers) {
                    source.forEach((value, key) => {
                      result[String(key)] = String(value);
                    });
                    return result;
                  }
                } catch {
                }

                try {
                  if (Array.isArray(source)) {
                    for (const pair of source) {
                      if (Array.isArray(pair) && pair.length >= 2) {
                        result[String(pair[0])] = String(pair[1]);
                      }
                    }

                    return result;
                  }
                } catch {
                }

                try {
                  if (typeof source.forEach === 'function') {
                    source.forEach((value, key) => {
                      result[String(key)] = String(value);
                    });
                    return result;
                  }
                } catch {
                }

                try {
                  if (typeof source === 'object') {
                    for (const key of Object.keys(source)) {
                      const value = source[key];
                      if (value != null) {
                        result[String(key)] = String(value);
                      }
                    }
                  }
                } catch {
                }

                return result;
              };

              const getHeader = headers => {
                const map = readHeaders(headers);
                return trim(map['Accepts-Controls'] || map['accepts-controls'] || map['ACCEPTS-CONTROLS']);
              };

              const pushToken = token => {
                token = trim(token);
                if (!token) {
                  return;
                }

                if (!capture.tokens.includes(token)) {
                  capture.tokens.push(token);
                  if (capture.tokens.length > 16) {
                    capture.tokens.splice(0, capture.tokens.length - 16);
                  }
                }

                postToTop({
                  kind: 'token',
                  token
                });
              };

              const pushRequest = (url, method, headers) => {
                const acceptsControls = getHeader(headers);
                if (acceptsControls) {
                  pushToken(acceptsControls);
                }

                capture.requests.push({
                  url: trim(url),
                  method: trim(method).toUpperCase(),
                  acceptsControls
                });

                if (capture.requests.length > 24) {
                  capture.requests.splice(0, capture.requests.length - 24);
                }

                postToTop({
                  kind: 'request',
                  request: {
                    url: trim(url),
                    method: trim(method).toUpperCase(),
                    acceptsControls
                  }
                });
              };

              if (!capture.fetchWrapped && typeof window.fetch === 'function') {
                const originalFetch = window.fetch.bind(window);
                window.fetch = function(input, init) {
                  try {
                    let url = '';
                    let method = '';
                    let headers = {};

                    if (typeof input === 'string') {
                      url = input;
                    } else if (input && typeof input.url === 'string') {
                      url = input.url;
                    }

                    if (input && typeof input.method === 'string') {
                      method = input.method;
                    }

                    if (input && input.headers) {
                      headers = readHeaders(input.headers);
                    }

                    if (init && typeof init.method === 'string') {
                      method = init.method;
                    }

                    if (init && init.headers) {
                      headers = Object.assign(headers, readHeaders(init.headers));
                    }

                    pushRequest(url, method || 'GET', headers);
                  } catch {
                  }

                  return originalFetch(input, init);
                };

                capture.fetchWrapped = true;
              }

              if (!capture.xhrWrapped && window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
                const proto = window.XMLHttpRequest.prototype;
                const originalOpen = proto.open;
                const originalSetRequestHeader = proto.setRequestHeader;
                const originalSend = proto.send;

                proto.open = function(method, url) {
                  try {
                    this.__yummyAllohaMethod = method;
                    this.__yummyAllohaUrl = url;
                    this.__yummyAllohaHeaders = {};
                  } catch {
                  }

                  return originalOpen.apply(this, arguments);
                };

                proto.setRequestHeader = function(name, value) {
                  try {
                    const headers = this.__yummyAllohaHeaders || (this.__yummyAllohaHeaders = {});
                    headers[String(name)] = String(value);
                  } catch {
                  }

                  return originalSetRequestHeader.apply(this, arguments);
                };

                proto.send = function() {
                  try {
                    pushRequest(
                      this.__yummyAllohaUrl || '',
                      this.__yummyAllohaMethod || 'GET',
                      this.__yummyAllohaHeaders || {});
                  } catch {
                  }

                  return originalSend.apply(this, arguments);
                };

                capture.xhrWrapped = true;
              }

              const reportPlayerState = () => {
                try {
                  const player = window.player;
                  if (!player) {
                    postToTop({
                      kind: 'state',
                      state: {
                        ready: false,
                        reason: 'player-not-ready',
                        currentQuality: 0,
                        desiredQuality: 0,
                        streamToken: capture.tokens.length > 0 ? capture.tokens[capture.tokens.length - 1] : '',
                        currentManifestUrls: [],
                        expectedManifestUrls: []
                      }
                    });
                    return;
                  }

                  try {
                    if (typeof player.play === 'function' && !player.ended) {
                      const playResult = player.play();
                      if (playResult && typeof playResult.catch === 'function') {
                        playResult.catch(() => {});
                      }
                    }
                  } catch {
                  }

                  const queryCandidates = [
                    player.reloadManifestQuery,
                    player.query,
                    player.input && player.input.reloadManifestQuery,
                    player.input && player.input.query,
                    window.input && window.input.reloadManifestQuery,
                    window.input && window.input.query
                  ].filter(Boolean);

                  for (const candidate of queryCandidates) {
                    try {
                      if (typeof candidate.getStreamToken === 'function') {
                        pushToken(candidate.getStreamToken());
                      }
                    } catch {
                    }

                    pushToken(candidate.token);
                    pushToken(candidate.streamToken);
                    pushToken(candidate.acceptsControls);
                  }

                  const qualities = Object.keys((player.currentSource && player.currentSource.quality) || {})
                    .map(value => Number.parseInt(value, 10))
                    .filter(value => Number.isFinite(value) && value > 0)
                    .sort((left, right) => left - right);
                  const currentQuality = Number(player.quality || 0);
                  const currentManifestUrls = Array.isArray(player.currentManifest)
                    ? player.currentManifest
                        .filter(value => typeof value === 'string' && value.length > 0)
                        .map(value => String(value))
                    : [];

                  postToTop({
                    kind: 'state',
                    state: {
                      ready: capture.tokens.length > 0,
                      reason: capture.tokens.length > 0 ? '' : 'token-pending',
                      currentQuality,
                      desiredQuality: qualities.length > 0 ? qualities[qualities.length - 1] : 0,
                      streamToken: capture.tokens.length > 0 ? capture.tokens[capture.tokens.length - 1] : '',
                      currentManifestUrls,
                      expectedManifestUrls: qualities.length > 0 && player.currentSource && player.currentSource.quality
                        ? Object.values(player.currentSource.quality)
                            .filter(value => typeof value === 'string' && value.length > 0)
                            .flatMap(value => value.split(' or ').map(item => item.trim()).filter(Boolean))
                            .slice(0, 8)
                        : []
                    }
                  });
                } catch (error) {
                  postToTop({
                    kind: 'state',
                    state: {
                      ready: false,
                      reason: String(error),
                      currentQuality: 0,
                      desiredQuality: 0,
                      streamToken: capture.tokens.length > 0 ? capture.tokens[capture.tokens.length - 1] : '',
                      currentManifestUrls: [],
                      expectedManifestUrls: []
                    }
                  });
                }
              };

              reportPlayerState();
              if (!capture.statePollStarted) {
                capture.statePollStarted = true;
                window.setInterval(reportPlayerState, 250);
              }
            })();
            """;
    }

    private static string? FindBrowserExecutable()
    {
        foreach (var candidate in BuildBrowserCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            var resolved = TryFindExecutableOnPath(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildBrowserCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            "chrome.exe",
            "msedge.exe"
        };
    }

    private static string? TryFindExecutableOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var fullPath = Path.Combine(entry, executableName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static async Task<PageTargetInfo> WaitForPageTargetAsync(
        int debugPort,
        Process process,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var deadline = DateTime.UtcNow.Add(DevToolsReadyTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Headless browser exited before DevTools became ready. ExitCode={process.ExitCode}.");
            }

            try
            {
                var response = await httpClient
                    .GetAsync($"http://127.0.0.1:{debugPort}/json/list", cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (!TryReadString(target, "type", out var type) ||
                        !string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadString(target, "webSocketDebuggerUrl", out var webSocketDebuggerUrl) ||
                        string.IsNullOrWhiteSpace(webSocketDebuggerUrl))
                    {
                        continue;
                    }

                    return new PageTargetInfo
                    {
                        WebSocketDebuggerUrl = webSocketDebuggerUrl.Trim()
                    };
                }
            }
            catch (HttpRequestException)
            {
                // DevTools endpoint is still starting up.
            }
            catch (JsonException)
            {
                // Retry while Chrome is warming up.
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Timed out waiting for headless browser DevTools page target.");
    }

    private static async Task<AllohaBrowserState> WaitForStreamTokenAsync(
        DevToolsPageClient client,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(StateReadyTimeout);
        AllohaBrowserState? lastState = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await EvaluateBrowserStateAsync(client, preferredQuality, cancellationToken).ConfigureAwait(false);
            lastState = state;

            if (!string.IsNullOrWhiteSpace(state.StreamToken))
            {
                return state;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for Alloha stream token. quality={lastState?.CurrentQuality ?? 0} desired={lastState?.DesiredQuality ?? preferredQuality} reason={lastState?.Reason ?? "unknown"} capturedTokens={lastState?.CapturedTokenCount ?? 0} currentManifest={string.Join(", ", lastState?.CurrentManifestUrls ?? Array.Empty<string>())} expectedManifest={string.Join(", ", lastState?.ExpectedManifestUrls ?? Array.Empty<string>())} capturedRequests={string.Join(", ", lastState?.CapturedRequestUrls ?? Array.Empty<string>())}");
    }

    private static async Task<AllohaBrowserState> EvaluateBrowserStateAsync(
        DevToolsPageClient client,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        var script = $$"""
            (() => {
              try {
                const capture = window.__yummyAllohaCapture || {};
                const capturedTokens = Array.isArray(capture.tokens)
                  ? capture.tokens
                      .filter(value => typeof value === 'string')
                      .map(value => value.trim())
                      .filter(Boolean)
                  : [];
                const state = capture.lastState && typeof capture.lastState === 'object'
                  ? capture.lastState
                  : {};
                const streamToken = capturedTokens.length > 0
                  ? capturedTokens[capturedTokens.length - 1]
                  : (typeof state.streamToken === 'string' ? state.streamToken.trim() : '');
                const currentQuality = Number(state.currentQuality || 0);
                const desiredQuality = Number(state.desiredQuality || {{preferredQuality}} || 0);
                const currentManifestUrls = Array.isArray(state.currentManifestUrls)
                  ? state.currentManifestUrls.filter(value => typeof value === 'string').map(value => value.trim()).filter(Boolean)
                  : [];
                const expectedManifestUrls = Array.isArray(state.expectedManifestUrls)
                  ? state.expectedManifestUrls.filter(value => typeof value === 'string').map(value => value.trim()).filter(Boolean)
                  : [];
                const ready = !!streamToken || !!state.ready;
                return {
                  ready,
                  reason: ready ? '' : (typeof state.reason === 'string' ? state.reason : 'capture-empty'),
                  currentQuality,
                  desiredQuality,
                  streamToken,
                  currentManifestUrls,
                  expectedManifestUrls,
                  capturedRequestUrls: Array.isArray(capture.requests)
                    ? capture.requests.map(item => item && typeof item.url === 'string' ? item.url : '').filter(Boolean).slice(-6)
                    : [],
                  capturedTokenCount: capturedTokens.length
                };
              } catch (error) {
                return {
                  ready: false,
                  reason: String(error),
                  currentQuality: 0,
                  desiredQuality: 0,
                  streamToken: '',
                  currentManifestUrls: [],
                  expectedManifestUrls: [],
                  capturedRequestUrls: [],
                  capturedTokenCount: 0
                };
              }
            })()
            """;

        var value = await client.EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AllohaBrowserState>(value.GetRawText()) ?? new AllohaBrowserState();
    }

    private static int ReserveFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var child) || child.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = child.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static async Task CleanupBrowserProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class PageTargetInfo
    {
        public string WebSocketDebuggerUrl { get; init; } = string.Empty;
    }

    private sealed class AllohaBrowserState
    {
        public bool Ready { get; init; }
        public string Reason { get; init; } = string.Empty;
        public int CurrentQuality { get; init; }
        public int DesiredQuality { get; init; }
        public string StreamToken { get; init; } = string.Empty;
        public string[] CurrentManifestUrls { get; init; } = Array.Empty<string>();
        public string[] ExpectedManifestUrls { get; init; } = Array.Empty<string>();
        public string[] CapturedRequestUrls { get; init; } = Array.Empty<string>();
        public int CapturedTokenCount { get; init; }
    }
}

internal sealed class DevToolsPageClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _receiveLoop;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _sync = new();
    private long _nextId;

    private DevToolsPageClient(ClientWebSocket socket)
    {
        _socket = socket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<DevToolsPageClient> ConnectAsync(string webSocketDebuggerUrl, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(webSocketDebuggerUrl), cancellationToken).ConfigureAwait(false);
        return new DevToolsPageClient(socket);
    }

    public async Task SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await SendCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendCoreAsync(
                "Runtime.evaluate",
                new
                {
                    expression,
                    returnByValue = true
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.TryGetProperty("result", out var innerResult))
        {
            throw new InvalidOperationException("DevTools evaluate result does not contain a value.");
        }

        if (innerResult.TryGetProperty("value", out var value))
        {
            return value.Clone();
        }

        return innerResult.Clone();
    }

    private async Task<JsonElement> SendCoreAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pending[id] = tcs;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters
        });

        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.Register(
            static state =>
            {
                var completion = (TaskCompletionSource<JsonElement>)state!;
                completion.TrySetCanceled();
            },
            tcs);

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[32 * 1024];
        var segment = new ArraySegment<byte>(buffer);

        try
        {
            while (!_disposeCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(segment, _disposeCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                stream.Position = 0;
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: _disposeCts.Token).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.Number ||
                    !idElement.TryGetInt64(out var id))
                {
                    continue;
                }

                TaskCompletionSource<JsonElement>? completion = null;
                lock (_sync)
                {
                    if (_pending.TryGetValue(id, out completion))
                    {
                        _pending.Remove(id);
                    }
                }

                if (completion == null)
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException("DevTools error: " + error.GetRawText()));
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("result", out var responseResult))
                {
                    completion.TrySetResult(default);
                    continue;
                }

                completion.TrySetResult(responseResult.Clone());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disposal path.
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(ex);
                }

                _pending.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore close failures during cleanup.
            }
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
            // Ignore loop failures during cleanup.
        }

        _socket.Dispose();
        _disposeCts.Dispose();
    }
}
