param(
    [string]$SourceDir = 'C:\TWS API\source\CSharpClient\client'
)

$resolved = Resolve-Path -LiteralPath $SourceDir -ErrorAction Stop
$required = @('EClientSocket.cs', 'EWrapper.cs', 'Contract.cs')
$missing = foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolved.Path $file))) {
        $file
    }
}

if ($missing.Count -gt 0) {
    throw "IB API C# source is incomplete. Missing: $($missing -join ', ')"
}

$assemblyPath = Join-Path $resolved.Path 'bin\Release\net8.0\CSharpAPI.dll'
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "IB API net8.0 runtime is missing: $assemblyPath"
}

$assemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).FileVersion
if ($assemblyVersion -notlike '10.45.*') {
    throw "Expected IB API 10.45, found $assemblyVersion at $assemblyPath"
}

Write-Output "IB API C# source ready: $($resolved.Path) ($assemblyVersion)"
