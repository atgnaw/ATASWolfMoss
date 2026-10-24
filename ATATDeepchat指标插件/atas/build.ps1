[CmdletBinding()]
param(
    [string]$ATASInstallDir = 'C:\Program Files (x86)\ATAS Platform',
    [ValidateSet('DeepMEffort', 'DeepVTracker', 'All')]
    [string]$Indicator = 'DeepMEffort',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskEnvNames = @('DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'MSBuildEnableWorkloadResolver', 'ATAS_INSTALL_DIR')
$taskOldEnvironment = @{}
foreach ($taskName in $taskEnvNames) { $taskOldEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process') }

try {
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:ATAS_INSTALL_DIR = (Resolve-Path -LiteralPath $ATASInstallDir).Path
    $taskIndicators = if ($Indicator -eq 'All') { @('DeepMEffort', 'DeepVTracker') } else { @($Indicator) }
    foreach ($taskIndicator in $taskIndicators) {
    $taskProject = Join-Path $PSScriptRoot "$taskIndicator\$taskIndicator.csproj"
    $taskTests = Join-Path $PSScriptRoot "tests\$taskIndicator.Tests\$taskIndicator.Tests.csproj"
    if ($SkipTests) {
        & dotnet build $taskProject -c Release "-p:ATASInstallDir=$ATASInstallDir"
        if ($LASTEXITCODE -ne 0) { throw "$taskIndicator build failed." }
    }
    else {
        & dotnet build $taskTests -c Release "-p:ATASInstallDir=$ATASInstallDir"
        if ($LASTEXITCODE -ne 0) { throw "$taskIndicator test build failed." }
        & dotnet (Join-Path $PSScriptRoot "tests\$taskIndicator.Tests\bin\Release\net10.0-windows\$taskIndicator.Tests.dll")
        if ($LASTEXITCODE -ne 0) { throw "$taskIndicator validation failed; package was not updated." }
    }

    $taskOutput = Join-Path $taskRoot "artifacts\$taskIndicator"
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    $taskBin = Join-Path $PSScriptRoot "$taskIndicator\bin\Release\net10.0-windows"
    $taskAssembly = "WolfMoss.$taskIndicator"
    Copy-Item -LiteralPath (Join-Path $taskBin "$taskAssembly.dll") -Destination $taskOutput -Force
    Copy-Item -LiteralPath (Join-Path $taskBin "$taskAssembly.pdb") -Destination $taskOutput -Force
    $taskReadme = if ($taskIndicator -eq 'DeepVTracker') { 'DeepVTracker\README.md' } else { 'README.md' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskReadme) -Destination $taskOutput -Force
    $taskHash = Get-FileHash -LiteralPath (Join-Path $taskOutput "$taskAssembly.dll") -Algorithm SHA256
    "$($taskHash.Hash)  $taskAssembly.dll" | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding utf8
    $taskArchive = Join-Path $taskRoot "artifacts\$taskIndicator-ATAS-1.0.0.zip"
    Compress-Archive -LiteralPath @(
        (Join-Path $taskOutput "$taskAssembly.dll"),
        (Join-Path $taskOutput "$taskAssembly.pdb"),
        (Join-Path $taskOutput 'README.md'),
        (Join-Path $taskOutput 'SHA256SUMS.txt')
    ) -DestinationPath $taskArchive -Force
    Write-Host "Package: $taskArchive"
    Write-Host "Indicator: $(Join-Path $taskOutput "$taskAssembly.dll")"
    }
}
finally {
    foreach ($taskName in $taskEnvNames) { [Environment]::SetEnvironmentVariable($taskName, $taskOldEnvironment[$taskName], 'Process') }
}
