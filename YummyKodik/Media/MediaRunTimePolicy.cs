using MediaBrowser.Model.Dto;
using YummyKodik.Configuration;

namespace YummyKodik.Media;

internal static class MediaRunTimePolicy
{
    private static readonly long AuthoritativeCorrectionToleranceTicks = TimeSpan.FromSeconds(2).Ticks;

    public static bool ShouldPublish(long? currentRunTimeTicks, long? resolvedRunTimeTicks)
    {
        if (!resolvedRunTimeTicks.HasValue || resolvedRunTimeTicks.Value <= 0)
        {
            return false;
        }

        if (!currentRunTimeTicks.HasValue || currentRunTimeTicks.Value <= 0)
        {
            return true;
        }

        return currentRunTimeTicks.Value < TimeSpan.FromMinutes(1).Ticks &&
               resolvedRunTimeTicks.Value > currentRunTimeTicks.Value + TimeSpan.FromSeconds(1).Ticks;
    }

    public static bool ShouldPublishAuthoritative(long? currentRunTimeTicks, long? resolvedRunTimeTicks)
    {
        if (!IsPlausible(resolvedRunTimeTicks))
        {
            return false;
        }

        if (!IsPlausible(currentRunTimeTicks))
        {
            return true;
        }

        return Math.Abs(currentRunTimeTicks!.Value - resolvedRunTimeTicks!.Value) >
               AuthoritativeCorrectionToleranceTicks;
    }

    public static bool HasExplicitSeriesSelection(
        PluginConfiguration configuration,
        IEnumerable<string> seriesKeys)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(seriesKeys);

        return seriesKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(key => !string.IsNullOrWhiteSpace(
                configuration.GetUserSeriesPreferredTranslationId(Guid.Empty, key)));
    }

    public static long? FillMissingSourceRunTimes(
        long? itemRunTimeTicks,
        IReadOnlyList<MediaSourceInfo> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var fallbackRunTimeTicks = sources
            .Select(source => source.RunTimeTicks)
            .FirstOrDefault(IsPlausible);
        if (!fallbackRunTimeTicks.HasValue && IsPlausible(itemRunTimeTicks))
        {
            fallbackRunTimeTicks = itemRunTimeTicks;
        }

        if (!fallbackRunTimeTicks.HasValue)
        {
            return null;
        }

        foreach (var source in sources)
        {
            if (ShouldPublish(source.RunTimeTicks, fallbackRunTimeTicks))
            {
                source.RunTimeTicks = fallbackRunTimeTicks;
            }
        }

        return fallbackRunTimeTicks;
    }

    public static bool IsSiblingEpisodeVersion(
        string? sourcePresentationUniqueKey,
        string? sourceDirectory,
        string? candidatePresentationUniqueKey,
        string? candidatePath)
    {
        return !string.IsNullOrWhiteSpace(sourcePresentationUniqueKey) &&
               !string.IsNullOrWhiteSpace(sourceDirectory) &&
               !string.IsNullOrWhiteSpace(candidatePath) &&
               candidatePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   sourcePresentationUniqueKey,
                   candidatePresentationUniqueKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   sourceDirectory,
                   Path.GetDirectoryName(candidatePath),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlausible(long? runTimeTicks)
    {
        return runTimeTicks.HasValue && runTimeTicks.Value >= TimeSpan.FromMinutes(1).Ticks;
    }
}
