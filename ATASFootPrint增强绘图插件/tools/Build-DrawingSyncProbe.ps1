# Compatibility entry point. New packages use the release name.
param([string]$ATASInstallDir = 'C:\Program Files (x86)\ATAS Platform')
& (Join-Path $PSScriptRoot 'Build-DrawingSync.ps1') -ATASInstallDir $ATASInstallDir
