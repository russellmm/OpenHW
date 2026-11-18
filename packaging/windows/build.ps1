# Builds OpenHW for Windows: the app (openhw.exe) and the helper service (openhw-helper.exe), self-contained win-x64
# (no .NET install needed), into dist\win-x64. Then install with install.ps1 (elevated).
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $root 'dist\win-x64'
# An OpenHW started from here holds its files open: close it (install.ps1 starts the installed copy).
Get-Process openhw -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'src\OpenHW.App\OpenHW.App.csproj', 'src\OpenHW.Helper\OpenHW.Helper.csproj') {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $out -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}
"Built into $out"
