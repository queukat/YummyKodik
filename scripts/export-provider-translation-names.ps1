param(
    [string]$ConfigPath = 'C:\ProgramData\Jellyfin\Server\plugins\configurations\YummyKodik.xml',
    [string]$AllohaTokenPath = 'C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.1.0.0\AllohaApiToken.txt',
    [string]$OutputDirectory = '',
    [int]$ThrottleMs = 0
)

$ErrorActionPreference = 'Stop'

function Add-UniqueName {
    param(
        [System.Collections.Generic.HashSet[string]]$Set,
        [string]$Name
    )

    $value = ($Name ?? '').Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return
    }

    [void]$Set.Add($value)
}

function Parse-QueryString {
    param([string]$Url)

    $result = @{}
    if ([string]::IsNullOrWhiteSpace($Url)) {
        return $result
    }

    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        return $result
    }

    $query = ($uri.Query ?? '').TrimStart('?')
    if ([string]::IsNullOrWhiteSpace($query)) {
        return $result
    }

    foreach ($pair in $query.Split('&', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $pair.Split('=', 2)
        if ($parts.Count -ne 2) {
            continue
        }

        $key = [Uri]::UnescapeDataString(($parts[0] ?? '').Replace('+', ' '))
        $value = [Uri]::UnescapeDataString(($parts[1] ?? '').Replace('+', ' '))
        $result[$key] = $value
    }

    return $result
}

function Invoke-YummyRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$ClientId,
        [string]$AccessToken
    )

    $headers = @{
        'Accept' = 'application/json, text/plain, */*'
    }

    if (-not [string]::IsNullOrWhiteSpace($ClientId)) {
        $headers['X-Application'] = $ClientId
    }

    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers['Authorization'] = "Bearer $AccessToken"
    }

    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
}

function Resolve-KodikToken {
    param([string]$ConfiguredToken)

    $trimmed = ($ConfiguredToken ?? '').Trim()
    if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
        return $trimmed
    }

    $scriptText = curl.exe -s --compressed 'https://kodik-add.com/add-players.min.js?v=2'
    if ([string]::IsNullOrWhiteSpace($scriptText)) {
        return ''
    }

    $match = [regex]::Match($scriptText, 'token=(?<token>[^&"''\s]+)')
    if (-not $match.Success) {
        return ''
    }

    return ($match.Groups['token'].Value ?? '').Trim()
}

function Get-KodikNamesForShikimori {
    param(
        [string]$ShikimoriId,
        [string]$Token
    )

    if ([string]::IsNullOrWhiteSpace($Token) -or [string]::IsNullOrWhiteSpace($ShikimoriId)) {
        return @()
    }

    $uri = 'https://kodik-api.com/search'
    $body = @{
        token = $Token
        limit = '100'
        all = 'true'
        with_episodes = 'true'
        with_episodes_data = 'false'
        shikimori_id = $ShikimoriId
    }

    $response = Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/x-www-form-urlencoded'
    if ($null -eq $response -or $null -eq $response.results) {
        return @()
    }

    return @($response.results | ForEach-Object {
        if ($_.translation) {
            $_.translation.title
        }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-AllohaNamesById {
    param(
        [string]$ApiToken,
        [string]$QueryName,
        [string]$QueryValue
    )

    if ([string]::IsNullOrWhiteSpace($ApiToken) -or [string]::IsNullOrWhiteSpace($QueryValue)) {
        return @()
    }

    $uri = "https://api.alloha.tv/?token=$([Uri]::EscapeDataString($ApiToken))&$QueryName=$([Uri]::EscapeDataString($QueryValue))"
    $response = Invoke-RestMethod -Method Get -Uri $uri -Headers @{ 'Accept' = 'application/json,text/plain,*/*' }
    if ($null -eq $response -or $response.status -ne 'success' -or $null -eq $response.data -or $null -eq $response.data.seasons) {
        return @()
    }

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($seasonProperty in $response.data.seasons.PSObject.Properties) {
        $season = $seasonProperty.Value
        if ($null -eq $season -or $null -eq $season.episodes) {
            continue
        }

        foreach ($episodeProperty in $season.episodes.PSObject.Properties) {
            $episode = $episodeProperty.Value
            if ($null -eq $episode -or $null -eq $episode.translation) {
                continue
            }

            foreach ($translationProperty in $episode.translation.PSObject.Properties) {
                $translation = $translationProperty.Value
                if ($null -eq $translation) {
                    continue
                }

                $name = $translation.translation
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    [void]$names.Add($name)
                }
            }
        }
    }

    return @($names)
}

function Get-CvhNamesForAnime {
    param([string]$AnimeId)

    if ([string]::IsNullOrWhiteSpace($AnimeId)) {
        return @()
    }

    $headers = @{
        'Accept' = 'application/json, text/plain, */*'
        'Accept-Language' = 'ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7'
        'Origin' = 'https://ru.yummyani.me'
        'Referer' = "https://ru.yummyani.me/iframeCVH.html?anime_id=$AnimeId&episode=1"
        'Sec-Fetch-Site' = 'cross-site'
        'Sec-Fetch-Mode' = 'cors'
        'Sec-Fetch-Dest' = 'empty'
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36'
    }

    $uri = "https://plapi.cdnvideohub.com/api/v1/player/sv/playlist?pub=745&id=$([Uri]::EscapeDataString($AnimeId))&aggr=mali"
    $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
    if ($null -eq $response -or $null -eq $response.items) {
        return @()
    }

    return @($response.items | ForEach-Object { $_.voiceStudio } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Write-MarkdownReport {
    param(
        [string]$Path,
        [pscustomobject]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('# Provider Translation Names')
    [void]$lines.Add('')
    [void]$lines.Add("Generated: $($Result.generatedAtUtc)")
    [void]$lines.Add("Scope: $($Result.scope)")
    [void]$lines.Add("UserListItems: $($Result.userListItemCount)")
    [void]$lines.Add("AnimeDetailsFetched: $($Result.animeDetailsFetched)")
    [void]$lines.Add('')

    foreach ($providerName in @('kodik', 'alloha', 'cvh')) {
        $provider = $Result.providers.$providerName
        [void]$lines.Add("## $providerName ($($provider.count))")
        [void]$lines.Add('')
        foreach ($name in $provider.names) {
            [void]$lines.Add("- $name")
        }
        [void]$lines.Add('')
    }

    [System.IO.File]::WriteAllLines($Path, $lines)
}

[xml]$configXml = Get-Content -Raw -LiteralPath $ConfigPath
$pluginConfig = $configXml.PluginConfiguration

$clientId = ($pluginConfig.YummyClientId ?? '').Trim()
$yummyBaseUrl = (($pluginConfig.YummyApiBaseUrl ?? 'https://api.yani.tv').Trim()).TrimEnd('/')
$accessToken = ($pluginConfig.YummyAccessToken ?? '').Trim()
$userId = [int]($pluginConfig.YummyUserId ?? 0)
$listId = [int]($pluginConfig.YummyUserListId ?? 0)
$configuredKodikToken = ($pluginConfig.KodikToken ?? '').Trim()
$allohaApiToken = ''

if (Test-Path -LiteralPath $AllohaTokenPath) {
    $allohaApiToken = (Get-Content -Raw -LiteralPath $AllohaTokenPath).Trim()
}

$kodikToken = Resolve-KodikToken -ConfiguredToken $configuredKodikToken

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\\artifacts'
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$generatedStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$jsonPath = Join-Path $resolvedOutputDirectory "provider-translation-names-$generatedStamp.json"
$markdownPath = Join-Path $resolvedOutputDirectory "provider-translation-names-$generatedStamp.md"

$userListResponse = Invoke-YummyRequest -Method Get -Uri "$yummyBaseUrl/users/$userId/lists/$listId" -ClientId $clientId -AccessToken $accessToken
$userListItems = @($userListResponse.response)

$shikimoriIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$kpIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$imdbIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$cvhAnimeIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

$animeFetchFailures = New-Object System.Collections.Generic.List[string]
$animeDetailsFetched = 0

foreach ($item in $userListItems) {
    if ($item.remote_ids) {
        if ($item.remote_ids.shikimori_id) {
            [void]$shikimoriIds.Add([string]$item.remote_ids.shikimori_id)
        }

        if ($item.remote_ids.kp_id) {
            [void]$kpIds.Add([string]$item.remote_ids.kp_id)
        }

        if ($item.remote_ids.imdb_id) {
            [void]$imdbIds.Add(([string]$item.remote_ids.imdb_id).Trim())
        }
    }

    $animeKey = ''
    if (-not [string]::IsNullOrWhiteSpace($item.anime_url)) {
        $animeKey = ([string]$item.anime_url).Trim()
    } elseif ($item.anime_id) {
        $animeKey = [string]$item.anime_id
    }

    if ([string]::IsNullOrWhiteSpace($animeKey)) {
        continue
    }

    try {
        $animeResponse = Invoke-YummyRequest -Method Get -Uri "$yummyBaseUrl/anime/$([Uri]::EscapeDataString($animeKey))?need_videos=true" -ClientId $clientId -AccessToken $accessToken
        $anime = $animeResponse.response
        if ($null -eq $anime -or $null -eq $anime.videos) {
            continue
        }

        $animeDetailsFetched++

        foreach ($video in @($anime.videos)) {
            if ($null -eq $video -or $null -eq $video.data) {
                continue
            }

            if ([int]$video.data.player_id -ne 3) {
                continue
            }

            $query = Parse-QueryString -Url ([string]$video.iframe_url)
            $cvhAnimeId = $query['anime_id']
            if (-not [string]::IsNullOrWhiteSpace($cvhAnimeId)) {
                [void]$cvhAnimeIds.Add($cvhAnimeId)
            }
        }
    }
    catch {
        [void]$animeFetchFailures.Add(($item.title ?? $animeKey).ToString())
    }

    if ($ThrottleMs -gt 0) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

$kodikNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$allohaNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$cvhNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

$kodikFailures = New-Object System.Collections.Generic.List[string]
$allohaFailures = New-Object System.Collections.Generic.List[string]
$cvhFailures = New-Object System.Collections.Generic.List[string]

foreach ($shikimoriId in $shikimoriIds) {
    try {
        foreach ($name in Get-KodikNamesForShikimori -ShikimoriId $shikimoriId -Token $kodikToken) {
            Add-UniqueName -Set $kodikNames -Name $name
        }
    }
    catch {
        [void]$kodikFailures.Add($shikimoriId)
    }

    if ($ThrottleMs -gt 0) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

foreach ($kpId in $kpIds) {
    try {
        foreach ($name in Get-AllohaNamesById -ApiToken $allohaApiToken -QueryName 'kp' -QueryValue $kpId) {
            Add-UniqueName -Set $allohaNames -Name $name
        }
    }
    catch {
        [void]$allohaFailures.Add("kp:$kpId")
    }

    if ($ThrottleMs -gt 0) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

foreach ($imdbId in $imdbIds) {
    try {
        foreach ($name in Get-AllohaNamesById -ApiToken $allohaApiToken -QueryName 'imdb' -QueryValue $imdbId) {
            Add-UniqueName -Set $allohaNames -Name $name
        }
    }
    catch {
        [void]$allohaFailures.Add("imdb:$imdbId")
    }

    if ($ThrottleMs -gt 0) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

foreach ($cvhAnimeId in $cvhAnimeIds) {
    try {
        foreach ($name in Get-CvhNamesForAnime -AnimeId $cvhAnimeId) {
            Add-UniqueName -Set $cvhNames -Name $name
        }
    }
    catch {
        [void]$cvhFailures.Add($cvhAnimeId)
    }

    if ($ThrottleMs -gt 0) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

$result = [pscustomobject]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'all titles from current Yummy user list'
    userId = $userId
    listId = $listId
    userListItemCount = $userListItems.Count
    animeDetailsFetched = $animeDetailsFetched
    uniqueRemoteIds = [pscustomobject]@{
        shikimori = $shikimoriIds.Count
        kp = $kpIds.Count
        imdb = $imdbIds.Count
        cvhAnime = $cvhAnimeIds.Count
    }
    failures = [pscustomobject]@{
        animeDetails = @($animeFetchFailures | Sort-Object -Unique)
        kodik = @($kodikFailures | Sort-Object -Unique)
        alloha = @($allohaFailures | Sort-Object -Unique)
        cvh = @($cvhFailures | Sort-Object -Unique)
    }
    providers = [pscustomobject]@{
        kodik = [pscustomobject]@{
            count = $kodikNames.Count
            names = @($kodikNames | Sort-Object)
        }
        alloha = [pscustomobject]@{
            count = $allohaNames.Count
            names = @($allohaNames | Sort-Object)
        }
        cvh = [pscustomobject]@{
            count = $cvhNames.Count
            names = @($cvhNames | Sort-Object)
        }
    }
    outputFiles = [pscustomobject]@{
        json = $jsonPath
        markdown = $markdownPath
    }
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
Write-MarkdownReport -Path $markdownPath -Result $result

$result | ConvertTo-Json -Depth 8
