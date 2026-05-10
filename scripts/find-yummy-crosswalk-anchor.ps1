param(
    [int]$Limit = 0,
    [int]$ThrottleLimit = 12,
    [int]$Top = 20,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

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

function Normalize-VoiceName {
    param([string]$Name)

    $value = ($Name ?? '').Trim()
    if ($value.StartsWith('Озвучка ', [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring('Озвучка '.Length).Trim()
    }

    return $value
}

function Normalize-VoiceKey {
    param([string]$Name)

    $value = Normalize-VoiceName -Name $Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ''
    }

    $trailingAliasMatch = [regex]::Match($value, '^(?<primary>.+?)\s*\((?<alias>[^()]+)\)\s*$')
    if ($trailingAliasMatch.Success) {
        $primary = ($trailingAliasMatch.Groups['primary'].Value ?? '').Trim()
        $alias = ($trailingAliasMatch.Groups['alias'].Value ?? '').Trim()
        if ($primary.Length -gt 0 -and $alias.Length -gt 0) {
            $value = if ($alias.Length -le $primary.Length) { $alias } else { $primary }
        }
    }

    $tokens = [regex]::Matches($value.ToLowerInvariant(), '[\p{L}\p{Nd}]+') | ForEach-Object { $_.Value }
    $tokenList = New-Object System.Collections.Generic.List[string]
    foreach ($token in $tokens) {
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $tokenList.Add($token) | Out-Null
        }
    }

    if ($tokenList.Count -gt 0 -and $tokenList[0] -eq 'озвучка') {
        $tokenList.RemoveAt(0)
    }

    while ($tokenList.Count -gt 0 -and $tokenList[$tokenList.Count - 1] -eq 'tv') {
        $tokenList.RemoveAt($tokenList.Count - 1)
    }

    $key = [string]::Concat($tokenList.ToArray())
    switch ($key) {
        '2х2' { return '2x2' }
        'anilib' { return 'anilibria' }
        'aniliberty' { return 'anilibria' }
        'anilibertyanilibria' { return 'anilibria' }
        default { return $key }
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

        function Normalize-VoiceNameInner {
            param([string]$Name)
            $value = ($Name ?? '').Trim()
            if ($value.StartsWith('Озвучка ', [System.StringComparison]::OrdinalIgnoreCase)) {
                $value = $value.Substring('Озвучка '.Length).Trim()
            }

            return $value
        }

        function Normalize-VoiceKeyInner {
            param([string]$Name)

            $value = Normalize-VoiceNameInner -Name $Name
            if ([string]::IsNullOrWhiteSpace($value)) {
                return ''
            }

            $trailingAliasMatch = [regex]::Match($value, '^(?<primary>.+?)\s*\((?<alias>[^()]+)\)\s*$')
            if ($trailingAliasMatch.Success) {
                $primary = ($trailingAliasMatch.Groups['primary'].Value ?? '').Trim()
                $alias = ($trailingAliasMatch.Groups['alias'].Value ?? '').Trim()
                if ($primary.Length -gt 0 -and $alias.Length -gt 0) {
                    $value = if ($alias.Length -le $primary.Length) { $alias } else { $primary }
                }
            }

            $tokens = [regex]::Matches($value.ToLowerInvariant(), '[\p{L}\p{Nd}]+') | ForEach-Object { $_.Value }
            $tokenList = New-Object System.Collections.Generic.List[string]
            foreach ($token in $tokens) {
                if (-not [string]::IsNullOrWhiteSpace($token)) {
                    $tokenList.Add($token) | Out-Null
                }
            }

            if ($tokenList.Count -gt 0 -and $tokenList[0] -eq 'озвучка') {
                $tokenList.RemoveAt(0)
            }

            while ($tokenList.Count -gt 0 -and $tokenList[$tokenList.Count - 1] -eq 'tv') {
                $tokenList.RemoveAt($tokenList.Count - 1)
            }

            $key = [string]::Concat($tokenList.ToArray())
            switch ($key) {
                '2х2' { return '2x2' }
                'anilib' { return 'anilibria' }
                'aniliberty' { return 'anilibria' }
                'anilibertyanilibria' { return 'anilibria' }
                default { return $key }
            }
        }

        try {
            $response = Invoke-RestMethod -Method Get -Uri ("https://api.yani.tv/anime/" + [Uri]::EscapeDataString($slug) + "?need_videos=true") -Headers @{
                'Accept' = 'application/json, text/plain, */*'
            }

            $anime = $response.response
            $videos = @($anime.videos)
            if ($videos.Count -eq 0) {
                return [pscustomobject]@{
                    slug = $slug
                    title = ($anime.title ?? '').Trim()
                    animeId = [long]($anime.anime_id ?? 0)
                    kpId = [long]($anime.remote_ids.kp_id ?? 0)
                    shikimoriId = [long]($anime.remote_ids.shikimori_id ?? 0)
                    kodikCount = 0
                    cvhCount = 0
                    allohaCount = 0
                    commonCount = 0
                    totalScore = 0
                    rawNames = [pscustomobject]@{ kodik = @(); cvh = @(); alloha = @() }
                    commonKeys = @()
                    error = ''
                }
            }

            $providerKeys = @{
                4 = 'kodik'
                3 = 'cvh'
                2 = 'alloha'
            }

            $providerNames = @{
                kodik = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
                cvh = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
                alloha = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
            }

            $providerNormalized = @{
                kodik = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
                cvh = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
                alloha = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
            }

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

                if (-not $providerKeys.ContainsKey($playerId)) {
                    continue
                }

                $providerName = $providerKeys[$playerId]
                $voiceName = Normalize-VoiceNameInner -Name ($video.data.dubbing ?? '')
                $voiceKey = Normalize-VoiceKeyInner -Name $voiceName

                if (-not [string]::IsNullOrWhiteSpace($voiceName)) {
                    [void]$providerNames[$providerName].Add($voiceName)
                }

                if (-not [string]::IsNullOrWhiteSpace($voiceKey)) {
                    [void]$providerNormalized[$providerName].Add($voiceKey)
                }
            }

            $kodikKeys = [string[]]$providerNormalized['kodik']
            $cvhKeys = [string[]]$providerNormalized['cvh']
            $allohaKeys = [string[]]$providerNormalized['alloha']

            $commonKeys = @(
                $kodikKeys |
                    Where-Object { $providerNormalized['cvh'].Contains($_) -and $providerNormalized['alloha'].Contains($_) } |
                    Sort-Object -Unique
            )

            $kodikNames = @($providerNames['kodik'] | Sort-Object)
            $cvhNames = @($providerNames['cvh'] | Sort-Object)
            $allohaNames = @($providerNames['alloha'] | Sort-Object)

            [pscustomobject]@{
                slug = $slug
                title = ($anime.title ?? '').Trim()
                animeId = [long]($anime.anime_id ?? 0)
                kpId = [long]($anime.remote_ids.kp_id ?? 0)
                shikimoriId = [long]($anime.remote_ids.shikimori_id ?? 0)
                kodikCount = $kodikNames.Count
                cvhCount = $cvhNames.Count
                allohaCount = $allohaNames.Count
                commonCount = $commonKeys.Count
                totalScore = $kodikNames.Count + $cvhNames.Count + $allohaNames.Count + ($commonKeys.Count * 5)
                rawNames = [pscustomobject]@{
                    kodik = $kodikNames
                    cvh = $cvhNames
                    alloha = $allohaNames
                }
                commonKeys = $commonKeys
                error = ''
            }
        }
        catch {
            [pscustomobject]@{
                slug = $slug
                title = ''
                animeId = 0
                kpId = 0
                shikimoriId = 0
                kodikCount = 0
                cvhCount = 0
                allohaCount = 0
                commonCount = 0
                totalScore = 0
                rawNames = [pscustomobject]@{ kodik = @(); cvh = @(); alloha = @() }
                commonKeys = @()
                error = ($_.Exception.Message ?? '').Trim()
            }
        }
    }
)

$candidates = @(
    $results |
        Where-Object {
            [string]::IsNullOrWhiteSpace($_.error) -and
            $_.kodikCount -gt 0 -and
            $_.cvhCount -gt 0 -and
            $_.allohaCount -gt 0
        } |
        Sort-Object -Property `
            @{ Expression = { $_.commonCount }; Descending = $true }, `
            @{ Expression = { $_.totalScore }; Descending = $true }, `
            @{ Expression = { $_.kodikCount }; Descending = $true }, `
            @{ Expression = { $_.cvhCount }; Descending = $true }, `
            @{ Expression = { $_.allohaCount }; Descending = $true }, `
            @{ Expression = { $_.title }; Descending = $false }
)

$topCandidates = @($candidates | Select-Object -First $Top)
$failures = @($results | Where-Object { -not [string]::IsNullOrWhiteSpace($_.error) } | Select-Object slug, error)

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$jsonPath = Join-Path $resolvedOutputDirectory "yummy-crosswalk-anchor-$stamp.json"
$markdownPath = Join-Path $resolvedOutputDirectory "yummy-crosswalk-anchor-$stamp.md"

$report = [pscustomobject]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = if ($Limit -gt 0) { "first $Limit sitemap catalog items" } else { 'full Yummy catalog sitemap' }
    catalogItems = $slugs.Count
    candidateCount = $candidates.Count
    topCandidates = $topCandidates
    failures = $failures
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Yummy Crosswalk Anchor Candidates') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("Generated: $($report.generatedAtUtc)") | Out-Null
$lines.Add("Scope: $($report.scope)") | Out-Null
$lines.Add("CatalogItems: $($report.catalogItems)") | Out-Null
$lines.Add("CandidateCount: $($report.candidateCount)") | Out-Null
$lines.Add("Failures: $($failures.Count)") | Out-Null
$lines.Add('') | Out-Null

foreach ($candidate in $topCandidates) {
    $lines.Add("## $($candidate.title)") | Out-Null
    $lines.Add("- slug: $($candidate.slug)") | Out-Null
    $lines.Add("- animeId: $($candidate.animeId)") | Out-Null
    $lines.Add("- shikimoriId: $($candidate.shikimoriId)") | Out-Null
    $lines.Add("- kpId: $($candidate.kpId)") | Out-Null
    $lines.Add("- counts: kodik=$($candidate.kodikCount), cvh=$($candidate.cvhCount), alloha=$($candidate.allohaCount), commonKeys=$($candidate.commonCount)") | Out-Null
    $lines.Add("- commonKeys: $([string]::Join(', ', $candidate.commonKeys))") | Out-Null
    $lines.Add('') | Out-Null
}

[System.IO.File]::WriteAllLines($markdownPath, $lines)

[pscustomobject]@{
    generatedAtUtc = $report.generatedAtUtc
    scope = $report.scope
    catalogItems = $report.catalogItems
    candidateCount = $report.candidateCount
    topCandidate = $topCandidates | Select-Object -First 1
    outputFiles = @{
        json = $jsonPath
        markdown = $markdownPath
    }
} | ConvertTo-Json -Depth 8
