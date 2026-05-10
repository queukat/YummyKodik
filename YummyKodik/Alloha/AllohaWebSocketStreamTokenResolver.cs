using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Alloha;

internal sealed class AllohaWebSocketStreamTokenResolver
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private const string AllohaOrigin = "https://alloha.yani.tv";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
    private const string BrowserAcceptLanguage = "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7";

    private readonly ILogger _logger;

    public AllohaWebSocketStreamTokenResolver(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ResolveStreamTokenAsync(
        AllohaStreamTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WebSocketBaseUrl) ||
            string.IsNullOrWhiteSpace(request.WebSocketSessionId) ||
            string.IsNullOrWhiteSpace(request.AudioTrackId))
        {
            _logger.LogDebug(
                "Alloha websocket token resolution skipped. hasWs={HasWs} hasSessionId={HasSessionId} audioTrackId={AudioTrackId}",
                !string.IsNullOrWhiteSpace(request.WebSocketBaseUrl),
                !string.IsNullOrWhiteSpace(request.WebSocketSessionId),
                request.AudioTrackId);
            return null;
        }

        var websocketUrl = BuildWebSocketUrl(request.WebSocketBaseUrl, request.WebSocketSessionId);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", AllohaOrigin);
        socket.Options.SetRequestHeader("User-Agent", BrowserUserAgent);
        socket.Options.SetRequestHeader("Accept-Language", BrowserAcceptLanguage);

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new Uri(websocketUrl), connectCts.Token).ConfigureAwait(false);
            }

            var payload = BuildPlaybackStartPayload(request);
            await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                string? message;
                using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveCts.CancelAfter(ReceiveTimeout);

                    try
                    {
                        message = await ReceiveMessageAsync(socket, receiveCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    break;
                }

                if (TryExtractEdgeHash(message, out var edgeHash, out var ttlSeconds, out var priority))
                {
                    _logger.LogInformation(
                        "Alloha stream token resolved via websocket. quality={Quality} audioTrackId={AudioTrackId} ttl={TtlSeconds} priority={Priority}",
                        request.SelectedQuality,
                        request.AudioTrackId,
                        ttlSeconds,
                        priority);

                    return edgeHash;
                }
            }

            _logger.LogDebug(
                "Alloha websocket token resolution finished without edge hash. quality={Quality} audioTrackId={AudioTrackId} ws={WebSocketUrl}",
                request.SelectedQuality,
                request.AudioTrackId,
                websocketUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Alloha websocket token resolution failed. quality={Quality} audioTrackId={AudioTrackId} ws={WebSocketUrl}",
                request.SelectedQuality,
                request.AudioTrackId,
                websocketUrl);
        }
        finally
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    closeCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }

        return null;
    }

    private static string BuildWebSocketUrl(string baseUrl, string sessionId)
    {
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return baseUrl + separator + "sid=" + Uri.EscapeDataString(sessionId) + "&v=2.1&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string BuildPlaybackStartPayload(AllohaStreamTokenRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "playback_start",
            ["current_time"] = 0,
            ["resolution"] = Math.Max(0, request.SelectedQuality).ToString(),
            ["track_id"] = request.AudioTrackId,
            ["speed"] = 1,
            ["subtitle"] = null,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return JsonSerializer.Serialize(payload);
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    private static bool TryExtractEdgeHash(string rawMessage, out string edgeHash, out int ttlSeconds, out int priority)
    {
        edgeHash = string.Empty;
        ttlSeconds = 0;
        priority = 0;

        try
        {
            using var doc = JsonDocument.Parse(rawMessage);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "config_update", StringComparison.Ordinal))
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("ttl", out var ttlElement) && ttlElement.TryGetInt32(out var ttlValue))
            {
                ttlSeconds = ttlValue;
            }

            if (doc.RootElement.TryGetProperty("edge_priority", out var priorityElement) &&
                priorityElement.TryGetInt32(out var priorityValue))
            {
                priority = priorityValue;
            }

            if (!doc.RootElement.TryGetProperty("edge_hash", out var hashElement))
            {
                return false;
            }

            edgeHash = (hashElement.GetString() ?? string.Empty).Trim();
            return edgeHash.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
