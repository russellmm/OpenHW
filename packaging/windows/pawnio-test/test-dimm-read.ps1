# One-boot test of OpenHW's AMDI2C PawnIO module (DDR5 DIMMs on the FCH I2C controllers).
#
# Run ELEVATED, on a boot where Windows was started with "Disable driver signature enforcement" (Advanced
# startup → Troubleshoot → Advanced options → Startup Settings → Restart → 7) and with Memory Integrity turned off by
# the owner beforehand (Windows Security → Device security → Core isolation). Nothing here changes either setting.
#
#   .\test-dimm-read.ps1 install   installs OpenHW's test build of PawnIO (its own device \\.\OpenHWPawnIOTest,
#                                  beside the official PawnIO, which stays untouched)
#   .\test-dimm-read.ps1 read      ONE controller, ONE address: I2C1 0x50 registers 0x00 and 0x01 (an SPD5118 hub
#                                  reads 51 18). Read-only. Nothing else is touched.
#   .\test-dimm-read.ps1 remove    removes the test driver completely (device, driver package, service)
#
# Gentle by rule (owner, 2026-11-18): no sweeps of addresses, registers or controllers.
param([Parameter(Mandatory)][ValidateSet('install', 'read', 'remove')] [string]$Step)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$package = Join-Path $root 'dist\pawnio-test'
$devcon = "${env:ProgramFiles(x86)}\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
$helper = Join-Path $root 'src\OpenHW.Helper\bin\Debug\net8.0\openhw-helper.exe'

switch ($Step) {
    'install' {
        & $devcon install (Join-Path $package 'OpenHWPawnIOTest.inf') 'Root\OpenHWPawnIOTest'
        & $devcon status 'Root\OpenHWPawnIOTest'
        "Device present: $(Test-Path '\\.\OpenHWPawnIOTest')"
    }
    'read' {
        & $helper --i2c-read 1 0x50 0x00
    }
    'remove' {
        & $devcon remove 'Root\OpenHWPawnIOTest'
        $inf = (pnputil /enum-drivers | Out-String) -split "`r?`n`r?`n" | Where-Object { $_ -match 'openhwpawniotest\.inf' } |
            ForEach-Object { if ($_ -match 'Published Name\s*:\s*(\S+)') { $Matches[1] } }
        foreach ($i in $inf) { pnputil /delete-driver $i /uninstall /force }
        sc.exe delete OpenHWPawnIOTest 2>$null
        "Removed. The official PawnIO is unchanged."
    }
}
