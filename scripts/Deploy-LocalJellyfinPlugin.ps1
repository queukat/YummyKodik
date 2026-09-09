[CmdletBinding()]
param()

$serviceName = 'Jellyfin'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..') -ErrorAction Stop).Path
$stagingDirectory = (Resolve-Path -LiteralPath (
    Join-Path $projectRoot 'publish\YummyKodik_1.0.0.0') -ErrorAction Stop).Path
$pluginsRoot = 'C:\ProgramData\Jellyfin\Server\plugins'
$pluginDirectory = Get-ChildItem -LiteralPath $pluginsRoot -Directory -ErrorAction Stop |
    Where-Object { $_.Name.StartsWith('YummyKodik_', [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        $parsedVersion = $null
        if ([Version]::TryParse($_.Name.Substring('YummyKodik_'.Length), [ref]$parsedVersion)) {
            [pscustomobject]@{
                Directory = $_
                Version = $parsedVersion
            }
        }
    } |
    Sort-Object Version -Descending |
    Select-Object -First 1 -ExpandProperty Directory

if ($null -eq $pluginDirectory) {
    throw "No installed YummyKodik_<version> plugin directory found under $pluginsRoot"
}

$pluginDirectory = $pluginDirectory.FullName
$runtimeNames = @(
    'YummyKodik.dll'
    'YummyKodik.deps.json'
    'YummyKodik.pdb'
    'HtmlAgilityPack.dll'
)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $quotedScriptPath = '"{0}"' -f $PSCommandPath
    $elevatedProcess = Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -ArgumentList @(
            '-NoProfile'
            '-ExecutionPolicy'
            'Bypass'
            '-File'
            $quotedScriptPath
        ) `
        -WindowStyle Hidden `
        -Wait `
        -PassThru `
        -ErrorAction Stop

    exit $elevatedProcess.ExitCode
}

$sourcePaths = foreach ($name in $runtimeNames) {
    $sourcePath = Join-Path $stagingDirectory $name
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Missing staging runtime file: $sourcePath"
    }

    $sourcePath
}

Write-Host "Staging: $stagingDirectory"
Write-Host "Destination: $pluginDirectory"
Write-Host "Stopping service: $serviceName"

$deploymentError = $null
try {
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -ErrorAction Stop
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        $service.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }

    Copy-Item `
        -LiteralPath $sourcePaths `
        -Destination $pluginDirectory `
        -Force `
        -ErrorAction Stop

    foreach ($name in $runtimeNames) {
        $sourcePath = Join-Path $stagingDirectory $name
        $destinationPath = Join-Path $pluginDirectory $name
        $sourceHash = (Get-FileHash `
            -LiteralPath $sourcePath `
            -Algorithm SHA256 `
            -ErrorAction Stop).Hash
        $destinationHash = (Get-FileHash `
            -LiteralPath $destinationPath `
            -Algorithm SHA256 `
            -ErrorAction Stop).Hash

        if ($sourceHash -ne $destinationHash) {
            throw "Deployment hash mismatch for $name"
        }

        Write-Host "Verified $name $destinationHash"
    }
}
catch {
    $deploymentError = $_
}

$restartError = $null
try {
    Write-Host "Starting service: $serviceName"
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name $serviceName -ErrorAction Stop
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        $service.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(30))
    }
}
catch {
    $restartError = $_
}

if ($deploymentError -ne $null) {
    Write-Error $deploymentError
    exit 1
}

if ($restartError -ne $null) {
    Write-Error $restartError
    exit 2
}

Write-Host 'Local Jellyfin plugin replacement completed.'
exit 0
