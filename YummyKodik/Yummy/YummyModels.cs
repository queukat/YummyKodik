// File: Yummy/YummyModels.cs

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YummyKodik.Yummy
{
    public sealed class YummyAnimeGetWrapper
    {
        [JsonPropertyName("response")]
        public YummyAnimeResponse? Response { get; set; }
    }

    public sealed class YummyAnimeResponse
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("poster")]
        public YummyPoster? Poster { get; set; }

        [JsonPropertyName("anime_url")]
        public string AnimeUrl { get; set; } = string.Empty;

        [JsonPropertyName("anime_id")]
        public long AnimeId { get; set; }

        [JsonPropertyName("remote_ids")]
        public YummyRemoteIds? RemoteIds { get; set; }
    }

    public sealed class YummyRemoteIds
    {
        [JsonPropertyName("shikimori_id")]
        public long? ShikimoriId { get; set; }

        [JsonPropertyName("kp_id")]
        public long? KpId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("worldart_id")]
        public long? WorldartId { get; set; }

        [JsonPropertyName("myanimelist_id")]
        public long? MyAnimeListId { get; set; }

        [JsonPropertyName("anidub_id")]
        public long? AnidubId { get; set; }

        [JsonPropertyName("anilibria_alias")]
        public string? AnilibriaAlias { get; set; }

        [JsonPropertyName("worldart_type")]
        public string? WorldartType { get; set; }

        [JsonPropertyName("sr_id")]
        public long? SrId { get; set; }
    }

    public sealed class YummyPoster
    {
        [JsonPropertyName("fullsize")]
        public string? Fullsize { get; set; }

        [JsonPropertyName("big")]
        public string? Big { get; set; }

        [JsonPropertyName("huge")]
        public string? Huge { get; set; }

        [JsonPropertyName("mega")]
        public string? Mega { get; set; }

        [JsonPropertyName("medium")]
        public string? Medium { get; set; }

        [JsonPropertyName("small")]
        public string? Small { get; set; }
    }

    public sealed class YummyUserListWrapper
    {
        [JsonPropertyName("response")]
        public List<YummyUserListItem>? Response { get; set; }
    }

    public sealed class YummyUserListItem
    {
        [JsonPropertyName("anime_id")]
        public long AnimeId { get; set; }

        [JsonPropertyName("anime_url")]
        public string AnimeUrl { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("remote_ids")]
        public YummyRemoteIds? RemoteIds { get; set; }

        [JsonPropertyName("poster")]
        public YummyPoster? Poster { get; set; }
    }

    public sealed class YummyLoginRequest
    {
        [JsonPropertyName("need_json")]
        public bool NeedJson { get; set; } = true;

        [JsonPropertyName("recaptcha_response")]
        public string RecaptchaResponse { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;
    }

    public sealed class YummyLoginWrapper
    {
        [JsonPropertyName("response")]
        public YummyLoginResponse? Response { get; set; }
    }

    public sealed class YummyLoginResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;
    }

    public sealed class YummyTokenWrapper
    {
        [JsonPropertyName("response")]
        public YummyTokenResponse? Response { get; set; }
    }

    public sealed class YummyTokenResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;
    }
}
