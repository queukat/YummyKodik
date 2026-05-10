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
        if (string.IsNullOrWhiteSpace(url) ||
            source == null ||
            string.IsNullOrWhiteSpace(source.MovieToken) ||
            string.IsNullOrWhiteSpace(source.RequestToken) ||
            source.TranslationId <= 0 ||
            source.SeasonNumber <= 0 ||
            string.IsNullOrWhiteSpace(source.RefererUrl))
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
        request = new YummyStreamRequest();

        var dict = ParseQueryToDictionary(u.Query);

        if (dict.TryGetValue("provider", out var providerRaw) &&
            TryParseYummyProviderKind(providerRaw, out var providerKind))
        {
            if ((!dict.TryGetValue("animeId", out var animeIdRaw) &&
                 !dict.TryGetValue("anime_id", out animeIdRaw)) ||
                !long.TryParse(animeIdRaw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId) ||
                animeId <= 0)
            {
                return false;
            }

            int? cvhEpisode = null;
            string? cvhEpisodeRaw = null;
            if (dict.TryGetValue("ep", out var cvhEp1))
            {
                cvhEpisodeRaw = cvhEp1;
            }
            else if (dict.TryGetValue("episode", out var cvhEp2))
            {
                cvhEpisodeRaw = cvhEp2;
            }

            if (!string.IsNullOrWhiteSpace(cvhEpisodeRaw) &&
                int.TryParse(cvhEpisodeRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cvhEp) &&
                cvhEp > 0)
            {
                cvhEpisode = cvhEp;
            }

            dict.TryGetValue("voice", out var voice);
            dict.TryGetValue("allohaMovieToken", out var allohaMovieToken);
            dict.TryGetValue("allohaRequestToken", out var allohaRequestToken);
            dict.TryGetValue("allohaHidden", out var allohaHidden);
            dict.TryGetValue("allohaRefererUrl", out var allohaRefererUrl);

            var allohaTranslationId = 0;
            if (dict.TryGetValue("allohaTranslationId", out var allohaTranslationIdRaw))
            {
                int.TryParse(
                    allohaTranslationIdRaw?.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out allohaTranslationId);
            }

            var allohaSeasonNumber = 0;
            if (dict.TryGetValue("allohaSeason", out var allohaSeasonRaw))
            {
                int.TryParse(
                    allohaSeasonRaw?.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out allohaSeasonNumber);
            }

            request = new YummyStreamRequest
            {
                Provider = providerKind,
                AnimeId = animeId,
                Episode = cvhEpisode,
                VoiceName = (voice ?? string.Empty).Trim(),
                AllohaMovieToken = (allohaMovieToken ?? string.Empty).Trim(),
                AllohaRequestToken = (allohaRequestToken ?? string.Empty).Trim(),
                AllohaTranslationId = allohaTranslationId > 0 ? allohaTranslationId : 0,
                AllohaSeasonNumber = allohaSeasonNumber > 0 ? allohaSeasonNumber : 0,
                AllohaHidden = (allohaHidden ?? string.Empty).Trim(),
                AllohaRefererUrl = (allohaRefererUrl ?? string.Empty).Trim()
            };

            return true;
        }

        if (!dict.TryGetValue("type", out var typeRaw) || string.IsNullOrWhiteSpace(typeRaw))
        {
            return false;
        }

        if (!Enum.TryParse(typeRaw.Trim(), ignoreCase: true, out KodikIdType idType))
        {
            return false;
        }

        if (!dict.TryGetValue("id", out var idRaw) || string.IsNullOrWhiteSpace(idRaw))
        {
            return false;
        }

        var id = idRaw.Trim();

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
            request = new YummyStreamRequest
            {
                Provider = YummyStreamProviderKind.Kodik,
                KodikIdType = idType,
                KodikId = id,
                Episode = ep
            };

            return true;
        }

        request = new YummyStreamRequest
        {
            Provider = YummyStreamProviderKind.Kodik,
            KodikIdType = idType,
            KodikId = id
        };

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
