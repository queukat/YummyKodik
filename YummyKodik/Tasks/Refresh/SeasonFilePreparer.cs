using Microsoft.Extensions.Logging;

namespace YummyKodik.Tasks.Refresh;

internal sealed class SeasonFilePreparer
{
    public string PrepareForEpisodeGeneration(
        ILogger logger,
        YummyRefreshInfo refresh,
        string seasonDir,
        bool createStrmPerVoiceTranslation,
        EpisodeGenerationState state,
        RefreshPerformanceMetrics perf)
    {
        using (perf.Measure("stage.prepare.season.dir"))
        {
            seasonDir = SeasonDirectoryMaintenance.PrepareSeasonDirectory(
                logger,
                refresh.Files.SeriesRoot,
                seasonDir,
                refresh.TitleInfo.SeasonNumber);
        }

        if (createStrmPerVoiceTranslation)
        {
            using (perf.Measure("stage.scan.translation.files"))
            {
                state.ExistingEpisodeTranslationFileBaseNames = EpisodeArtifactWriter.BuildExistingEpisodeTranslationFileBaseNames(
                    seasonDir,
                    refresh.TitleInfo.SeasonNumber);
            }
        }

        return seasonDir;
    }
}
