$result = 'C:\Users\DangT\curseforge\Downloads\DeskMon-elevated-launch.txt'
try {
    Start-Process -FilePath 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
        -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','C:\Users\DangT\curseforge\Downloads\DeskMon\restart-hwinfo-vsb.ps1' `
        -Verb RunAs -ErrorAction Stop
    "launched $(Get-Date -Format o)" | Set-Content $result
} catch {
    "failed: $($_.Exception.Message) $(Get-Date -Format o)" | Set-Content $result
}
