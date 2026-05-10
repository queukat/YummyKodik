namespace YummyKodik.Alloha;

internal sealed class AllohaStreamTokenRequest
{
    public string IframeUrl { get; init; } = string.Empty;
    public string WebSocketBaseUrl { get; init; } = string.Empty;
    public string WebSocketSessionId { get; init; } = string.Empty;
    public string AudioTrackId { get; init; } = string.Empty;
    public int SelectedQuality { get; init; }
}
