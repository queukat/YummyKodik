param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'YummyKodik\YummyKodik.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$package = Join-Path $artifacts 'package'
$zip = Join-Path $artifacts "YummyKodik_$Version.zip"
$md5 = "$zip.md5"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

foreach ($path in @($publish, $package)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

foreach ($path in @($zip, $md5)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

dotnet restore $project
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet publish $project -c Release -o $publish `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Force -Path $package | Out-Null

$files = @(
    'YummyKodik.dll',
    'YummyKodik.deps.json',
    'HtmlAgilityPack.dll',
    'YummyKodik.pdb'
)

foreach ($file in $files) {
    $source = Join-Path $publish $file
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $package
    }
}

foreach ($asset in @('logo.png', 'logo.svg')) {
    $source = Join-Path $root "YummyKodik\Assets\$asset"
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $package
    }
}

Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
(Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant() | Set-Content -LiteralPath $md5 -NoNewline

Write-Host "Created: $zip"
Write-Host "MD5:     $md5"
