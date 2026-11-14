$ErrorActionPreference = 'Stop'
$ini = 'C:\Program Files\HWiNFO64\HWiNFO64.INI'
$marker = 'C:\Users\DangT\curseforge\Downloads\DeskMon\hwinfo-sm-result.txt'

try {
    if (-not (Test-Path $ini)) { throw "INI not found: $ini" }
    if (-not (Test-Path "$ini.bak")) { Copy-Item $ini "$ini.bak" -Force }

    $c = Get-Content $ini -Raw
    if ($c -notmatch 'SensorsSM=1') {
        if ($c -match '(?m)^\[Settings\]\s*$') {
            $c = $c -replace '(?m)^\[Settings\]\s*$', "[Settings]`r`nSensorsSM=1"
        } else {
            $c = $c.TrimEnd() + "`r`n[Settings]`r`nSensorsSM=1`r`n"
        }
        Set-Content $ini $c -NoNewline
    }

    Get-Process HWiNFO64,HWiNFO64Launcher -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Start-ScheduledTask -TaskName HWiNFO
    Start-Sleep -Seconds 12

    $p = Get-Process HWiNFO64 -ErrorAction SilentlyContinue
    "OK pid=$($p.Id -join ',')" | Set-Content $marker
}
catch {
    $_ | Out-String | Set-Content $marker
    exit 1
}
