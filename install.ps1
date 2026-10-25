# Installs AmbientRGB for the current user: %LOCALAPPDATA%\AmbientRGB, autostart at logon, then starts it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $env:LOCALAPPDATA 'AmbientRGB'
New-Item -ItemType Directory -Force $target | Out-Null

# Close the settings window first (it saves on close), then stop the tray app.
$running = Get-Process AmbientRGB -ErrorAction SilentlyContinue
if ($running) {
    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Milliseconds 800
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

Copy-Item (Join-Path $root 'bin\AmbientRGB.exe') $target -Force
Copy-Item (Join-Path $root 'bin\AmbientRGB-diag.exe') $target -Force
$exe = Join-Path $target 'AmbientRGB.exe'

Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AmbientRGB' -Value "`"$exe`""
Start-Process $exe
Write-Host "Installed to $target and started."
