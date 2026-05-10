using System.Globalization;
using YummyKodik.Util;

namespace YummyKodik.Yummy;

public sealed class YummyVideoCatalog
{
    private static readonly YummyVideoProviderKind[] DefaultProviderPreference =
    {
        YummyVideoProviderKind.Alloha,
        YummyVideoProviderKind.Cvh
    };

    private readonly IReadOnlyList<YummyVideoEntry> _entries;

    private YummyVideoCatalog(IReadOnlyList<YummyVideoEntry> entries)
    {
        _entries = entries;
    }

    public static YummyVideoCatalog Create(
        YummyAnimeResponse? anime,
        IEnumerable<YummyVideoEntry>? additionalEntries = null)
    {
        if (anime?.Videos == null || anime.Videos.Count == 0)
        {
            var emptyEntries = new List<YummyVideoEntry>();
            MergeAdditionalEntries(emptyEntries, additionalEntries);
            return new YummyVideoCatalog(emptyEntries);
        }

        var entries = new List<YummyVideoEntry>(anime.Videos.Count);

        foreach (var video in anime.Videos)
        {
            if (!TryParseEpisodeNumber(video?.Number, out var episodeNumber) || episodeNumber <= 0)
            {
                continue;
            }

            var provider = ParseProviderKind(video?.Data?.PlayerId ?? 0);
            var rawDubbing = (video?.Data?.Dubbing ?? string.Empty).Trim();
            var displayVoiceName = NormalizeVoiceName(rawDubbing);

            var entry = new YummyVideoEntry
            {
                EpisodeNumber = episodeNumber,
                Provider = provider,
                RawDubbing = rawDubbing,
                DisplayVoiceName = displayVoiceName,
                DurationSeconds = Math.Max(0, video?.Duration ?? 0),
                Skips = video?.Skips,
                IframeUrl = NormalizeUrl(video?.IframeUrl),
                Cvh = TryParseCvhSource(video?.IframeUrl),
                Alloha = TryParseAllohaSource(video?.IframeUrl)
            };

            entries.Add(entry);
        }

        MergeAdditionalEntries(entries, additionalEntries);
        return new YummyVideoCatalog(entries);
    }

    public bool HasAnyCvhEpisodes => HasAnyEpisodes(YummyVideoProviderKind.Cvh);

    public bool HasAnyAllohaEpisodes => HasAnyEpisodes(YummyVideoProviderKind.Alloha);

    public bool HasAnyEpisodes(YummyVideoProviderKind provider)
    {
        return _entries.Any(x => MatchesProvider(x, provider));
    }

    public IReadOnlyList<int> GetSupportedEpisodeNumbersAcrossProviders(IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        var providerSet = NormalizeProviderSet(providers);
        return _entries
            .Where(x => providerSet.Contains(x.Provider) && MatchesProvider(x, x.Provider))
            .Select(x => x.EpisodeNumber)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    public IReadOnlyList<int> GetSupportedEpisodeNumbers()
    {
        return GetSupportedEpisodeNumbers(YummyVideoProviderKind.Cvh);
    }

    public IReadOnlyList<int> GetSupportedEpisodeNumbers(YummyVideoProviderKind provider)
    {
        return FilterProviderEntries(provider)
            .Select(x => x.EpisodeNumber)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    public int? GetFirstSupportedEpisodeNumber()
    {
        return GetFirstSupportedEpisodeNumber(YummyVideoProviderKind.Cvh);
    }

    public int? GetFirstSupportedEpisodeNumber(YummyVideoProviderKind provider)
    {
        var value = GetSupportedEpisodeNumbers(provider).FirstOrDefault();
        return value > 0 ? value : null;
    }

    public IReadOnlyList<string> GetAllVoiceNames()
    {
        return GetAllVoiceNames(YummyVideoProviderKind.Cvh);
    }

    public IReadOnlyList<string> GetAllVoiceNames(YummyVideoProviderKind provider)
    {
        return FilterProviderEntries(provider)
            .Select(x => x.DisplayVoiceName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetAllVoiceNamesAcrossProviders(IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        var providerSet = NormalizeProviderSet(providers);
        return _entries
            .Where(x => providerSet.Contains(x.Provider) && MatchesProvider(x, x.Provider))
            .Select(x => x.DisplayVoiceName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetSupportedVoiceNames(int episodeNumber)
    {
        return GetSupportedVoiceNames(YummyVideoProviderKind.Cvh, episodeNumber);
    }

    public IReadOnlyList<string> GetSupportedVoiceNames(YummyVideoProviderKind provider, int episodeNumber)
    {
        return FilterProviderEntries(provider)
            .Where(x => x.EpisodeNumber == episodeNumber)
            .Select(x => x.DisplayVoiceName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetSupportedVoiceNamesAcrossProviders(
        int episodeNumber,
        IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        var providerSet = NormalizeProviderSet(providers);
        return _entries
            .Where(x => x.EpisodeNumber == episodeNumber && providerSet.Contains(x.Provider) && MatchesProvider(x, x.Provider))
            .Select(x => x.DisplayVoiceName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public YummyVideoEntry? FindPreferredPlayableEntry(int episodeNumber, string? preferredVoiceName = null)
    {
        return FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, episodeNumber, preferredVoiceName);
    }

    public YummyVideoEntry? FindPreferredPlayableEntry(
        YummyVideoProviderKind provider,
        int episodeNumber,
        string? preferredVoiceName = null)
    {
        return FindMatchingPlayableEntry(provider, episodeNumber, preferredVoiceName, allowFallback: true);
    }

    public YummyVideoProviderKind? PickPreferredProvider(
        int episodeNumber,
        string? explicitVoiceName = null,
        string? preferredFilter = null,
        IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        var providerOrder = NormalizeProviderOrder(providers);
        var normalizedExplicitVoice = NormalizeVoiceName(explicitVoiceName);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitVoice))
        {
            foreach (var provider in providerOrder)
            {
                if (FindMatchingPlayableEntry(provider, episodeNumber, normalizedExplicitVoice, allowFallback: false) != null)
                {
                    return provider;
                }
            }
        }

        foreach (var token in StringTokenParser.ParseTokens(preferredFilter))
        {
            foreach (var provider in providerOrder)
            {
                if (FindMatchingPlayableEntry(provider, episodeNumber, token, allowFallback: false) != null)
                {
                    return provider;
                }
            }
        }

        foreach (var provider in providerOrder)
        {
            if (FindPreferredPlayableEntry(provider, episodeNumber) != null)
            {
                return provider;
            }
        }

        return null;
    }

    public YummyVideoSkips? GetSkips(int episodeNumber, string? preferredVoiceName = null)
    {
        return GetSkips(YummyVideoProviderKind.Cvh, episodeNumber, preferredVoiceName);
    }

    public YummyVideoSkips? GetSkips(YummyVideoProviderKind provider, int episodeNumber, string? preferredVoiceName = null)
    {
        return FindPreferredPlayableEntry(provider, episodeNumber, preferredVoiceName)?.Skips;
    }

    public YummyVideoSkips? GetSkipsAcrossProviders(
        int episodeNumber,
        string? preferredVoiceName = null,
        IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        return FindPreferredEntryWithSkipsAcrossProviders(episodeNumber, preferredVoiceName, providers)?.Skips;
    }

    public YummyVideoEntry? FindPreferredEntryWithSkipsAcrossProviders(
        int episodeNumber,
        string? preferredVoiceName = null,
        IEnumerable<YummyVideoProviderKind>? providers = null)
    {
        var providerOrder = NormalizeProviderOrder(providers);
        var normalizedPreferredVoice = NormalizeVoiceName(preferredVoiceName);

        foreach (var provider in providerOrder)
        {
            var exactMatch = FindMatchingEntryWithSkips(provider, episodeNumber, normalizedPreferredVoice, allowFallback: false);
            if (exactMatch != null)
            {
                return exactMatch;
            }
        }

        foreach (var provider in providerOrder)
        {
            var fallbackMatch = FindMatchingEntryWithSkips(provider, episodeNumber, normalizedPreferredVoice, allowFallback: true);
            if (fallbackMatch != null)
            {
                return fallbackMatch;
            }
        }

        return null;
    }

    public int? GetDurationSeconds(int episodeNumber, string? preferredVoiceName = null)
    {
        return GetDurationSeconds(YummyVideoProviderKind.Cvh, episodeNumber, preferredVoiceName);
    }

    public int? GetDurationSeconds(YummyVideoProviderKind provider, int episodeNumber, string? preferredVoiceName = null)
    {
        var duration = FindPreferredPlayableEntry(provider, episodeNumber, preferredVoiceName)?.DurationSeconds ?? 0;
        return duration > 0 ? duration : null;
    }

    public string? PickPreferredVoiceName(
        int episodeNumber,
        string? explicitVoiceName,
        string? savedVoiceName,
        string? preferredFilter,
        out string reason)
    {
        return PickPreferredVoiceName(
            YummyVideoProviderKind.Cvh,
            episodeNumber,
            explicitVoiceName,
            savedVoiceName,
            preferredFilter,
            out reason);
    }

    public string? PickPreferredVoiceName(
        YummyVideoProviderKind provider,
        int episodeNumber,
        string? explicitVoiceName,
        string? savedVoiceName,
        string? preferredFilter,
        out string reason)
    {
        var normalizedExplicitVoice = NormalizeVoiceName(explicitVoiceName);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitVoice))
        {
            var explicitMatch = FindPreferredPlayableEntry(provider, episodeNumber, normalizedExplicitVoice);
            if (explicitMatch != null)
            {
                reason = "explicit";
                return explicitMatch.DisplayVoiceName;
            }
        }

        var normalizedSavedVoice = NormalizeVoiceName(savedVoiceName);
        if (!string.IsNullOrWhiteSpace(normalizedSavedVoice))
        {
            var savedMatch = FindPreferredPlayableEntry(provider, episodeNumber, normalizedSavedVoice);
            if (savedMatch != null)
            {
                reason = "saved";
                return savedMatch.DisplayVoiceName;
            }
        }

        foreach (var token in StringTokenParser.ParseTokens(preferredFilter))
        {
            var preferredMatch = FindPreferredPlayableEntry(provider, episodeNumber, token);
            if (preferredMatch != null)
            {
                reason = "preferred-filter";
                return preferredMatch.DisplayVoiceName;
            }
        }

        var fallback = FindPreferredPlayableEntry(provider, episodeNumber);
        if (fallback != null)
        {
            reason = "first-available";
            return fallback.DisplayVoiceName;
        }

        reason = "missing";
        return null;
    }

    public static string NormalizeVoiceName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("Озвучка ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("Озвучка ".Length).Trim();
        }

        return normalized;
    }

    private IEnumerable<YummyVideoEntry> FilterProviderEntries(YummyVideoProviderKind provider)
    {
        return _entries.Where(x => MatchesProvider(x, provider));
    }

    private YummyVideoEntry? FindMatchingPlayableEntry(
        YummyVideoProviderKind provider,
        int episodeNumber,
        string? preferredVoiceName,
        bool allowFallback)
    {
        var episodeEntries = FilterProviderEntries(provider)
            .Where(x => x.EpisodeNumber == episodeNumber)
            .ToList();

        if (episodeEntries.Count == 0)
        {
            return null;
        }

        var normalizedPreferredVoice = NormalizeVoiceName(preferredVoiceName);
        var normalizedPreferredKey = TranslationNameKeyNormalizer.Normalize(normalizedPreferredVoice);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredKey))
        {
            var keyMatch = episodeEntries.FirstOrDefault(x =>
                string.Equals(
                    TranslationNameKeyNormalizer.Normalize(x.DisplayVoiceName),
                    normalizedPreferredKey,
                    StringComparison.Ordinal));

            if (keyMatch != null)
            {
                return keyMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedPreferredVoice))
        {
            var exactMatch = episodeEntries.FirstOrDefault(x =>
                string.Equals(x.DisplayVoiceName, normalizedPreferredVoice, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                return exactMatch;
            }

            var partialMatch = episodeEntries.FirstOrDefault(x =>
                x.DisplayVoiceName.Contains(normalizedPreferredVoice, StringComparison.OrdinalIgnoreCase) ||
                normalizedPreferredVoice.Contains(x.DisplayVoiceName, StringComparison.OrdinalIgnoreCase));

            if (partialMatch != null)
            {
                return partialMatch;
            }
        }

        return allowFallback
            ? episodeEntries.OrderBy(x => x.DisplayVoiceName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    private YummyVideoEntry? FindMatchingEntryWithSkips(
        YummyVideoProviderKind provider,
        int episodeNumber,
        string? preferredVoiceName,
        bool allowFallback)
    {
        var episodeEntries = FilterProviderEntries(provider)
            .Where(x => x.EpisodeNumber == episodeNumber && HasUsableSkips(x.Skips))
            .ToList();

        if (episodeEntries.Count == 0)
        {
            return null;
        }

        var normalizedPreferredVoice = NormalizeVoiceName(preferredVoiceName);
        var normalizedPreferredKey = TranslationNameKeyNormalizer.Normalize(normalizedPreferredVoice);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredKey))
        {
            var keyMatch = episodeEntries.FirstOrDefault(x =>
                string.Equals(
                    TranslationNameKeyNormalizer.Normalize(x.DisplayVoiceName),
                    normalizedPreferredKey,
                    StringComparison.Ordinal));

            if (keyMatch != null)
            {
                return keyMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedPreferredVoice))
        {
            var exactMatch = episodeEntries.FirstOrDefault(x =>
                string.Equals(x.DisplayVoiceName, normalizedPreferredVoice, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                return exactMatch;
            }

            var partialMatch = episodeEntries.FirstOrDefault(x =>
                x.DisplayVoiceName.Contains(normalizedPreferredVoice, StringComparison.OrdinalIgnoreCase) ||
                normalizedPreferredVoice.Contains(x.DisplayVoiceName, StringComparison.OrdinalIgnoreCase));

            if (partialMatch != null)
            {
                return partialMatch;
            }
        }

        return allowFallback
            ? episodeEntries.OrderBy(x => x.DisplayVoiceName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    private static HashSet<YummyVideoProviderKind> NormalizeProviderSet(IEnumerable<YummyVideoProviderKind>? providers)
    {
        return new HashSet<YummyVideoProviderKind>(NormalizeProviderOrder(providers));
    }

    private static YummyVideoProviderKind[] NormalizeProviderOrder(IEnumerable<YummyVideoProviderKind>? providers)
    {
        var normalized = (providers ?? DefaultProviderPreference)
            .Where(x => x is YummyVideoProviderKind.Alloha or YummyVideoProviderKind.Cvh)
            .Distinct()
            .ToArray();

        return normalized.Length > 0 ? normalized : DefaultProviderPreference;
    }

    private static bool MatchesProvider(YummyVideoEntry entry, YummyVideoProviderKind provider)
    {
        return provider switch
        {
            YummyVideoProviderKind.Cvh => entry.Provider == YummyVideoProviderKind.Cvh && entry.Cvh != null,
            YummyVideoProviderKind.Alloha => entry.Provider == YummyVideoProviderKind.Alloha && entry.Alloha != null,
            _ => false
        };
    }

    private static bool HasUsableSkips(YummyVideoSkips? skips)
    {
        if (skips == null)
        {
            return false;
        }

        var hasOpening = skips.Opening != null && skips.Opening.Length > 0;
        var hasEnding = skips.Ending != null && skips.Ending.Length > 0;
        return hasOpening || hasEnding;
    }

    private static YummyVideoProviderKind ParseProviderKind(int playerId)
    {
        return Enum.IsDefined(typeof(YummyVideoProviderKind), playerId)
            ? (YummyVideoProviderKind)playerId
            : YummyVideoProviderKind.Unknown;
    }

    private static bool TryParseEpisodeNumber(string? rawNumber, out int episodeNumber)
    {
        return int.TryParse(
            (rawNumber ?? string.Empty).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out episodeNumber);
    }

    private static string NormalizeUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + value;
        }

        return value;
    }

    private static void MergeAdditionalEntries(
        List<YummyVideoEntry> entries,
        IEnumerable<YummyVideoEntry>? additionalEntries)
    {
        if (additionalEntries == null)
        {
            return;
        }

        var entryIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].EpisodeNumber <= 0)
            {
                continue;
            }

            entryIndexes[BuildEntryKey(entries[index])] = index;
        }

        foreach (var entry in additionalEntries)
        {
            if (entry.EpisodeNumber <= 0)
            {
                continue;
            }

            var key = BuildEntryKey(entry);
            if (entryIndexes.TryGetValue(key, out var existingIndex))
            {
                entries[existingIndex] = MergeDuplicateEntry(entries[existingIndex], entry);
                continue;
            }

            entries.Add(entry);
            entryIndexes[key] = entries.Count - 1;
        }
    }

    private static YummyVideoEntry MergeDuplicateEntry(YummyVideoEntry existing, YummyVideoEntry preferred)
    {
        return new YummyVideoEntry
        {
            EpisodeNumber = preferred.EpisodeNumber > 0 ? preferred.EpisodeNumber : existing.EpisodeNumber,
            Provider = preferred.Provider != YummyVideoProviderKind.Unknown ? preferred.Provider : existing.Provider,
            RawDubbing = !string.IsNullOrWhiteSpace(preferred.RawDubbing) ? preferred.RawDubbing : existing.RawDubbing,
            DisplayVoiceName = !string.IsNullOrWhiteSpace(preferred.DisplayVoiceName) ? preferred.DisplayVoiceName : existing.DisplayVoiceName,
            DurationSeconds = Math.Max(existing.DurationSeconds, preferred.DurationSeconds),
            Skips = HasUsableSkips(existing.Skips) ? existing.Skips : preferred.Skips,
            IframeUrl = !string.IsNullOrWhiteSpace(existing.IframeUrl) ? existing.IframeUrl : preferred.IframeUrl,
            Cvh = existing.Cvh ?? preferred.Cvh,
            Alloha = existing.Alloha ?? preferred.Alloha
        };
    }

    private static string BuildEntryKey(YummyVideoEntry entry)
    {
        var provider = ((int)entry.Provider).ToString(CultureInfo.InvariantCulture);
        var episode = entry.EpisodeNumber.ToString(CultureInfo.InvariantCulture);
        var sourceKey = entry.Provider switch
        {
            YummyVideoProviderKind.Alloha when entry.Alloha != null && entry.Alloha.TranslationId > 0
                => "alloha:" +
                   (entry.Alloha.SeasonNumber > 0
                       ? entry.Alloha.SeasonNumber.ToString(CultureInfo.InvariantCulture)
                       : "0") +
                   ":" +
                   entry.Alloha.TranslationId.ToString(CultureInfo.InvariantCulture),
            YummyVideoProviderKind.Cvh when entry.Cvh != null && !string.IsNullOrWhiteSpace(entry.Cvh.DubbingCode)
                => "cvh:" + entry.Cvh.DubbingCode.Trim().ToLowerInvariant(),
            _ => NormalizeVoiceName(entry.DisplayVoiceName).ToLowerInvariant()
        };

        return provider + "|" + episode + "|" + sourceKey;
    }

    private static YummyCvhSource? TryParseCvhSource(string? iframeUrl)
    {
        if (!Uri.TryCreate(NormalizeUrl(iframeUrl), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var dict = ParseQuery(uri.Query);
        if (!dict.TryGetValue("anime_id", out var animeIdRaw) ||
            !long.TryParse(animeIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId) ||
            animeId <= 0)
        {
            return null;
        }

        if (!dict.TryGetValue("episode", out var episodeRaw) ||
            !int.TryParse(episodeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode) ||
            episode <= 0)
        {
            return null;
        }

        dict.TryGetValue("dubbing_code", out var dubbingCode);
        dict.TryGetValue("dubbing", out var dubbingName);

        return new YummyCvhSource
        {
            AnimeId = animeId,
            EpisodeNumber = episode,
            DubbingCode = (dubbingCode ?? string.Empty).Trim(),
            DubbingName = NormalizeVoiceName(dubbingName)
        };
    }

    private static YummyAllohaSource? TryParseAllohaSource(string? iframeUrl)
    {
        if (!Uri.TryCreate(NormalizeUrl(iframeUrl), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var dict = ParseQuery(uri.Query);
        if (!dict.TryGetValue("token_movie", out var movieToken) || string.IsNullOrWhiteSpace(movieToken))
        {
            return null;
        }

        if (!dict.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!dict.TryGetValue("translation", out var translationRaw) ||
            !int.TryParse(translationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var translationId) ||
            translationId <= 0)
        {
            return null;
        }

        if (!dict.TryGetValue("season", out var seasonRaw) ||
            !int.TryParse(seasonRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber) ||
            seasonNumber <= 0)
        {
            return null;
        }

        if (!dict.TryGetValue("episode", out var episodeRaw) ||
            !int.TryParse(episodeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeNumber) ||
            episodeNumber <= 0)
        {
            return null;
        }

        dict.TryGetValue("hidden", out var hidden);

        return new YummyAllohaSource
        {
            MovieToken = movieToken.Trim(),
            RequestToken = token.Trim(),
            TranslationId = translationId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Hidden = (hidden ?? string.Empty).Trim(),
            RefererUrl = NormalizeUrl(iframeUrl)
        };
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var value = (query ?? string.Empty).TrimStart('?');
        if (value.Length == 0)
        {
            return result;
        }

        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = DecodeQueryComponent(part.Substring(0, eq));
            var val = DecodeQueryComponent(part[(eq + 1)..]);
            result[key] = val;
        }

        return result;
    }

    private static string DecodeQueryComponent(string value)
    {
        return Uri.UnescapeDataString((value ?? string.Empty).Replace("+", " ", StringComparison.Ordinal));
    }
}

public sealed class YummyVideoEntry
{
    public int EpisodeNumber { get; init; }
    public YummyVideoProviderKind Provider { get; init; }
    public string RawDubbing { get; init; } = string.Empty;
    public string DisplayVoiceName { get; init; } = string.Empty;
    public int DurationSeconds { get; init; }
    public YummyVideoSkips? Skips { get; init; }
    public string IframeUrl { get; init; } = string.Empty;
    public YummyCvhSource? Cvh { get; init; }
    public YummyAllohaSource? Alloha { get; init; }
}

public sealed class YummyCvhSource
{
    public long AnimeId { get; init; }
    public int EpisodeNumber { get; init; }
    public string DubbingCode { get; init; } = string.Empty;
    public string DubbingName { get; init; } = string.Empty;
    public string Aggregator { get; init; } = "mali";
    public int PublisherId { get; init; } = 745;
}

public sealed class YummyAllohaSource
{
    public string MovieToken { get; init; } = string.Empty;
    public string RequestToken { get; init; } = string.Empty;
    public int TranslationId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public string Hidden { get; init; } = string.Empty;
    public string RefererUrl { get; init; } = string.Empty;
}
