using System;
using System.Collections.Generic;
using System.Globalization;
using YummyKodik.Kodik;
using YummyKodik.Yummy;

namespace YummyKodik.Util;

public static class YummyKodikStreamUri
{
    public const string OldScheme = "yummy-kodik://";
    public const string CvhProvider = "cvh";
    public const string AllohaProvider = "alloha";

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

        if (!TryParseRequest(uri, out var request) || request.Provider != YummyStreamProviderKind.Kodik)
        {
            return false;
        }

        idType = request.KodikIdType;
        id = request.KodikId;
        episode = request.Episode;
        return true;
    }

    public static bool TryParseRequest(string uri, out YummyStreamRequest request)
    {
        request = new YummyStreamRequest();

        var s = (uri ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return false;
        }

        if (s.StartsWith(OldScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOldScheme(s, out var oldIdType, out var oldId, out var oldEpisode))
            {
                return false;
            }

            request = new YummyStreamRequest
            {
                Provider = YummyStreamProviderKind.Kodik,
                KodikIdType = oldIdType,
                KodikId = oldId,
                Episode = oldEpisode
            };

            return true;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            return false;
        }

        return TryParseHttpUrl(u, out request);
    }

    public static string BuildCvhHttpUrl(string baseUrl, long animeId, int episode, string? voiceName = null)
    {
        return BuildYummyProviderHttpUrl(baseUrl, CvhProvider, animeId, episode, voiceName);
    }

    public static string BuildAllohaHttpUrl(
        string baseUrl,
        long animeId,
        int episode,
        string? voiceName = null,
        YummyAllohaSource? source = null)
    {
        var url = BuildYummyProviderHttpUrl(baseUrl, AllohaProvider, animeId, episode, voiceName);
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (source == null)
        {
            return url;
        }

        if (!HasEmbeddableAllohaSource(source))
        {
            return url;
        }

        url = AppendQueryParameter(url, "allohaMovieToken", source.MovieToken);
        url = AppendQueryParameter(url, "allohaRequestToken", source.RequestToken);
        url = AppendQueryParameter(url, "allohaTranslationId", source.TranslationId.ToString(CultureInfo.InvariantCulture));
        url = AppendQueryParameter(url, "allohaSeason", source.SeasonNumber.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(source.Hidden))
        {
            url = AppendQueryParameter(url, "allohaHidden", source.Hidden);
        }

        return AppendQueryParameter(url, "allohaRefererUrl", source.RefererUrl);
    }

    private static string BuildYummyProviderHttpUrl(string baseUrl, string provider, long animeId, int episode, string? voiceName = null)
    {
        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var url = $"{root}/YummyKodik/stream?provider={provider}&animeId={animeId}&ep={episode}";
        var voice = (voiceName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(voice))
        {
            url += "&voice=" + Uri.EscapeDataString(voice);
        }

        return url;
    }

    private static string AppendQueryParameter(string url, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        return url + "&" + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
    }

    private static bool HasEmbeddableAllohaSource(YummyAllohaSource source)
    {
        if (string.IsNullOrWhiteSpace(source.MovieToken))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(source.RequestToken))
        {
            return false;
        }

        if (source.TranslationId <= 0)
        {
            return false;
        }

        if (source.SeasonNumber <= 0)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(source.RefererUrl);
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

    private static bool TryParseHttpUrl(Uri u, out YummyStreamRequest request)
    {
        var dict = ParseQueryToDictionary(u.Query);

        if (dict.TryGetValue("provider", out var providerRaw) &&
            TryParseYummyProviderKind(providerRaw, out var providerKind))
        {
            return TryParseYummyProviderRequest(dict, providerKind, out request);
        }

        return TryParseKodikHttpRequest(dict, out request);
    }

    private static bool TryParseYummyProviderRequest(
        IReadOnlyDictionary<string, string> query,
        YummyStreamProviderKind providerKind,
        out YummyStreamRequest request)
    {
        request = new YummyStreamRequest();

        if (!TryReadPositiveLongQueryValue(query, out var animeId, "animeId", "anime_id"))
        {
            return false;
        }

        request = new YummyStreamRequest
        {
            Provider = providerKind,
            AnimeId = animeId,
            Episode = ReadPositiveIntQueryValue(query, "ep", "episode"),
            VoiceName = ReadTrimmedQueryValue(query, "voice"),
            AllohaMovieToken = ReadTrimmedQueryValue(query, "allohaMovieToken"),
            AllohaRequestToken = ReadTrimmedQueryValue(query, "allohaRequestToken"),
            AllohaTranslationId = ReadPositiveIntQueryValue(query, "allohaTranslationId") ?? 0,
            AllohaSeasonNumber = ReadPositiveIntQueryValue(query, "allohaSeason") ?? 0,
            AllohaHidden = ReadTrimmedQueryValue(query, "allohaHidden"),
            AllohaRefererUrl = ReadTrimmedQueryValue(query, "allohaRefererUrl")
        };

        return true;
    }

    private static bool TryParseKodikHttpRequest(
        IReadOnlyDictionary<string, string> query,
        out YummyStreamRequest request)
    {
        request = new YummyStreamRequest();

        if (!TryReadKodikIdType(query, out var idType))
        {
            return false;
        }

        if (!TryReadRequiredStringQueryValue(query, "id", out var id))
        {
            return false;
        }

        request = new YummyStreamRequest
        {
            Provider = YummyStreamProviderKind.Kodik,
            KodikIdType = idType,
            KodikId = id,
            Episode = ReadPositiveIntQueryValue(query, "ep", "episode")
        };

        return true;
    }

    private static bool TryReadKodikIdType(
        IReadOnlyDictionary<string, string> query,
        out KodikIdType idType)
    {
        idType = default;

        return TryReadRequiredStringQueryValue(query, "type", out var typeRaw) &&
               Enum.TryParse(typeRaw, ignoreCase: true, out idType);
    }

    private static bool TryReadRequiredStringQueryValue(
        IReadOnlyDictionary<string, string> query,
        string key,
        out string value)
    {
        value = ReadTrimmedQueryValue(query, key);
        return value.Length > 0;
    }

    private static string ReadTrimmedQueryValue(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) ? (value ?? string.Empty).Trim() : string.Empty;
    }

    private static int? ReadPositiveIntQueryValue(IReadOnlyDictionary<string, string> query, params string[] keys)
    {
        if (!TryReadFirstQueryValue(query, out var rawValue, keys))
        {
            return null;
        }

        return int.TryParse(rawValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
               value > 0
            ? value
            : null;
    }

    private static bool TryReadPositiveLongQueryValue(
        IReadOnlyDictionary<string, string> query,
        out long value,
        params string[] keys)
    {
        value = 0;
        if (!TryReadFirstQueryValue(query, out var rawValue, keys))
        {
            return false;
        }

        return long.TryParse(rawValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

    private static bool TryReadFirstQueryValue(
        IReadOnlyDictionary<string, string> query,
        out string? value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public static Dictionary<string, string> ParseQueryToDictionary(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(query))
        {
            return dict;
        }

        var q = query.StartsWith('?') ? query[1..] : query;
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
        catch (UriFormatException)
        {
            return s;
        }
    }

    private static bool TryParseYummyProviderKind(string? value, out YummyStreamProviderKind providerKind)
    {
        providerKind = YummyStreamProviderKind.Unknown;

        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalized, CvhProvider, StringComparison.OrdinalIgnoreCase))
        {
            providerKind = YummyStreamProviderKind.Cvh;
            return true;
        }

        if (string.Equals(normalized, AllohaProvider, StringComparison.OrdinalIgnoreCase))
        {
            providerKind = YummyStreamProviderKind.Alloha;
            return true;
        }

        return false;
    }
}
