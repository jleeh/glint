# Glint installer. Run elevated (it self-elevates).
#   -DisableSignalRgb   stop SignalRGB and remove it from startup (does NOT uninstall it)
#   -Uninstall          remove the startup task and installed files (config is kept)
param([switch]$DisableSignalRgb, [switch]$Uninstall, [switch]$NoStart)

$ErrorActionPreference = 'Stop'
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($DisableSignalRgb) { $a += '-DisableSignalRgb' }
    if ($Uninstall) { $a += '-Uninstall' }
    if ($NoStart) { $a += '-NoStart' }
    # Wait for the elevated script only. (-Wait waits for the whole process tree, and dotnet's build
    # servers linger for minutes after publish, which made this look hung.)
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $a -PassThru
    $p.WaitForExit()
    if (Test-Path (Join-Path $PSScriptRoot 'install.log')) { Get-Content (Join-Path $PSScriptRoot 'install.log') | Select-String 'Glint started|registered|error|failed' | ForEach-Object { $_.Line } }
    exit $p.ExitCode
}

$log = Join-Path $PSScriptRoot 'install.log'
Start-Transcript -Path $log -Force | Out-Null
$dest = Join-Path $env:ProgramFiles 'Glint'
$task = 'Glint'

Get-Process Glint -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'Glint selftest' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Glint removed (config kept in %APPDATA%\Glint).'
    Stop-Transcript | Out-Null
    exit
}

# 1. PawnIO: signed ring0 driver used by LibreHardwareMonitor for CPU temps + motherboard fan headers.
$pawn = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -match 'PawnIO'
if (-not $pawn) {
    Write-Host 'Installing PawnIO driver...'
    winget install -e --id namazso.PawnIO --silent --accept-package-agreements --accept-source-agreements --scope machine
} else { Write-Host "PawnIO present: $($pawn.DisplayVersion)" }

# 2. Install files: copy the prebuilt (portable zip) folder if Glint.exe sits next to this script, else publish from source.
if (Test-Path (Join-Path $PSScriptRoot 'Glint.exe')) {
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-ChildItem $PSScriptRoot -Exclude 'install.log' | Copy-Item -Destination $dest -Recurse -Force
} else {
    $src = Join-Path $PSScriptRoot 'src\Glint\Glint.csproj'
    # dotnet sometimes finishes publishing but never exits; wait for the process OR a finished output, with a timeout.
    $started = Get-Date
    $pubArgs = @('publish', "`"$src`"", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true',
              '-o', "`"$dest`"", '--nologo', '-v', 'q', '--disable-build-servers', '-p:UseSharedCompilation=false', '-nodeReuse:false')
    $p = Start-Process dotnet -ArgumentList $pubArgs -NoNewWindow -PassThru
    $null = $p.Handle   # cache the handle now, or ExitCode reads as null later (PowerShell quirk)
    $marker = Join-Path $PSScriptRoot 'src\Glint\obj\Release\net10.0-windows\win-x64'
    $done = $false
    for ($i = 0; $i -lt 600 -and -not $done; $i++) {
        if ($p.HasExited) { $done = $true; break }
        $out = Get-ChildItem $marker -Filter 'PublishOutputs.*.txt' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -gt $started
        if ($out -and (Get-Item (Join-Path $dest 'Glint.dll') -ErrorAction SilentlyContinue).LastWriteTime -gt $started) {
            Start-Sleep 5   # give it a moment to exit by itself
            if (-not $p.HasExited) { Write-Host 'publish finished but dotnet did not exit; stopping it'; Stop-Process -Id $p.Id -Force }
            $done = $true
        }
        Start-Sleep 1
    }
    $ok = (Get-Item (Join-Path $dest 'Glint.dll') -ErrorAction SilentlyContinue).LastWriteTime -gt $started
    if ($p.HasExited -and $null -ne $p.ExitCode -and $p.ExitCode -ne 0) { $ok = $false }
    dotnet build-server shutdown | Out-Null
    if (-not $ok) { throw 'publish failed' }
}

# 3. Logon task, highest privileges, no UAC prompt each boot, restart on failure.
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute (Join-Path $dest 'Glint.exe') -WorkingDirectory $dest
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -Priority 4
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Startup task '$task' registered."

# Elevated selftest on demand (schtasks /run /tn "Glint selftest") without another UAC prompt.
Copy-Item (Join-Path $PSScriptRoot 'selftest.ps1') $dest -Force
$stAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$dest\selftest.ps1`"" -WorkingDirectory $dest
Register-ScheduledTask -TaskName 'Glint selftest' -Action $stAction -Principal $principal -Settings $settings -Force | Out-Null

# 4. Optional: hand over from SignalRGB.
if ($DisableSignalRgb) {
    Get-Process SignalRgb, SignalRgbLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run') {
        $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
        if ($p) { $p.PSObject.Properties | Where-Object { $_.Name -match '^SignalRgb' } | ForEach-Object { Remove-ItemProperty $k -Name $_.Name; Write-Host "Removed startup entry $($_.Name)" } }
    }
    Get-ScheduledTask | Where-Object TaskName -match 'SignalRgb' | Disable-ScheduledTask | Out-Null
    $svc = Get-Service SignalRgb* -ErrorAction SilentlyContinue
    if ($svc) { $svc | Stop-Service -Force -ErrorAction SilentlyContinue; $svc | Set-Service -StartupType Manual; Write-Host 'SignalRGB service stopped and set to Manual.' }
}

if (-not $NoStart) { Start-ScheduledTask -TaskName $task; Write-Host 'Glint started.' }
Stop-Transcript | Out-Null
