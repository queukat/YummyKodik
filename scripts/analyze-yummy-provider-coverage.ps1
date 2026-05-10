param(
    [int]$Limit = 0,
    [int]$ThrottleLimit = 12,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Normalize-DubbingName {
    param([string]$Name)

    $value = ($Name ?? '').Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ''
    }

    $normalized = $value -replace '^(Озвучка|Субтитры)\s+', ''
    $normalized = $normalized.Trim()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $value
    }

    return $normalized
}

function Get-SitemapSlugs {
    $response = Invoke-WebRequest -UseBasicParsing -Uri 'https://ru.yummyani.me/sitemapcatalog.xml.gz' -Headers @{
        'User-Agent' = 'Mozilla/5.0'
    }

    $bytes = if ($response.Content -is [string]) {
        [System.Text.Encoding]::UTF8.GetBytes($response.Content)
    }
    else {
        $response.Content
    }

    $stream = New-Object System.IO.MemoryStream(,$bytes)
    $gzip = New-Object System.IO.Compression.GzipStream($stream, [System.IO.Compression.CompressionMode]::Decompress)
    $reader = New-Object System.IO.StreamReader($gzip)

    try {
        $xml = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $gzip.Dispose()
        $stream.Dispose()
    }

    $matches = [regex]::Matches($xml, '<loc>(?<url>[^<]+)</loc>')
    $slugs = New-Object System.Collections.Generic.List[string]

    foreach ($match in $matches) {
        $url = ($match.Groups['url'].Value ?? '').Trim()
        if ([string]::IsNullOrWhiteSpace($url)) {
            continue
        }

        $uri = [Uri]$url
        $segments = $uri.AbsolutePath.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
        if ($segments.Length -lt 3) {
            continue
        }

        if ($segments[0] -ne 'catalog' -or $segments[1] -ne 'item') {
            continue
        }

        $slug = ($segments[2] ?? '').Trim()
        if (-not [string]::IsNullOrWhiteSpace($slug)) {
            [void]$slugs.Add($slug)
        }
    }

    return @($slugs)
}

function New-ProviderStat {
    param(
        [string]$ProviderKey,
        [int]$PlayerId,
        [string]$PlayerName
    )

    return [pscustomobject]@{
        providerKey = $ProviderKey
        playerId = $PlayerId
        playerName = $PlayerName
        titles = 0
        videoRows = 0
        dubbingNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts'
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$slugs = Get-SitemapSlugs
if ($Limit -gt 0) {
    $slugs = @($slugs | Select-Object -First $Limit)
}

$results = @(
    $slugs | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
        $slug = $_
        $headers = @{
            'Accept' = 'application/json, text/plain, */*'
        }

        try {
            $uri = "https://api.yani.tv/anime/$([Uri]::EscapeDataString($slug))?need_videos=true"
            $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
            $anime = $response.response
            $videos = @($anime.videos)
            $providerRows = New-Object System.Collections.Generic.List[object]
            $providerMap = @{}

            foreach ($video in $videos) {
                if ($null -eq $video -or $null -eq $video.data) {
                    continue
                }

                $playerId = 0
                try {
                    $playerId = [int]($video.data.player_id ?? 0)
                }
                catch {
                    $playerId = 0
                }

                $playerName = ($video.data.player ?? '').Trim()
                $rawDubbing = ($video.data.dubbing ?? '').Trim()
                $providerKey = "$playerId|$playerName"

                if (-not $providerMap.ContainsKey($providerKey)) {
                    $providerMap[$providerKey] = [ordered]@{
                        providerKey = $providerKey
                        playerId = $playerId
                        playerName = $playerName
                        rowCount = 0
                        dubbings = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
                    }
                }

                $providerEntry = $providerMap[$providerKey]
                $providerEntry.rowCount++

                if (-not [string]::IsNullOrWhiteSpace($rawDubbing)) {
                    [void]$providerEntry.dubbings.Add($rawDubbing)
                }
            }

            foreach ($providerEntry in $providerMap.Values) {
                $dubbingsList = New-Object System.Collections.Generic.List[string]
                foreach ($dubbingName in $providerEntry['dubbings']) {
                    $dubbingsList.Add([string]$dubbingName) | Out-Null
                }

                $dubbings = [string[]]($dubbingsList | Sort-Object)
                $providerRows.Add([pscustomobject]@{
                    providerKey = $providerEntry['providerKey']
                    playerId = $providerEntry['playerId']
                    playerName = $providerEntry['playerName']
                    rowCount = $providerEntry['rowCount']
                    dubbings = $dubbings
                }) | Out-Null
            }

            $providerRowsArray = [object[]]$providerRows.ToArray()
            [pscustomobject]@{
                slug = $slug
                title = ($anime.title ?? '').Trim()
                videoCount = $videos.Count
                providerRows = $providerRowsArray
                error = ''
            }
        }
        catch {
            [pscustomobject]@{
                slug = $slug
                title = ''
                videoCount = 0
                providerRows = @()
                error = ($_.Exception.Message ?? '').Trim()
            }
        }
    }
)

$providerStats = @{}
$failed = New-Object System.Collections.Generic.List[object]
$titlesWithVideos = 0

foreach ($result in $results) {
    if (-not [string]::IsNullOrWhiteSpace($result.error)) {
        $failed.Add([pscustomobject]@{
            slug = $result.slug
            error = $result.error
        }) | Out-Null
        continue
    }

    if (($result.videoCount ?? 0) -gt 0) {
        $titlesWithVideos++
    }

    foreach ($providerRow in @($result.providerRows)) {
        $providerKey = ($providerRow.providerKey ?? '').Trim()
        if ([string]::IsNullOrWhiteSpace($providerKey)) {
            continue
        }

        if (-not $providerStats.ContainsKey($providerKey)) {
            $providerStats[$providerKey] = New-ProviderStat -ProviderKey $providerKey -PlayerId ([int]($providerRow.playerId ?? 0)) -PlayerName (($providerRow.playerName ?? '').Trim())
        }

        $stat = $providerStats[$providerKey]
        $stat.titles++
        $stat.videoRows += [int]($providerRow.rowCount ?? 0)

        foreach ($rawName in @($providerRow.dubbings)) {
            $normalizedName = Normalize-DubbingName -Name $rawName
            if (-not [string]::IsNullOrWhiteSpace($normalizedName)) {
                [void]$stat.dubbingNames.Add($normalizedName)
            }
        }
    }
}

$providerSummary = @(
    $providerStats.Values |
        Sort-Object -Property `
            @{ Expression = { $_.dubbingNames.Count }; Descending = $true }, `
            @{ Expression = { $_.titles }; Descending = $true }, `
            @{ Expression = { $_.videoRows }; Descending = $true }, `
            @{ Expression = { $_.playerName }; Descending = $false } |
        ForEach-Object {
            [pscustomobject]@{
                providerKey = $_.providerKey
                playerId = $_.playerId
                playerName = $_.playerName
                titles = $_.titles
                videoRows = $_.videoRows
                uniqueDubbingCount = $_.dubbingNames.Count
                sampleDubbings = @($_.dubbingNames | Sort-Object | Select-Object -First 25)
            }
        }
)

$supportedProviderIds = @(2, 3, 4)
$supportedSummary = @($providerSummary | Where-Object { $supportedProviderIds -contains $_.playerId })

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$jsonPath = Join-Path $resolvedOutputDirectory "yummy-provider-coverage-$stamp.json"
$markdownPath = Join-Path $resolvedOutputDirectory "yummy-provider-coverage-$stamp.md"
$scopeLabel = if ($Limit -gt 0) { "first $Limit sitemap catalog items" } else { 'full Yummy catalog sitemap' }
$generatedAtUtc = [DateTime]::UtcNow.ToString('o')
$catalogItemCount = $slugs.Count
$titlesFetchedCount = $results.Count - $failed.Count
$failedItems = [object[]]$failed.ToArray()

$report = [pscustomobject]@{
    generatedAtUtc = $generatedAtUtc
    scope = $scopeLabel
    catalogItems = $catalogItemCount
    titlesFetched = $titlesFetchedCount
    titlesWithVideos = $titlesWithVideos
    failures = $failedItems
    providers = $providerSummary
    supportedProviders = $supportedSummary
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Yummy Provider Coverage') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("Generated: $($report.generatedAtUtc)") | Out-Null
$lines.Add("Scope: $($report.scope)") | Out-Null
$lines.Add("CatalogItems: $($report.catalogItems)") | Out-Null
$lines.Add("TitlesFetched: $($report.titlesFetched)") | Out-Null
$lines.Add("TitlesWithVideos: $($report.titlesWithVideos)") | Out-Null
$lines.Add("Failures: $($failed.Count)") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## Providers') | Out-Null
$lines.Add('') | Out-Null

foreach ($provider in $providerSummary) {
    $lines.Add("- [$($provider.playerId)] $($provider.playerName): titles=$($provider.titles), videoRows=$($provider.videoRows), uniqueDubbings=$($provider.uniqueDubbingCount)") | Out-Null
}

$lines.Add('') | Out-Null
$lines.Add('## Supported Providers') | Out-Null
$lines.Add('') | Out-Null

foreach ($provider in $supportedSummary) {
    $lines.Add("- [$($provider.playerId)] $($provider.playerName): titles=$($provider.titles), videoRows=$($provider.videoRows), uniqueDubbings=$($provider.uniqueDubbingCount)") | Out-Null
}

[System.IO.File]::WriteAllLines($markdownPath, $lines)

[pscustomobject]@{
    generatedAtUtc = $report.generatedAtUtc
    scope = $report.scope
    catalogItems = $report.catalogItems
    titlesFetched = $report.titlesFetched
    titlesWithVideos = $report.titlesWithVideos
    failures = $failed.Count
    topProvider = $providerSummary | Select-Object -First 1
    topSupportedProvider = $supportedSummary | Select-Object -First 1
    outputFiles = @{
        json = $jsonPath
        markdown = $markdownPath
    }
} | ConvertTo-Json -Depth 6
