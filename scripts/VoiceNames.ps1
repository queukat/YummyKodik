# Load the canonical production rules once per PowerShell process, including parallel runspaces.
if (-not ('YummyKodik.Util.TranslationNameKeyNormalizer' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../YummyKodik/Util/TranslationNameKeyNormalizer.cs') -CompilerOptions '/nullable:enable'
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

    return [YummyKodik.Util.TranslationNameKeyNormalizer]::Normalize($Name)
}
