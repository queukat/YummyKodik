using Microsoft.Extensions.Logging;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class YummyEpisodeArtifactGenerator
{
    private readonly EpisodeArtifactWriter _artifactWriter;

    public YummyEpisodeArtifactGenerator(EpisodeArtifactWriter artifactWriter)
    {
        _artifactWriter = artifactWriter;
    }

    public async Task GeneratePreferredProviderEpisodeFilesAsync(
        YummyEpisodeGenerationContext context,
        int[] supportedEpisodes,
        CancellationToken cancellationToken)
    {
        foreach (var ep in supportedEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GeneratePreferredProviderEpisodeFileAsync(context, ep, cancellationToken).ConfigureAwait(false);
        }

        LogIncompleteYummyCoverage(context, supportedEpisodes.Length);
    }

    private async Task GeneratePreferredProviderEpisodeFileAsync(
        YummyEpisodeGenerationContext context,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        var baseName = RefreshPathUtilities.BuildEpisodeBaseName(context.Refresh.TitleInfo.SeasonNumber, episodeNumber);
        var writeContext = CreateEpisodeArtifactWriteContext(context);

        if (!context.CreateStrmPerVoiceTranslation)
        {
            await TryWritePreferredProviderEpisodeFileAsync(context, writeContext, episodeNumber, baseName, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var voiceNames = context.Refresh.VideoCatalog.GetSupportedVoiceNamesAcrossProviders(
            episodeNumber,
            RefreshConstants.PreferredYummyProviderOrder);
        if (voiceNames.Count == 0)
        {
            await TryWriteAutomaticYummyEpisodeFileAsync(context, writeContext, episodeNumber, baseName, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteYummyVoiceEpisodeFilesAsync(context, writeContext, episodeNumber, baseName, voiceNames, cancellationToken)
            .ConfigureAwait(false);
    }

    private static EpisodeArtifactWriteContext CreateEpisodeArtifactWriteContext(YummyEpisodeGenerationContext context)
    {
        return new EpisodeArtifactWriteContext(
            context.Logger,
            context.SeasonDir,
            context.Refresh.TitleInfo.SeasonNumber,
            context.Refresh.TitleInfo.Title,
            context.Refresh.TitleInfo.Anime.Description,
            context.Perf);
    }

    private async Task<bool> TryWritePreferredProviderEpisodeFileAsync(
        YummyEpisodeGenerationContext context,
        EpisodeArtifactWriteContext writeContext,
        int episodeNumber,
        string baseName,
        CancellationToken cancellationToken)
    {
        var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
            episodeNumber,
            preferredFilter: context.PreferredTranslationFilter,
            providers: RefreshConstants.PreferredYummyProviderOrder);
        if (!provider.HasValue)
        {
            return false;
        }

        var url = RefreshPathUtilities.BuildProviderStreamUrl(
            context.Refresh.Files.BaseUrl,
            provider.Value,
            context.Refresh.TitleInfo.Anime.AnimeId,
            episodeNumber) + RefreshConstants.HlsFormatQuerySuffix;
        await _artifactWriter.WriteEpisodeArtifactsAsync(writeContext, baseName, url, episodeNumber, cancellationToken)
            .ConfigureAwait(false);
        EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, baseName);
        return true;
    }

    private async Task<bool> TryWriteAutomaticYummyEpisodeFileAsync(
        YummyEpisodeGenerationContext context,
        EpisodeArtifactWriteContext writeContext,
        int episodeNumber,
        string baseName,
        CancellationToken cancellationToken)
    {
        var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
            episodeNumber,
            preferredFilter: context.PreferredTranslationFilter,
            providers: RefreshConstants.PreferredYummyProviderOrder);
        if (!provider.HasValue)
        {
            return false;
        }

        var url = RefreshPathUtilities.BuildProviderStreamUrl(
            context.Refresh.Files.BaseUrl,
            provider.Value,
            context.Refresh.TitleInfo.Anime.AnimeId,
            episodeNumber) + RefreshConstants.HlsFormatQuerySuffix;
        var fileBaseName = baseName + " - Auto";
        await _artifactWriter.WriteEpisodeArtifactsAsync(writeContext, fileBaseName, url, episodeNumber, cancellationToken)
            .ConfigureAwait(false);
        EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, fileBaseName);
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(context.State.ExpectedEpisodeTranslationKeys, episodeNumber, "Auto");
        return true;
    }

    private async Task WriteYummyVoiceEpisodeFilesAsync(
        YummyEpisodeGenerationContext context,
        EpisodeArtifactWriteContext writeContext,
        int episodeNumber,
        string baseName,
        IEnumerable<string> voiceNames,
        CancellationToken cancellationToken)
    {
        foreach (var voiceName in voiceNames)
        {
            await TryWriteYummyVoiceEpisodeFileAsync(
                    context,
                    writeContext,
                    episodeNumber,
                    baseName,
                    voiceName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> TryWriteYummyVoiceEpisodeFileAsync(
        YummyEpisodeGenerationContext context,
        EpisodeArtifactWriteContext writeContext,
        int episodeNumber,
        string baseName,
        string voiceName,
        CancellationToken cancellationToken)
    {
        var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
            episodeNumber,
            explicitVoiceName: voiceName,
            providers: RefreshConstants.PreferredYummyProviderOrder);
        if (!provider.HasValue)
        {
            return false;
        }

        var chosenEntry = context.Refresh.VideoCatalog.FindPreferredPlayableEntry(provider.Value, episodeNumber, voiceName);
        if (chosenEntry == null)
        {
            return false;
        }

        var suffix = BuildSafeVoiceSuffix(voiceName);
        var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(
            context.State.ExistingEpisodeTranslationFileBaseNames,
            episodeNumber,
            baseName,
            suffix);
        var url = RefreshPathUtilities.BuildProviderStreamUrl(
            context.Refresh.Files.BaseUrl,
            provider.Value,
            context.Refresh.TitleInfo.Anime.AnimeId,
            episodeNumber,
            voiceName,
            chosenEntry) + RefreshConstants.HlsFormatQuerySuffix;

        await _artifactWriter.WriteEpisodeArtifactsAsync(writeContext, fileBaseName, url, episodeNumber, cancellationToken)
            .ConfigureAwait(false);
        EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, fileBaseName);
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(context.State.ExpectedEpisodeTranslationKeys, episodeNumber, suffix);
        return true;
    }

    private static string BuildSafeVoiceSuffix(string voiceName)
    {
        var suffix = RefreshPathUtilities.SafeFilename(voiceName);
        return string.IsNullOrWhiteSpace(suffix) ? "Voice" : suffix;
    }

    private static void LogIncompleteYummyCoverage(YummyEpisodeGenerationContext context, int coveredEpisodeCount)
    {
        var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(context.Refresh.TitleInfo.Anime);
        if (expectedAvailableEpisodes > coveredEpisodeCount)
        {
            context.Logger.LogInformation(
                "[YummyKodik] Yummy-backed providers currently cover {CoveredEpisodes}/{TotalEpisodes} episodes for '{Title}'.",
                coveredEpisodeCount,
                expectedAvailableEpisodes,
                context.Refresh.TitleInfo.Title);
        }
    }
}
