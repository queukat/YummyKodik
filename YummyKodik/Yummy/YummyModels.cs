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

        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("type")]
        public YummyAnimeType? Type { get; set; }

        [JsonPropertyName("anime_status")]
        public YummyAnimeStatus? AnimeStatus { get; set; }

        [JsonPropertyName("episodes")]
        public YummyEpisodesInfo? Episodes { get; set; }

        [JsonPropertyName("videos")]
        public List<YummyVideoItem>? Videos { get; set; }

        [JsonPropertyName("viewing_order")]
        public List<YummyViewingOrderItem>? ViewingOrder { get; set; }

        [JsonPropertyName("remote_ids")]
        public YummyRemoteIds? RemoteIds { get; set; }
    }

    public sealed class YummyAnimeStatus
    {
        [JsonPropertyName("value")]
        public int Value { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("alias")]
        public string Alias { get; set; } = string.Empty;
    }

    public sealed class YummyAnimeType
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public int Value { get; set; }

        [JsonPropertyName("shortname")]
        public string ShortName { get; set; } = string.Empty;

        [JsonPropertyName("alias")]
        public string Alias { get; set; } = string.Empty;
    }

    public sealed class YummyEpisodesInfo
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("aired")]
        public int Aired { get; set; }
    }

    public sealed class YummyVideoItem
    {
        [JsonPropertyName("video_id")]
        public long VideoId { get; set; }

        [JsonPropertyName("data")]
        public YummyVideoData? Data { get; set; }

        [JsonPropertyName("number")]
        public string Number { get; set; } = string.Empty;

        [JsonPropertyName("iframe_url")]
        public string IframeUrl { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        [JsonPropertyName("skips")]
        public YummyVideoSkips? Skips { get; set; }
    }

    public sealed class YummyVideoData
    {
        [JsonPropertyName("player")]
        public string Player { get; set; } = string.Empty;

        [JsonPropertyName("dubbing")]
        public string Dubbing { get; set; } = string.Empty;

        [JsonPropertyName("player_id")]
        public int PlayerId { get; set; }
    }

    public sealed class YummyVideoSkips
    {
        [JsonPropertyName("opening")]
        public YummySkipSegment? Opening { get; set; }

        [JsonPropertyName("ending")]
        public YummySkipSegment? Ending { get; set; }
    }

    public sealed class YummySkipSegment
    {
        [JsonPropertyName("time")]
        public int Time { get; set; }

        [JsonPropertyName("length")]
        public int Length { get; set; }
    }

    public sealed class YummyViewingOrderItem
    {
        [JsonPropertyName("anime_id")]
        public long AnimeId { get; set; }

        [JsonPropertyName("anime_url")]
        public string AnimeUrl { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public YummyViewingOrderData? Data { get; set; }
    }

    public sealed class YummyViewingOrderData
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
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
