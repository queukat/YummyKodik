using System;
using System.Net;

namespace YummyKodik.Web;

public static class JellyfinWebIndexPatcher
{
    public const string StartMarker = "<!-- YummyKodik: seriesTranslation bootstrap start -->";
    public const string EndMarker = "<!-- YummyKodik: seriesTranslation bootstrap end -->";

    public static bool TryInjectSeriesTranslationScript(string html, string scriptUrl, out string patchedHtml)
    {
        patchedHtml = html ?? string.Empty;
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(scriptUrl))
        {
            return false;
        }

        var newline = DetectNewline(html);
        var snippet = BuildManagedSnippet(scriptUrl, newline);

        var startIndex = html.IndexOf(StartMarker, StringComparison.Ordinal);
        var endIndex = html.IndexOf(EndMarker, StringComparison.Ordinal);
        if (startIndex >= 0 && endIndex > startIndex)
        {
            endIndex += EndMarker.Length;
            var existing = html.AsSpan(startIndex, endIndex - startIndex);
            if (existing.Equals(snippet.AsSpan(), StringComparison.Ordinal))
            {
                return false;
            }

            patchedHtml = string.Concat(html.AsSpan(0, startIndex), snippet, html.AsSpan(endIndex));
            return true;
        }

        if (html.Contains("ConfigurationPage?name=seriesTranslation.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var headCloseIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headCloseIndex >= 0)
        {
            patchedHtml = html.Insert(headCloseIndex, snippet + newline);
            return true;
        }

        patchedHtml = html + newline + snippet;
        return true;
    }

    public static string BuildManagedSnippet(string scriptUrl, string newline = "\n")
    {
        var encodedUrl = WebUtility.HtmlEncode(scriptUrl);
        return string.Join(
            newline,
            StartMarker,
            $"<script defer=\"defer\" src=\"{encodedUrl}\"></script>",
            EndMarker);
    }

    private static string DetectNewline(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }
}
