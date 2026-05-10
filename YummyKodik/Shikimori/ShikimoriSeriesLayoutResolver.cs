using System.Text.RegularExpressions;

namespace YummyKodik.Shikimori;

public static class ShikimoriSeriesLayoutResolver
{
    private static readonly Regex PartSuffixRegex = new(
        @"(?:^|[\s\.:|,-])(?:part|часть)\s*\d+\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ShikimoriSeriesLayoutInfo? BuildFromMainlineChain(IReadOnlyList<ShikimoriSeriesLayoutNode>? rootToCurrent)
    {
        if (rootToCurrent == null || rootToCurrent.Count == 0)
        {
            return null;
        }

        var ordered = rootToCurrent
            .Where(node => node != null)
            .ToList();

        if (ordered.Count == 0)
        {
            return null;
        }

        var baseTitle = PickPrimaryTitle(ordered[0]);
        if (string.IsNullOrWhiteSpace(baseTitle))
        {
            baseTitle = PickPrimaryTitle(ordered[^1]);
        }

        var seasonNumber = 1;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (IsMainlineKind(ordered[i].Kind) &&
                !IsContinuationPart(ordered[i]))
            {
                seasonNumber++;
            }
        }

        return new ShikimoriSeriesLayoutInfo
        {
            SeasonNumber = seasonNumber,
            BaseTitle = baseTitle
        };
    }

    public static bool IsMainlineKind(string? kind)
    {
        var normalizedKind = (kind ?? string.Empty).Trim();
        return normalizedKind.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
               normalizedKind.Equals("ona", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsContinuationPart(ShikimoriSeriesLayoutNode? node)
    {
        if (node == null)
        {
            return false;
        }

        return PartSuffixRegex.IsMatch(node.RussianTitle ?? string.Empty) ||
               PartSuffixRegex.IsMatch(node.Name ?? string.Empty);
    }

    public static string PickPrimaryTitle(ShikimoriSeriesLayoutNode? node)
    {
        if (!string.IsNullOrWhiteSpace(node?.RussianTitle))
        {
            return node.RussianTitle.Trim();
        }

        return (node?.Name ?? string.Empty).Trim();
    }
}

public sealed class ShikimoriSeriesLayoutNode
{
    public long Id { get; init; }

    public string RussianTitle { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;
}
