$ErrorActionPreference = 'Stop'
$voiceNamesPath = Join-Path $PSScriptRoot 'VoiceNames.ps1'
. $voiceNamesPath

$cases = @(
    @{ Name = 'Озвучка AniLiberty'; Expected = 'anilibria' }
    @{ Name = 'Студийная Банда'; Expected = 'studioband' }
    @{ Name = 'Studio Band'; Expected = 'studioband' }
    @{ Name = 'AniLeague.TV'; Expected = 'anileague' }
    @{ Name = '2х2'; Expected = '2x2' }
    @{ Name = 'Dream Cast'; Expected = 'dreamcast' }
    @{ Name = ''; Expected = '' }
)

foreach ($case in $cases) {
    if ((Normalize-VoiceKey $case.Name) -cne $case.Expected) {
        throw "Wrong voice key for '$($case.Name)'"
    }
}
if ((Normalize-VoiceName ' Озвучка Dream Cast ') -cne 'Dream Cast') {
    throw 'Diagnostic display names must preserve spelling and spaces.'
}

$results = @($cases | ForEach-Object -ThrottleLimit 2 -Parallel {
    . $using:voiceNamesPath
    if ((Normalize-VoiceKey $_.Name) -cne $_.Expected) {
        throw "Wrong parallel voice key for '$($_.Name)'"
    }
    $_.Name
})
if ($results.Count -ne $cases.Count) { throw 'A parallel normalization did not complete.' }

foreach ($name in @('build-provider-voice-crosswalk.ps1', 'find-yummy-crosswalk-anchor.ps1')) {
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $name), [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
Write-Output 'PASS canonical diagnostic voice keys, display names and parallel runspaces'
