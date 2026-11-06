# Runs the elevated hardware selftest (via the "Glint selftest" task), then restarts Glint.
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $env:APPDATA 'Glint\selftest.json'
Get-Process Glint -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& (Join-Path $dir 'Glint.exe') selftest $out --hw | Out-Null
Start-ScheduledTask -TaskName 'Glint'
