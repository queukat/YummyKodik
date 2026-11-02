using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YummyKodik.Alloha;

// Diagnostic-only reproduction of the player channel. No production registration.
internal sealed class AllohaLiveTokenChannel : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    internal Action<string>? TokenUpdated { get; set; }
    internal string? Token { get; private set; }

    internal async Task<string?> ResolveAsync(AllohaStreamTokenRequest request, CancellationToken ct)
    {
        await DisposeAsync();
        Token = null;
        Console.WriteLine($"Live channel requested: endpoint={!string.IsNullOrEmpty(request.WebSocketBaseUrl)} session={!string.IsNullOrEmpty(request.WebSocketSessionId)} track={!string.IsNullOrEmpty(request.AudioTrackId)}");
        if (string.IsNullOrEmpty(request.WebSocketBaseUrl) || string.IsNullOrEmpty(request.WebSocketSessionId)) return null;
        var socket = _socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://alloha.yani.tv");
        socket.Options.SetRequestHeader("User-Agent", request.UserAgent);
        socket.Options.SetRequestHeader("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.7");
        _stop = new CancellationTokenSource();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(new Uri(request.WebSocketBaseUrl + (request.WebSocketBaseUrl.Contains('?') ? "&" : "?") +
                "sid=" + Uri.EscapeDataString(request.WebSocketSessionId) + "&v=2.1&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), connect.Token);
        }
        catch (Exception error)
        {
            Console.WriteLine($"Live channel connect failed: {error.GetType().Name}; code={(error as WebSocketException)?.WebSocketErrorCode}");
            throw;
        }
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var began = System.Diagnostics.Stopwatch.StartNew();
        async Task Send(string type, CancellationToken stop)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { type, current_time = (int)began.Elapsed.TotalSeconds,
                resolution = request.SelectedQuality.ToString(), track_id = request.AudioTrackId, speed = 1, subtitle = -1,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await socket.SendAsync(body, WebSocketMessageType.Text, true, stop);
            Console.WriteLine($"Live channel sent {type} at {began.Elapsed.TotalSeconds:F1}s");
        }
        async Task Receive(CancellationToken stop)
        {
            var bytes = new byte[4096];
            while (!stop.IsCancellationRequested)
            {
                using var body = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(bytes, stop);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine($"Live channel closed: code={result.CloseStatus}");
                        return;
                    }
                    body.Write(bytes, 0, result.Count);
                    if (body.Length > 65536) throw new InvalidOperationException("Oversized diagnostic channel message.");
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(body.ToArray());
                if (json.RootElement.TryGetProperty("edge_hash", out var hash) && hash.ValueKind == JsonValueKind.String)
                {
                    Token = hash.GetString();
                    if (!string.IsNullOrEmpty(Token))
                    {
                        TokenUpdated?.Invoke(Token);
                        first.TrySetResult(Token);
                        Console.WriteLine($"Live channel received token at {began.Elapsed.TotalSeconds:F1}s");
                    }
                }
            }
        }
        async Task Heartbeat(CancellationToken stop)
        {
            await Send("playback_start", stop);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stop)) await Send("playing", stop);
        }
        async Task Run(CancellationToken stop)
        {
            try { await Task.WhenAll(Receive(stop), Heartbeat(stop)); }
            catch (Exception ex) { first.TrySetException(ex); Console.WriteLine($"Live channel stopped: {ex.GetType().Name}"); }
        }
        _worker = Run(_stop.Token);
        try { return await first.Task.WaitAsync(TimeSpan.FromSeconds(6), ct); }
        catch (TimeoutException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        _socket?.Abort();
        if (_worker != null) await _worker;
        _socket?.Dispose();
        _stop?.Dispose();
        _socket = null; _stop = null; _worker = null;
    }
}
