param(
    [string]$ATASInstallDir = 'C:\Program Files (x86)\ATAS Platform'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$previousAtasDir = $env:ATAS_INSTALL_DIR
$previousPreview = $env:FOOTPRINT_PREVIEW_PATH
Push-Location $repoRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $ATASInstallDir 'ATAS.Indicators.dll'))) {
        throw "ATAS.Indicators.dll not found under $ATASInstallDir"
    }
    $dist = Join-Path $repoRoot 'dist'
    [xml]$project = Get-Content -LiteralPath '.\src\FootprintOutline\FootprintOutline.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Project version is missing.' }
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    & dotnet build .\FootprintOutline.slnx -c Release --nologo ("-p:ATASInstallDir=" + $ATASInstallDir)
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet run --project .\tests\FootprintOutline.Tests -c Release --no-build |
        Tee-Object -FilePath (Join-Path $dist 'TEST-RESULTS.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Geometry tests failed.' }
    $env:ATAS_INSTALL_DIR = $ATASInstallDir
    $env:FOOTPRINT_PREVIEW_PATH = Join-Path $repoRoot 'docs\images\host-render-preview.png'
    & dotnet run --project .\tests\FootprintOutline.HostTests -c Release --no-build |
        Tee-Object -FilePath (Join-Path $dist 'TEST-RESULTS.txt') -Append
    if ($LASTEXITCODE -ne 0) { throw 'Host integration tests failed.' }
    Copy-Item -LiteralPath '.\src\FootprintOutline\bin\Release\FootprintOutline.dll' -Destination $dist -Force
    Copy-Item -LiteralPath '.\README.md' -Destination $dist -Force
    $distDocs = Join-Path $dist 'docs'
    New-Item -ItemType Directory -Path (Join-Path $distDocs 'images') -Force | Out-Null
    Copy-Item -LiteralPath '.\docs\VALIDATION.md' -Destination $distDocs -Force
    Copy-Item -LiteralPath '.\docs\images\host-render-preview.png' -Destination (Join-Path $distDocs 'images') -Force
    $hash = (Get-FileHash -LiteralPath (Join-Path $dist 'FootprintOutline.dll') -Algorithm SHA256).Hash
    "$hash  FootprintOutline.dll" | Set-Content -LiteralPath (Join-Path $dist 'FootprintOutline.dll.sha256') -Encoding ascii
    $archive = Join-Path $dist ("FootprintOutline-v$version.zip")
    $items = @('FootprintOutline.dll','FootprintOutline.dll.sha256','README.md','TEST-RESULTS.txt','docs') |
        ForEach-Object { Join-Path $dist $_ }
    Compress-Archive -Path $items -DestinationPath $archive -Force
    Write-Output "Release: $archive"
    Write-Output "DLL SHA256: $hash"
}
finally {
    $env:ATAS_INSTALL_DIR = $previousAtasDir
    $env:FOOTPRINT_PREVIEW_PATH = $previousPreview
    Pop-Location
}

