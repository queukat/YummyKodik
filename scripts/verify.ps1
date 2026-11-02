param(
    [ValidateSet('Info', 'Full', 'Tests')]
    [string]$Mode = 'Full',
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$FfmpegSmoke,
    [string]$FfmpegPath = 'ffmpeg',
    [string]$FfprobePath = 'ffprobe'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$timer = [Diagnostics.Stopwatch]::StartNew()

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    if ($Mode -eq 'Info') {
        [xml]$project = Get-Content YummyKodik/YummyKodik.csproj
        Write-Host "Project: $root"
        Write-Host "Plugin: $($project.Project.PropertyGroup.Version) / $($project.Project.PropertyGroup.TargetFramework)"
        Invoke-Checked git @('status', '--short', '--branch')
        foreach ($tool in @('dotnet', 'node', $FfmpegPath, $FfprobePath)) {
            $command = Get-Command $tool -ErrorAction SilentlyContinue
            if ($command) {
                Write-Host "$tool : $($command.Source)"
                $versionArgument = if ($tool -in @($FfmpegPath, $FfprobePath)) { '-version' } else { '--version' }
                & $command.Source $versionArgument | Select-Object -First 1
            } else { Write-Host "$tool : not found" }
        }
        if ($IsWindows) {
            $service = Get-Service Jellyfin -ErrorAction SilentlyContinue
            if ($service) { Write-Host "Jellyfin service: $($service.Status)" }
            Get-ChildItem "$env:ProgramData/Jellyfin/Server/plugins/YummyKodik_*/YummyKodik.dll" -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host "Installed plugin: $($_.FullName) / $([Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version)" }
        }
        return
    }

    if ($Mode -eq 'Full' -and $Filter) { throw 'Use -Mode Tests with -Filter; Full always runs the entire suite.' }
    if ($FfmpegSmoke) {
        $FfmpegPath = (Get-Command $FfmpegPath -ErrorAction Stop).Source
        $FfprobePath = (Get-Command $FfprobePath -ErrorAction Stop).Source
    }
    if (-not $NoBuild) { Invoke-Checked dotnet @('build', './YummyKodik.sln', '-c', 'Release', '--nologo') }
    $testArguments = @('run', '--project', './YummyKodik.Tests/YummyKodik.Tests.csproj', '-c', 'Release', '--no-build')
    if ($Filter) { Invoke-Checked dotnet ($testArguments + @('--', '--filter', $Filter)) }
    else { Invoke-Checked dotnet $testArguments }

    if ($Mode -eq 'Full') {
        foreach ($test in @('test-config-page.cjs', 'test-series-translation.cjs', 'test-playback-buffer.cjs')) {
            Invoke-Checked node @("scripts/$test")
        }
        & ./scripts/test-voice-names.ps1
    }
    if ($FfmpegSmoke) {
        Invoke-Checked dotnet ($testArguments + @('--', '--ffmpeg-smoke', $FfmpegPath, $FfprobePath))
    }
    Write-Host ('Verification passed in {0:N1}s.' -f $timer.Elapsed.TotalSeconds)
} finally {
    Pop-Location
}
