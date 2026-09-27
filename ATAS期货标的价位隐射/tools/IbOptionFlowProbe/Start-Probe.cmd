@echo off
setlocal
rem RemoteSigned applies to this child process only, not the machine/user policy.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Start-Probe.ps1"
if errorlevel 1 (
  echo Launch failed. Keep this message for troubleshooting.
  pause
)
endlocal
