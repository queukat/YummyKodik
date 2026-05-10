param(
    [int[]]$Episodes = @(1, 2),
    [string]$TokenMovie = 'bd5bf5242d7707b7b5028d2b8d792d',
    [string]$Token = '8b5512267a2a52e9de06d67d342e0c',
    [int]$Translation = 10,
    [int]$Season = 4
)

$ErrorActionPreference = 'Stop'
$AcceptsControlsPrefix = '9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9'

function Invoke-YfInverseTransform {
    param([string]$InputString)

    $chars = $InputString.ToCharArray()
    [Array]::Reverse($chars)
    return -join $chars
}

function Invoke-YcInverseTransform {
    param([string]$InputString)

    $builder = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $InputString.Length; $i += 2) {
        $pair = $InputString.Substring($i, [Math]::Min(2, $InputString.Length - $i))
        $pairChars = $pair.ToCharArray()
        [Array]::Reverse($pairChars)
        [void]$builder.Append((-join $pairChars))
    }

    return $builder.ToString()
}

function Invoke-YcTransform {
    param([string]$InputString)

    $builder = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $InputString.Length; $i += 2) {
        $pair = $InputString.Substring($i, [Math]::Min(2, $InputString.Length - $i))
        $pairChars = $pair.ToCharArray()
        [Array]::Reverse($pairChars)
        [void]$builder.Append((-join $pairChars))
    }

    return $builder.ToString()
}

function Get-AllohaEpisodeTracks {
    param([int]$Episode)

    $baseReferer = "https://alloha.yani.tv/?token_movie=$TokenMovie&translation=$Translation&season=$Season&episode=$Episode&token=$Token&hidden=translation,season,episode"
    $iframeUrl = "$baseReferer&_r=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"

    $iframeHeaders = @{
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36'
        'Accept' = 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8'
        'Accept-Language' = 'ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7'
        'Referer' = 'https://site.yummyani.me/'
        'Sec-Fetch-Dest' = 'iframe'
        'Sec-Fetch-Mode' = 'navigate'
        'Sec-Fetch-Site' = 'cross-site'
        'Upgrade-Insecure-Requests' = '1'
    }

    $iframeHtml = (Invoke-WebRequest -UseBasicParsing -Uri $iframeUrl -Headers $iframeHeaders).Content
    $viewportMatch = [regex]::Match($iframeHtml, '<meta\s+name="viewporti"\s+content="(.*?)"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $fileListMatch = [regex]::Match($iframeHtml, "const\s+fileList\s*=\s*JSON\.parse\('(.*?)'\);", [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if (-not $viewportMatch.Success) {
        throw "Episode ${Episode}: viewporti token not found."
    }

    if (-not $fileListMatch.Success) {
        throw "Episode ${Episode}: fileList token not found."
    }

    $viewportToken = $viewportMatch.Groups[1].Value
    $fileListToken = $fileListMatch.Groups[1].Value
    $viewportDecoded = Invoke-YfInverseTransform -InputString $viewportToken
    $borthSuffix = Invoke-YcTransform -InputString (Invoke-YcInverseTransform -InputString $viewportDecoded)
    $borth = "$AcceptsControlsPrefix|$borthSuffix"
    $fileListJson = [regex]::Unescape($fileListMatch.Groups[1].Value) | ConvertFrom-Json
    $fileId = [string]$fileListJson.active.id

    $postHeaders = @{
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36'
        'Accept' = '*/*'
        'Accept-Language' = 'ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7'
        'Borth' = $borth
        'Origin' = 'https://alloha.yani.tv'
        'Referer' = $baseReferer
        'Sec-Fetch-Dest' = 'empty'
        'Sec-Fetch-Mode' = 'cors'
        'Sec-Fetch-Site' = 'same-origin'
        'X-Requested-With' = 'XMLHttpRequest'
    }

    $body = "token=$Token&av1=true&autoplay=0&audio=&subtitle="
    $bnsiJson = curl.exe -s --compressed -X POST `
        -H "User-Agent: $($postHeaders['User-Agent'])" `
        -H "Accept: $($postHeaders['Accept'])" `
        -H "Accept-Language: $($postHeaders['Accept-Language'])" `
        -H "Borth: $($postHeaders['Borth'])" `
        -H "Origin: $($postHeaders['Origin'])" `
        -H "Referer: $($postHeaders['Referer'])" `
        -H "Sec-Fetch-Dest: $($postHeaders['Sec-Fetch-Dest'])" `
        -H "Sec-Fetch-Mode: $($postHeaders['Sec-Fetch-Mode'])" `
        -H "Sec-Fetch-Site: $($postHeaders['Sec-Fetch-Site'])" `
        -H "X-Requested-With: $($postHeaders['X-Requested-With'])" `
        -H "Content-Type: application/x-www-form-urlencoded; charset=UTF-8" `
        --data-raw $body `
        "https://alloha.yani.tv/bnsi/movies/$fileId"

    if ([string]::IsNullOrWhiteSpace($bnsiJson)) {
        throw "Episode ${Episode}: bnsi returned an empty response."
    }

    $bnsi = $bnsiJson | ConvertFrom-Json

    if ($bnsi.error) {
        throw "Episode ${Episode}: bnsi error: $($bnsi.error)"
    }

    $tracks = @()
    foreach ($track in @($bnsi.source.hls)) {
        $qualities = @($track.quality.PSObject.Properties.Name | Sort-Object { [int]$_ } -Descending)
        $tracks += [pscustomobject]@{
            label = $track.label
            audioId = [string]$track.audio_id
            qualities = $qualities
        }
    }

    return [pscustomobject]@{
        episode = $Episode
        fileId = $fileId
        tracks = $tracks
    }
}

$result = foreach ($episode in $Episodes) {
    Get-AllohaEpisodeTracks -Episode $episode
}

$result | ConvertTo-Json -Depth 8
