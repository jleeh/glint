# Used by the Glint setup (glint.iss). Not meant to be run by hand; see install.ps1 for that.
#   -Action Tasks          register the "Glint" logon task and the "Glint selftest" task (same as install.ps1)
#   -Action Pawnio         download + silently install PawnIO from its official GitHub releases
#   -Action WebView2       download + silently install the WebView2 Evergreen runtime
#   -Action SignalRgb      stop SignalRGB and remove it from startup (same as install.ps1 -DisableSignalRgb)
# Exit code 0 = ok, 2 = could not download (offline), 1 = other failure.
param([Parameter(Mandatory)][string]$Action, [string]$Dir)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

function Get-File($url, $out) {
    try { Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 120 } catch { Write-Host "download failed: $_"; exit 2 }
}

try {
switch ($Action) {
    'Tasks' {
        $task = 'Glint'
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $action = New-ScheduledTaskAction -Execute (Join-Path $Dir 'Glint.exe') -WorkingDirectory $Dir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -Priority 4
        Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        $stAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Dir\selftest.ps1`"" -WorkingDirectory $Dir
        Register-ScheduledTask -TaskName 'Glint selftest' -Action $stAction -Principal $principal -Settings $settings -Force | Out-Null
    }
    'Pawnio' {
        $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest' -Headers @{ 'User-Agent' = 'GlintSetup' } -TimeoutSec 60
        $asset = $rel.assets | Where-Object name -like '*.exe' | Select-Object -First 1
        if (-not $asset) { Write-Host 'no PawnIO setup asset found'; exit 2 }
        $f = Join-Path $env:TEMP 'PawnIO_setup.exe'
        Get-File $asset.browser_download_url $f
        # Silent switches per the official winget manifest (namazso.PawnIO): -install -silent; 3010 = reboot suggested.
        $p = Start-Process $f -ArgumentList '-install', '-silent' -Wait -PassThru
        Remove-Item $f -Force -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { Write-Host "PawnIO setup exit code $($p.ExitCode)"; exit 1 }
    }
    'WebView2' {
        $f = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
        Get-File 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' $f
        $p = Start-Process $f -ArgumentList '/silent', '/install' -Wait -PassThru
        Remove-Item $f -Force -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0) { Write-Host "WebView2 setup exit code $($p.ExitCode)"; exit 1 }
    }
    'SignalRgb' {
        Get-Process SignalRgb, SignalRgbLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
        foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run') {
            $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
            if ($p) { $p.PSObject.Properties | Where-Object { $_.Name -match '^SignalRgb' } | ForEach-Object { Remove-ItemProperty $k -Name $_.Name; Write-Host "Removed startup entry $($_.Name)" } }
        }
        Get-ScheduledTask | Where-Object TaskName -match 'SignalRgb' | Disable-ScheduledTask | Out-Null
        $svc = Get-Service SignalRgb* -ErrorAction SilentlyContinue
        if ($svc) { $svc | Stop-Service -Force -ErrorAction SilentlyContinue; $svc | Set-Service -StartupType Manual; Write-Host 'SignalRGB service stopped and set to Manual.' }
    }
    default { Write-Host "unknown action $Action"; exit 1 }
}
} catch { Write-Host "error: $_"; exit 1 }
