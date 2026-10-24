param([string]$ATASInstallDir = 'C:\Program Files (x86)\ATAS Platform')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$previousAtasDir = $env:ATAS_INSTALL_DIR
Push-Location $repoRoot
try {
    $package = Join-Path $repoRoot 'dist\DrawingSync'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    & dotnet build .\DrawingSync.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false ("-p:ATASInstallDir=" + $ATASInstallDir)
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $env:ATAS_INSTALL_DIR = $ATASInstallDir
    & dotnet run --project .\tests\DrawingSyncProbe.Tests -c Release --no-build |
        Tee-Object -FilePath (Join-Path $package 'TEST-RESULTS.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Drawing sync tests failed.' }
    $dll = Join-Path $package 'WolfMoss.DrawingSyncProbe.dll'
    Copy-Item -LiteralPath '.\src\DrawingSyncProbe\bin\Release\net10.0-windows\WolfMoss.DrawingSyncProbe.dll' -Destination $dll -Force
    Copy-Item -LiteralPath '.\docs\DRAWING-SYNC.md' -Destination (Join-Path $package 'README.md') -Force
    Copy-Item -LiteralPath '.\docs\DRAWING-SYNC-ARCHITECTURE.md' -Destination (Join-Path $package 'ARCHITECTURE.md') -Force
    $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    "$hash  WolfMoss.DrawingSyncProbe.dll" | Set-Content -LiteralPath (Join-Path $package 'SHA256.txt') -Encoding ascii
    $files = @('WolfMoss.DrawingSyncProbe.dll', 'README.md', 'ARCHITECTURE.md', 'TEST-RESULTS.txt', 'SHA256.txt') | ForEach-Object { Join-Path $package $_ }
    [xml]$project = Get-Content -LiteralPath '.\src\DrawingSyncProbe\DrawingSyncProbe.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    $archive = Join-Path $package ("DrawingSync-v$version.zip")
    Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
    Write-Output "Package: $archive"
    Write-Output "SHA256: $hash"
}
finally {
    $env:ATAS_INSTALL_DIR = $previousAtasDir
    Pop-Location
}
