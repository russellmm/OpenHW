# Installs (or updates) OpenHW for Windows from dist\win-x64, elevated: stops the OpenHW Helper service (it gives
# every fan it holds back to the BMC as it stops) and any running OpenHW, copies the files to Program Files\OpenHW,
# then registers and starts the service (openhw-helper --install). Phase-4 work replaces this with an installer.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$from = Join-Path $root 'dist\win-x64'
$to = Join-Path $env:ProgramFiles 'OpenHW'
if (-not (Test-Path (Join-Path $from 'openhw-helper.exe'))) { throw "Build first: packaging\windows\build.ps1" }

if (Get-Service OpenHWHelper -ErrorAction SilentlyContinue) { Stop-Service OpenHWHelper -ErrorAction SilentlyContinue }
Get-Process openhw -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $to | Out-Null
Copy-Item (Join-Path $from '*') $to -Recurse -Force
& (Join-Path $to 'openhw-helper.exe') --install
"Installed to $to"
