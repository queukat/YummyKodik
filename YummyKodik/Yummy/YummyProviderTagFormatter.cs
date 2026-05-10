namespace YummyKodik.Yummy
{
    /// <summary>
    /// Builds stable provider-id tags for generated library folders.
    /// </summary>
    public static class YummyProviderTagFormatter
    {
        public static string BuildBestIdTag(YummyAnimeResponse? anime)
        {
            var r = anime?.RemoteIds;

            if (r?.ShikimoriId is long shiki && shiki > 0)
            {
                return $"[shikimori-{shiki}]";
            }

            if (r?.KpId is long kp && kp > 0)
            {
                return $"[kp-{kp}]";
            }

            if (!string.IsNullOrWhiteSpace(r?.ImdbId))
            {
                return $"[imdbid-{r.ImdbId.Trim()}]";
            }

            if (anime != null && anime.AnimeId > 0)
            {
                return $"[yaniid-{anime.AnimeId}]";
            }

            return string.Empty;
        }

        public static string BuildLegacyBestIdTag(YummyAnimeResponse? anime)
        {
            var r = anime?.RemoteIds;
            if (r?.ShikimoriId is long shiki && shiki > 0)
            {
                return $"[shikimoriid-{shiki}]";
            }

            return BuildBestIdTag(anime);
        }
    }
}
