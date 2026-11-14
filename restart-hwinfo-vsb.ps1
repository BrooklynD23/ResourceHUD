<#
.SYNOPSIS
    Restarts HWiNFO64 so it loads shared-memory and Gadget/VSB sensor reporting.

.DESCRIPTION
    DeskMon reads HWiNFO through two feeds:
      * Global\HWiNFO_SENS_SM2  (all sensors, free build stops after ~12 h)
      * HKCU/HKLM\Software\HWiNFO64\VSB (Gadget feed, no Pro time limit)

    HWiNFO only refreshes those feeds after it restarts. This script enables both,
    marks plausible VSB readings, restarts HWiNFO elevated, then runs DeskMon's
    --dump-hwinfo diagnostic so the result lands in DeskMon-HwInfo-VSB.txt.

    Run elevated (right-click -> Run with PowerShell as administrator).
#>
[CmdletBinding()]
param()

$log = 'C:\Users\DangT\curseforge\Downloads\DeskMon-hwinfo-restart.log'
try { Start-Transcript -Path $log -Force | Out-Null } catch { }
$ErrorActionPreference = 'Continue'

function Step([string]$m) { Write-Host "== $m" }

$hwinfoRoot = 'HKCU:\Software\HWiNFO64'
$sensors    = Join-Path $hwinfoRoot 'Sensors'

Step 'enable shared memory + gadget registry'
try {
    New-Item -Path $hwinfoRoot -Force | Out-Null
    Set-ItemProperty -Path $hwinfoRoot -Name SensorsSM -Value 1      -Type DWord
    Set-ItemProperty -Path $hwinfoRoot -Name SMMemSize -Value 262144 -Type DWord
    Set-ItemProperty -Path $sensors    -Name EnableVSB  -Value 1     -Type DWord
    New-Item -Path (Join-Path $hwinfoRoot 'VSB') -Force | Out-Null
} catch { Write-Warning "registry setup failed: $($_.Exception.Message)" }

Step 'patch HWiNFO64.INI SensorsSM'
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
        Step 'INI ok'
    } else { Write-Warning "INI not found: $ini" }
} catch { Write-Warning "INI patch failed: $($_.Exception.Message)" }

# Best-effort VSB marking. HWiNFO only creates registry subkeys for readings the
# user has touched, so cover Temp0..Temp31 plus every existing non-voltage reading
# under the groups already present. Exact IDs are refined after the shared-memory
# dump shows the real sensor map.
Step 'mark VSB readings'
$groups = @('E0003000_0', 'F0000300_0', 'F0000301_0', 'F0000400_0', 'F000BA00_0')
$idx = 0

function Set-Vsb([string]$Path) {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    Set-ItemProperty -Path $Path -Name InVSB  -Value 1 -Type DWord
    Set-ItemProperty -Path $Path -Name VSBidx -Value $script:idx -Type DWord
    $script:idx++
}

foreach ($g in $groups) {
    $gpath = Join-Path $sensors $g
    if (-not (Test-Path $gpath)) { continue }
    foreach ($i in 0..31) { Set-Vsb (Join-Path $gpath "Temp$i") }
    Get-ChildItem $gpath -ErrorAction SilentlyContinue | ForEach-Object {
        $name = $_.PSChildName
        if ($name -like 'Volt*') { return }
        $enabled = (Get-ItemProperty -Path $_.PSPath -Name Enabled -ErrorAction SilentlyContinue).Enabled
        if ($null -ne $enabled -and $enabled -eq 0) { return }
        Set-Vsb $_.PSPath
    }
}
Step "marked $idx VSB readings"

Step 'restart HWiNFO64'
Get-Process HWiNFO64, HWiNFO64Launcher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath 'C:\Program Files\HWiNFO64\HWiNFO64Launcher.exe' `
    -WorkingDirectory 'C:\Program Files\HWiNFO64'
Start-Sleep -Seconds 10
Step 'HWiNFO restarted'

# Keep the free shared-memory feed alive: HWiNFO stops publishing after ~12 h, so
# bounce it every 11 h via a highest-privilege scheduled task.
Step 'install keep-alive task'
try {
    $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $keep = 'C:\Users\DangT\curseforge\Downloads\DeskMon\keep-hwinfo-shared-memory.ps1'
    $action = New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$keep`""
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) `
        -RepetitionInterval (New-TimeSpan -Hours 11) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId $user -RunLevel Highest -LogonType Interactive
    Register-ScheduledTask -TaskName 'DeskMon-HWiNFO-Sensors' -Action $action `
        -Trigger $trigger -Principal $principal -Force | Out-Null
    Step 'keep-alive task registered'
} catch { Write-Warning "scheduled task failed: $($_.Exception.Message)" }

Step 'run DeskMon --dump-hwinfo'
$out = 'C:\Users\DangT\curseforge\Downloads\DeskMon-HwInfo-VSB.txt'
$dotnet = 'C:\Users\DangT\AppData\Local\Programs\dotnet\dotnet.exe'
$dll    = 'C:\Users\DangT\curseforge\Downloads\DeskMon\publish\DeskMon.dll'
try {
    if ((Test-Path $dotnet) -and (Test-Path $dll)) {
        if (Test-Path $out) { Remove-Item $out -Force }
        & $dotnet $dll --dump-hwinfo $out | Out-Null
    } else { Write-Warning "dotnet or dll missing" }
} catch { Write-Warning "dump failed: $($_.Exception.Message)" }

if (Test-Path 'HKCU:\Software\HWiNFO64\VSB') {
    $count = (Get-Item 'HKCU:\Software\HWiNFO64\VSB').ValueCount
    "VSB key present, value count: $count" | Add-Content -Path $out -Encoding UTF8 -ErrorAction SilentlyContinue
} else {
    'HKCU VSB missing' | Add-Content -Path $out -Encoding UTF8 -ErrorAction SilentlyContinue
}

try { Stop-Transcript | Out-Null } catch { }
