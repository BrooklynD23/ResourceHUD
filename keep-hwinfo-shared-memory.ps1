<#
.SYNOPSIS
    Restarts HWiNFO64 so its free-build shared-memory feed stays under the 12-hour limit.

.DESCRIPTION
    HWiNFO's free Shared Memory Support stops publishing after ~12 hours of runtime.
    DeskMon reads Global\HWiNFO_SENS_SM2 for CPU/GPU temperatures, so this script is
    run by the "DeskMon-HWiNFO-Sensors" scheduled task every 11 hours to bounce
    HWiNFO before the feed expires. It only re-enables SensorsSM and relaunches
    HWiNFO; it does not touch Gadget/VSB settings.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$log = 'C:\Users\DangT\curseforge\Downloads\DeskMon-hwinfo-keepalive.log'
try { Start-Transcript -Path $log -Force | Out-Null } catch { }

try {
    Set-ItemProperty -Path 'HKCU:\Software\HWiNFO64' -Name SensorsSM  -Value 1      -Type DWord
    Set-ItemProperty -Path 'HKCU:\Software\HWiNFO64' -Name SMMemSize  -Value 262144 -Type DWord
} catch { Write-Warning "registry update failed: $($_.Exception.Message)" }

$ini = 'C:\Program Files\HWiNFO64\HWiNFO64.INI'
try {
    if (Test-Path $ini) {
        $text = Get-Content $ini -Raw -ErrorAction SilentlyContinue
        if ($text -and $text -notmatch '(?im)^\s*SensorsSM\s*=') {
            if ($text -match '(?im)^\[Settings\]\s*$') {
                $text = $text -replace '(?im)^\[Settings\]\s*$', "[Settings]`r`nSensorsSM=1"
            } else {
                $text = "[Settings]`r`nSensorsSM=1`r`n" + $text
            }
            Set-Content -Path $ini -Value $text -Encoding ASCII -Force
        }
    }
} catch { Write-Warning "INI patch failed: $($_.Exception.Message)" }

Get-Process HWiNFO64, HWiNFO64Launcher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath 'C:\Program Files\HWiNFO64\HWiNFO64Launcher.exe' `
    -WorkingDirectory 'C:\Program Files\HWiNFO64'

try { Stop-Transcript | Out-Null } catch { }
