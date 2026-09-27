param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'extension'
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$package = Join-Path $OutputDirectory ("UwPageCollector-{0}-{1}" -f $manifest.version, $stamp)
if (Test-Path -LiteralPath $package) { throw "Package already exists: $package" }
New-Item -ItemType Directory -Path $package -Force | Out-Null
Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $package
Compress-Archive -LiteralPath $package -DestinationPath ($package + '.zip')
Write-Output "Load unpacked folder: $package"
Write-Output "ZIP: $package.zip"
