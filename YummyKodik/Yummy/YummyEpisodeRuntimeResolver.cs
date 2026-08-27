namespace YummyKodik.Yummy;

internal static class YummyEpisodeRuntimeResolver
{
    public static int? ResolveDurationSeconds(
        YummyVideoCatalog catalog,
        YummyVideoProviderKind preferredProvider,
        int episode,
        string? preferredVoiceName)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var preferredDuration = catalog.GetDurationSeconds(preferredProvider, episode, preferredVoiceName);
        if (preferredDuration.HasValue && preferredDuration.Value > 0)
        {
            return preferredDuration;
        }

        var fallbackProvider = preferredProvider == YummyVideoProviderKind.Alloha
            ? YummyVideoProviderKind.Cvh
            : YummyVideoProviderKind.Alloha;
        return catalog.GetDurationSeconds(fallbackProvider, episode, preferredVoiceName)
               ?? catalog.GetDurationSeconds(fallbackProvider, episode);
    }
}
