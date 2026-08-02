<#
.SYNOPSIS
    Starts DeskMon at logon via a Startup-folder shortcut.

.DESCRIPTION
    DeskMon runs as a normal user -- no elevation, no UAC prompt, no kernel driver,
    no scheduled task. A plain shortcut in the per-user Startup folder is all it needs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-startup.ps1
    powershell -ExecutionPolicy Bypass -File .\install-startup.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

$startup      = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'DeskMon.lnk'

# --- uninstall ----------------------------------------------------------------
if ($Uninstall) {
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
        Write-Host "Removed $shortcutPath" -ForegroundColor Green
    }
    else {
        Write-Host 'No DeskMon startup shortcut was present.' -ForegroundColor Yellow
    }

    # Clean up the scheduled task from earlier builds, if one was ever registered.
    $task = Get-ScheduledTask -TaskName 'DeskMon' -ErrorAction SilentlyContinue
    if ($task) {
        Write-Host 'Found a legacy elevated scheduled task; removing it needs admin.' -ForegroundColor Yellow
        Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
            '-NoProfile', '-Command',
            'Unregister-ScheduledTask -TaskName DeskMon -Confirm:$false'
        )
        Write-Host 'Legacy scheduled task removed.' -ForegroundColor Green
    }

    Get-Process DeskMon -ErrorAction SilentlyContinue | Stop-Process -Force
    return
}

# --- locate the exe -----------------------------------------------------------
# Works both in the shipped folder (exe alongside this script) and in the source
# tree (exe under publish\).
if (-not $ExePath) {
    foreach ($candidate in @(
        (Join-Path $PSScriptRoot 'DeskMon.exe'),            # shipped folder
        (Join-Path $PSScriptRoot 'ship\DeskMon.exe'),       # self-contained build
        (Join-Path $PSScriptRoot 'publish\DeskMon.exe')     # framework-dependent build
    )) {
        if (Test-Path $candidate) { $ExePath = $candidate; break }
    }
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "DeskMon.exe not found next to this script, or under .\ship\ or .\publish\.`n" +
          "Build it first with either:`n" +
          "    dotnet publish DeskMon.csproj -c Release -r win-x64 --self-contained false -o publish`n" +
          '    dotnet publish DeskMon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o ship'
}
$ExePath = (Resolve-Path $ExePath).Path

# --- create the shortcut ------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcutPath)
$lnk.TargetPath       = $ExePath
$lnk.WorkingDirectory = Split-Path $ExePath
$lnk.Description      = 'Small desktop CPU/GPU usage and temperature widget'
$lnk.WindowStyle      = 7          # minimised; the app has no main window anyway
$lnk.Save()
[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

Write-Host "Created $shortcutPath" -ForegroundColor Green
Write-Host "  -> $ExePath"
Write-Host 'DeskMon will start automatically at logon (no UAC prompt).' -ForegroundColor Green

if (-not (Get-Process DeskMon -ErrorAction SilentlyContinue)) {
    Start-Process $ExePath
    Write-Host 'Started it now as well.' -ForegroundColor Green
}
else {
    Write-Host 'DeskMon is already running.' -ForegroundColor Yellow
}
