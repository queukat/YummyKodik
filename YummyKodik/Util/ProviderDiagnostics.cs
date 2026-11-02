namespace YummyKodik.Util;

// Provider payloads and URL paths can contain opaque bearer credentials. Log metadata only.
internal static class ProviderDiagnostics
{
    internal static string DescribePayload(string? value) => $"[payload omitted; chars={value?.Length ?? 0}]";

    internal static string DescribeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}/[path omitted]"
            : "[URL omitted]";
}
