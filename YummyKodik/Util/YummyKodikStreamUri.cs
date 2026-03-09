using System;
using System.Collections.Generic;
using System.Globalization;
using YummyKodik.Kodik;

namespace YummyKodik.Util;

public static class YummyKodikStreamUri
{
    public const string OldScheme = "yummy-kodik://";

    /// <summary>
    /// Parses both:
    ///  1) Old scheme: yummy-kodik://{type}/{id}/{ep?}
    ///  2) New scheme: http(s)://.../YummyKodik/stream?type=...&id=...&ep=...
    ///
    /// Episode is optional here because some call sites only need (type,id).
    /// </summary>
    public static bool TryParse(string uri, out KodikIdType idType, out string id, out int? episode)
    {
        idType = default;
        id = string.Empty;
        episode = null;

        var s = (uri ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return false;
        }

        if (s.StartsWith(OldScheme, StringComparison.OrdinalIgnoreCase))
        {
            return TryParseOldScheme(s, out idType, out id, out episode);
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return false;
        }

        return TryParseHttpUrl(u, out idType, out id, out episode);
    }

    private static bool TryParseOldScheme(string uri, out KodikIdType idType, out string id, out int? episode)
    {
        idType = default;
        id = string.Empty;
        episode = null;

        var tail = uri.Length > OldScheme.Length ? uri[OldScheme.Length..] : string.Empty;

        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!Enum.TryParse(parts[0], ignoreCase: true, out idType))
        {
            return false;
        }

        id = (parts[1] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (parts.Length >= 3)
        {
            var epRaw = (parts[2] ?? string.Empty).Trim();
            if (int.TryParse(epRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ep) && ep > 0)
            {
                episode = ep;
            }
        }

        return true;
    }

    private static bool TryParseHttpUrl(Uri u, out KodikIdType idType, out string id, out int? episode)
    {
        idType = default;
        id = string.Empty;
        episode = null;

        var dict = ParseQueryToDictionary(u.Query);

        if (!dict.TryGetValue("type", out var typeRaw) || string.IsNullOrWhiteSpace(typeRaw))
        {
            return false;
        }

        if (!Enum.TryParse(typeRaw.Trim(), ignoreCase: true, out idType))
        {
            return false;
        }

        if (!dict.TryGetValue("id", out var idRaw) || string.IsNullOrWhiteSpace(idRaw))
        {
            return false;
        }

        id = idRaw.Trim();

        // Episode is optional for some call sites.
        string? epRaw = null;
        if (dict.TryGetValue("ep", out var ep1))
        {
            epRaw = ep1;
        }
        else if (dict.TryGetValue("episode", out var ep2))
        {
            epRaw = ep2;
        }

        if (!string.IsNullOrWhiteSpace(epRaw) &&
            int.TryParse(epRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ep) &&
            ep > 0)
        {
            episode = ep;
        }

        return true;
    }

    public static Dictionary<string, string> ParseQueryToDictionary(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(query))
        {
            return dict;
        }

        var q = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;
        if (q.Length == 0)
        {
            return dict;
        }

        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var rawKey = part.Substring(0, eq);
            var rawVal = part.Substring(eq + 1);

            var k = SafeUnescape(rawKey);
            var v = SafeUnescape(rawVal);

            if (string.IsNullOrWhiteSpace(k))
            {
                continue;
            }

            dict[k] = v;
        }

        return dict;
    }

    private static string SafeUnescape(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch
        {
            return s;
        }
    }
}
