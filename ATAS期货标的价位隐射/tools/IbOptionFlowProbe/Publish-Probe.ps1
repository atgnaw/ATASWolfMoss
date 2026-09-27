$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'IbOptionFlowProbe.csproj'
$output = Join-Path $PSScriptRoot 'bin/publish/win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Write-Host "Windows 独立运行包：$output"
