# Used by the Glint setup (glint.iss). Not meant to be run by hand; see install.ps1 for that.
#   -Action Tasks          register the "Glint" logon task and the "Glint selftest" task (same as install.ps1)
#   -Action Pawnio         download + silently install PawnIO from its official GitHub releases
#   -Action WebView2       download + silently install the WebView2 Evergreen runtime
#   -Action SignalRgb      stop SignalRGB and remove it from startup (same as install.ps1 -DisableSignalRgb)
# Exit code 0 = ok, 2 = could not download (offline), 1 = other failure.
param([Parameter(Mandatory)][string]$Action, [string]$Dir)
$ErrorActionPreference = 'Stop'
# everything this script does is logged next to the setup's own log (%TEMP%\glint-setup-helper.log)
Start-Transcript -Path (Join-Path $env:TEMP 'glint-setup-helper.log') -Append -Force | Out-Null
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# Downloads land in a fresh folder only Administrators/SYSTEM can write to (not the user's TEMP, which any
# non-elevated process could swap the file in), and must carry a valid Authenticode signature from the expected
# publisher before they are run elevated.
function New-SafeDir {
    $d = Join-Path $env:ProgramData ('GlintSetup-' + [guid]::NewGuid().ToString('N'))
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {   # Administrators, SYSTEM
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    Set-Acl -Path $d -AclObject $acl
    return $d
}
function Assert-Signed($file, $subjectPattern) {
    $sig = Get-AuthenticodeSignature $file
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch $subjectPattern) {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
        throw "refusing to run $(Split-Path $file -Leaf): signature $($sig.Status), signer '$($sig.SignerCertificate.Subject)'"
    }
}
function Get-File($url, $out) {
    try { Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 120 } catch { Write-Host "download failed: $_"; exit 2 }
}

try {
switch ($Action) {
    'Tasks' {
        $task = 'Glint'
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $taskAction = New-ScheduledTaskAction -Execute (Join-Path $Dir 'Glint.exe') -WorkingDirectory $Dir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -Priority 4
        Register-ScheduledTask -TaskName $task -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        $stAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Dir\selftest.ps1`"" -WorkingDirectory $Dir
        Register-ScheduledTask -TaskName 'Glint selftest' -Action $stAction -Principal $principal -Settings $settings -Force | Out-Null
    }
    'Pawnio' {
        $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest' -Headers @{ 'User-Agent' = 'GlintSetup' } -TimeoutSec 60
        $asset = $rel.assets | Where-Object name -like '*.exe' | Select-Object -First 1
        if (-not $asset) { Write-Host 'no PawnIO setup asset found'; exit 2 }
        if ($asset.browser_download_url -notmatch '^https://github\.com/namazso/PawnIO\.Setup/releases/download/') { Write-Host 'unexpected PawnIO download URL'; exit 1 }
        $dir = New-SafeDir
        $f = Join-Path $dir 'PawnIO_setup.exe'
        Get-File $asset.browser_download_url $f
        Assert-Signed $f '(^|, )CN=namazso\.eu(,|$)'
        # Silent switches per the official winget manifest (namazso.PawnIO): -install -silent; 3010 = reboot suggested.
        $p = Start-Process $f -ArgumentList '-install', '-silent' -Wait -PassThru
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { Write-Host "PawnIO setup exit code $($p.ExitCode)"; exit 1 }
    }
    'WebView2' {
        $dir = New-SafeDir
        $f = Join-Path $dir 'MicrosoftEdgeWebview2Setup.exe'
        Get-File 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' $f
        Assert-Signed $f '(^|, )CN=Microsoft Corporation(,|$)'
        $p = Start-Process $f -ArgumentList '/silent', '/install' -Wait -PassThru
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
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
} catch { Write-Host "error: $_"; Write-Host $_.ScriptStackTrace; exit 1 }
