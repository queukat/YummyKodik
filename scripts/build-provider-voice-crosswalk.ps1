param(
    [string]$Slug,
    [string]$ConfigPath = 'C:\ProgramData\Jellyfin\Server\plugins\configurations\YummyKodik.xml',
    [string]$AllohaTokenPath = 'C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.1.0.0\AllohaApiToken.txt',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

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

function Resolve-KodikToken {
    param([string]$ConfiguredToken)

    $trimmed = ($ConfiguredToken ?? '').Trim()
    if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
        return $trimmed
    }

    function Decode-KodikAtob {
        param([string]$Value)

        $b64 = ($Value ?? '').Trim()
        if ([string]::IsNullOrWhiteSpace($b64)) {
            return ''
        }

        $pad = (4 - ($b64.Length % 4)) % 4
        if ($pad -ne 0) {
            $b64 = $b64 + ('=' * $pad)
        }

        try {
            $bytes = [Convert]::FromBase64String($b64)
            return [System.Text.Encoding]::UTF8.GetString($bytes)
        }
        catch {
            return ''
        }
    }

    function Invoke-KodikSalt {
        param([string]$InputString)

        function Convert-ToInt32Unchecked {
            param([int64]$Value)

            $wrapped = $Value % 4294967296
            if ($wrapped -lt 0) {
                $wrapped += 4294967296
            }

            if ($wrapped -gt 2147483647) {
                $wrapped -= 4294967296
            }

            return [int]$wrapped
        }

        $inputValue = $InputString ?? ''
        [int]$hash = 0
        foreach ($charValue in $inputValue.ToCharArray()) {
            $hash = Convert-ToInt32Unchecked -Value (([int64]$hash -shl 5) - [int64]$hash + [int64][int][char]$charValue)
        }

        $unsignedHash = if ($hash -lt 0) {
            [uint32]([int64]$hash + 4294967296)
        }
        else {
            [uint32]$hash
        }
        $builder = New-Object System.Text.StringBuilder
        $bitIndex = 0
        for ($j = 29; $j -ge 0; $j -= 3) {
            $x = (((($unsignedHash -shr $bitIndex) -band 7) -shl 3) + (($unsignedHash -shr $j) -band 7))
            [int]$cc = 0
            if ($x -lt 26) {
                $cc = 97 + $x
            }
            elseif ($x -lt 52) {
                $cc = 39 + $x
            }
            else {
                $cc = $x - 4
            }

            [void]$builder.Append([char]$cc)
            $bitIndex += 3
        }

        return $builder.ToString()
    }

    function Decode-KodikSecret {
        param(
            [int[]]$Numbers,
            [string]$Password
        )

        if ($null -eq $Numbers -or $Numbers.Count -eq 0) {
            return ''
        }

        $pwd = $Password ?? ''
        if ([string]::IsNullOrWhiteSpace($pwd)) {
            return ''
        }

        $hash = Invoke-KodikSalt -InputString ("123456789" + $pwd)
        $hashBuilder = New-Object System.Text.StringBuilder($hash)
        while ($hashBuilder.Length -lt $Numbers.Count) {
            [void]$hashBuilder.Append($hash)
        }

        $hashString = $hashBuilder.ToString()
        $result = New-Object System.Text.StringBuilder
        for ($i = 0; $i -lt $Numbers.Count; $i++) {
            [void]$result.Append([char]($Numbers[$i] -bxor [int][char]$hashString[$i]))
        }

        return $result.ToString()
    }

    $onlineMod = curl.exe -s --compressed 'https://raw.githubusercontent.com/nb557/plugins/refs/heads/main/online_mod.js'
    if (-not [string]::IsNullOrWhiteSpace($onlineMod)) {
        $regexes = @(
            "(?:(?:var|let|const)\s+token\s*=|token\s*=)\s*(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?<pwd>atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)",
            "(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?<pwd>atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)"
        )

        foreach ($pattern in $regexes) {
            $match = [regex]::Match($onlineMod, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
            if (-not $match.Success) {
                continue
            }

            $numbers = [regex]::Matches(($match.Groups['list'].Value ?? ''), '\d+') |
                ForEach-Object { [int]$_.Value }
            if ($numbers.Count -eq 0) {
                continue
            }

            $password = 'kodik'
            $b64 = ($match.Groups['b64'].Value ?? '').Trim()
            if (-not [string]::IsNullOrWhiteSpace($b64)) {
                $decodedPassword = Decode-KodikAtob -Value $b64
                if (-not [string]::IsNullOrWhiteSpace($decodedPassword)) {
                    $password = $decodedPassword
                }
            }
            else {
                $plain1 = ($match.Groups['plain1'].Value ?? '').Trim()
                $plain2 = ($match.Groups['plain2'].Value ?? '').Trim()
                if (-not [string]::IsNullOrWhiteSpace($plain1)) {
                    $password = $plain1
                }
                elseif (-not [string]::IsNullOrWhiteSpace($plain2)) {
                    $password = $plain2
                }
            }

            $decodedToken = Decode-KodikSecret -Numbers $numbers -Password $password
            if (-not [string]::IsNullOrWhiteSpace($decodedToken)) {
                return $decodedToken.Trim()
            }
        }
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

function Add-NameToBucket {
    param(
        [hashtable]$Bucket,
        [string]$Provider,
        [string]$Name
    )

    $normalizedName = Normalize-VoiceName -Name $Name
    $key = Normalize-VoiceKey -Name $normalizedName
    if ([string]::IsNullOrWhiteSpace($normalizedName) -or [string]::IsNullOrWhiteSpace($key)) {
        return
    }

    if (-not $Bucket.ContainsKey($key)) {
        $Bucket[$key] = @{
            yummy = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
            kodik = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
            cvh = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
            alloha = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
        }
    }

    [void]$Bucket[$key][$Provider].Add($normalizedName)
}

function To-SortedArray {
    param([object]$Value)

    $result = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Value)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
            $result.Add([string]$item) | Out-Null
        }
    }

    return [string[]]@($result | Sort-Object -Unique)
}

function Join-Names {
    param([object]$Value)

    $items = To-SortedArray -Value $Value
    if ($items.Count -eq 0) {
        return ''
    }

    return [string]::Join(', ', $items)
}

if ([string]::IsNullOrWhiteSpace($Slug)) {
    throw 'Slug is required.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts'
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

[xml]$configXml = Get-Content -Raw -LiteralPath $ConfigPath
$pluginConfig = $configXml.PluginConfiguration
$configuredKodikToken = ($pluginConfig.KodikToken ?? '').Trim()
$allohaApiToken = if (Test-Path -LiteralPath $AllohaTokenPath) {
    (Get-Content -Raw -LiteralPath $AllohaTokenPath).Trim()
}
else {
    ''
}
$kodikToken = Resolve-KodikToken -ConfiguredToken $configuredKodikToken

$animeResponse = Invoke-RestMethod -Method Get -Uri ("https://api.yani.tv/anime/" + [Uri]::EscapeDataString($Slug) + "?need_videos=true") -Headers @{
    'Accept' = 'application/json, text/plain, */*'
}
$anime = $animeResponse.response

$bucket = @{}
$usedKodikYummyFallback = $false

$videos = @($anime.videos)
foreach ($video in $videos) {
    if ($null -eq $video -or $null -eq $video.data) {
        continue
    }

    $playerId = [int]($video.data.player_id ?? 0)
    switch ($playerId) {
        4 {
            Add-NameToBucket -Bucket $bucket -Provider 'yummy' -Name ($video.data.dubbing ?? '')
        }
        3 {
            Add-NameToBucket -Bucket $bucket -Provider 'yummy' -Name ($video.data.dubbing ?? '')
        }
        2 {
            Add-NameToBucket -Bucket $bucket -Provider 'yummy' -Name ($video.data.dubbing ?? '')
        }
        default { }
    }
}

if (-not [string]::IsNullOrWhiteSpace($kodikToken)) {
    try {
        $kodikResponse = Invoke-RestMethod -Method Post -Uri 'https://kodik-api.com/search' -Body @{
            token = $kodikToken
            limit = '100'
            all = 'true'
            with_episodes = 'true'
            with_episodes_data = 'false'
            shikimori_id = [string]($anime.remote_ids.shikimori_id ?? 0)
        } -ContentType 'application/x-www-form-urlencoded'

        foreach ($result in @($kodikResponse.results)) {
            if ($null -ne $result.translation) {
                Add-NameToBucket -Bucket $bucket -Provider 'kodik' -Name ($result.translation.title ?? '')
            }
        }
    }
    catch {
        $usedKodikYummyFallback = $true
    }
}
else {
    $usedKodikYummyFallback = $true
}

if ($usedKodikYummyFallback) {
    foreach ($video in $videos | Where-Object { $_.data.player_id -eq 4 }) {
        Add-NameToBucket -Bucket $bucket -Provider 'kodik' -Name ($video.data.dubbing ?? '')
    }
}

$cvhAnimeId = $null
foreach ($video in $videos | Where-Object { $_.data.player_id -eq 3 }) {
    $iframeUrl = ($video.iframe_url ?? '').Trim()
    if ([string]::IsNullOrWhiteSpace($iframeUrl)) {
        continue
    }

    $absoluteUrl = if ($iframeUrl.StartsWith('//', [System.StringComparison]::Ordinal)) {
        "https:$iframeUrl"
    }
    else {
        $iframeUrl
    }

    $uri = [Uri]$absoluteUrl
    $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
    $candidate = ($query['anime_id'] ?? '').Trim()
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        $cvhAnimeId = $candidate
        break
    }
}

if (-not [string]::IsNullOrWhiteSpace($cvhAnimeId)) {
    $cvhResponse = Invoke-RestMethod -Method Get -Uri ("https://plapi.cdnvideohub.com/api/v1/player/sv/playlist?pub=745&id=" + [Uri]::EscapeDataString($cvhAnimeId) + "&aggr=mali") -Headers @{
        'Accept' = 'application/json, text/plain, */*'
        'Accept-Language' = 'ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7'
        'Origin' = 'https://ru.yummyani.me'
        'Referer' = "https://ru.yummyani.me/iframeCVH.html?anime_id=$cvhAnimeId&episode=1"
        'Sec-Fetch-Site' = 'cross-site'
        'Sec-Fetch-Mode' = 'cors'
        'Sec-Fetch-Dest' = 'empty'
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36'
    }

    foreach ($item in @($cvhResponse.items)) {
        Add-NameToBucket -Bucket $bucket -Provider 'cvh' -Name ($item.voiceStudio ?? '')
    }
}

if (-not [string]::IsNullOrWhiteSpace($allohaApiToken) -and [long]($anime.remote_ids.kp_id ?? 0) -gt 0) {
    $allohaResponse = Invoke-RestMethod -Method Get -SkipCertificateCheck -Uri ("https://api.alloha.tv/?token=" + [Uri]::EscapeDataString($allohaApiToken) + "&kp=" + [Uri]::EscapeDataString([string]$anime.remote_ids.kp_id)) -Headers @{
        'Accept' = 'application/json,text/plain,*/*'
    }

    if ($allohaResponse.status -eq 'success' -and $null -ne $allohaResponse.data -and $null -ne $allohaResponse.data.seasons) {
        foreach ($seasonProperty in $allohaResponse.data.seasons.PSObject.Properties) {
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

                    Add-NameToBucket -Bucket $bucket -Provider 'alloha' -Name ($translation.translation ?? '')
                }
            }
        }
    }
}

$rows = @(
    foreach ($key in ($bucket.Keys | Sort-Object)) {
        $entry = $bucket[$key]
        $yummyNames = To-SortedArray -Value $entry.yummy
        $kodikNames = To-SortedArray -Value $entry.kodik
        $cvhNames = To-SortedArray -Value $entry.cvh
        $allohaNames = To-SortedArray -Value $entry.alloha
        $canonicalName = ''
        foreach ($nameList in @($yummyNames, $kodikNames, $cvhNames, $allohaNames)) {
            $candidate = @($nameList | Select-Object -First 1)
            if ($candidate.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($candidate[0])) {
                $canonicalName = [string]$candidate[0]
                break
            }
        }

        $allNamesList = New-Object System.Collections.Generic.List[string]
        foreach ($nameList in @($yummyNames, $kodikNames, $cvhNames, $allohaNames)) {
            foreach ($name in @($nameList)) {
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    $allNamesList.Add([string]$name) | Out-Null
                }
            }
        }

        $allNames = To-SortedArray -Value $allNamesList
        $aliases = [string[]]@($allNames | Where-Object { $_ -ne $canonicalName })

        [pscustomobject]@{
            key = $key
            canonicalName = $canonicalName
            yummy = $yummyNames
            kodik = $kodikNames
            cvh = $cvhNames
            alloha = $allohaNames
            aliases = $aliases
        }
    }
)

$suggestedAliases = @(
    foreach ($row in $rows) {
        foreach ($alias in @($row.aliases)) {
            [pscustomobject]@{
                alias = $alias
                aliasKey = Normalize-VoiceKey -Name $alias
                canonicalName = $row.canonicalName
                canonicalKey = $row.key
            }
        }
    }
)

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$jsonPath = Join-Path $resolvedOutputDirectory "provider-voice-crosswalk-$stamp.json"
$markdownPath = Join-Path $resolvedOutputDirectory "provider-voice-crosswalk-$stamp.md"

$report = [pscustomobject]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    slug = $Slug
    title = ($anime.title ?? '').Trim()
    animeId = [long]($anime.anime_id ?? 0)
    shikimoriId = [long]($anime.remote_ids.shikimori_id ?? 0)
    kpId = [long]($anime.remote_ids.kp_id ?? 0)
    kodikUsedYummyFallback = $usedKodikYummyFallback
    rows = $rows
    suggestedAliases = $suggestedAliases
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Provider Voice Crosswalk') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("Generated: $($report.generatedAtUtc)") | Out-Null
$lines.Add("Title: $($report.title)") | Out-Null
$lines.Add("Slug: $($report.slug)") | Out-Null
$lines.Add("animeId: $($report.animeId)") | Out-Null
$lines.Add("shikimoriId: $($report.shikimoriId)") | Out-Null
$lines.Add("kpId: $($report.kpId)") | Out-Null
$lines.Add("kodikUsedYummyFallback: $($report.kodikUsedYummyFallback)") | Out-Null
$lines.Add('') | Out-Null

foreach ($row in $rows) {
    $lines.Add("## $($row.canonicalName) [$($row.key)]") | Out-Null
    $lines.Add("- Yummy: $(Join-Names -Value $row.yummy)") | Out-Null
    $lines.Add("- Kodik: $(Join-Names -Value $row.kodik)") | Out-Null
    $lines.Add("- CVH: $(Join-Names -Value $row.cvh)") | Out-Null
    $lines.Add("- Alloha: $(Join-Names -Value $row.alloha)") | Out-Null
    if (@($row.aliases).Count -gt 0) {
        $lines.Add("- Aliases: $(Join-Names -Value $row.aliases)") | Out-Null
    }
    $lines.Add('') | Out-Null
}

[System.IO.File]::WriteAllLines($markdownPath, $lines)

[pscustomobject]@{
    generatedAtUtc = $report.generatedAtUtc
    slug = $report.slug
    title = $report.title
    rowCount = $rows.Count
    aliasSuggestionCount = $suggestedAliases.Count
    kodikUsedYummyFallback = $report.kodikUsedYummyFallback
    outputFiles = @{
        json = $jsonPath
        markdown = $markdownPath
    }
} | ConvertTo-Json -Depth 6
