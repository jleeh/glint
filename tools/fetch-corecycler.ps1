<#
  Downloads the pinned CoreCycler release, verifies its SHA-256 and unpacks the parts Glint uses into
  src\Glint\third_party\CoreCycler (build / publish copy that folder to tools\CoreCycler next to Glint.exe).
  Only downloads and extracts files: it never runs CoreCycler or y-cruncher.
  Works in Windows PowerShell 5.1 (uses Windows' built-in tar.exe, bsdtar reads 7z).
  CoreCycler: CC BY-NC-SA 4.0, https://github.com/sp00n/CoreCycler ; y-cruncher: https://www.numberworld.org/y-cruncher/
  Usage: powershell -ExecutionPolicy Bypass -File tools\fetch-corecycler.ps1 [-Force]
#>
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '0.11.0.3'
$url     = "https://github.com/sp00n/CoreCycler/releases/download/v$version/CoreCycler-v$version.7z"
$sha256  = '721ed2948b51a2fc7c60289ecb7cbf62057b22d54c4ba5dc70ac5a107fcc1a63'
$dest    = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Glint\third_party\CoreCycler'
$stamp   = Join-Path $dest '.glint-version'

if (-not $Force -and (Test-Path -LiteralPath $stamp) -and ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq $version) -and
    (Test-Path -LiteralPath (Join-Path $dest 'script-corecycler.ps1'))) {
    Write-Host "CoreCycler $version already in $dest"
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('glint-cc-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $archive = Join-Path $work 'cc.7z'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $got = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $sha256) { throw "SHA-256 mismatch for CoreCycler $version (expected $sha256, got $got). Nothing was extracted." }
    Write-Host 'SHA-256 verified'

    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) { throw 'Windows tar.exe not found (needs Windows 10 1803 or newer).' }
    $x = Join-Path $work 'x'
    New-Item -ItemType Directory -Path $x | Out-Null
    # leave out the stress programs Glint doesn't use (Prime95, Linpack, AIDA64, y-cruncher 0.7.10) and y-cruncher's sample digits / formulas
    $excludes = @('*/test_programs/p95', '*/test_programs/linpack', '*/test_programs/aida64', '*/test_programs/y-cruncher-0.7.10',
                  '*/test_programs/y-cruncher/Custom Formulas', '*/test_programs/y-cruncher/Binaries/Digits')
    $targs = @('-xf', $archive, '-C', $x) + ($excludes | ForEach-Object { '--exclude'; $_ })
    # bsdtar warns about a few non-ASCII file names (in the digits folder we skip anyway) and exits non-zero: judge by the files instead
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into an error otherwise
    & $tar @targs 2>&1 | ForEach-Object { Write-Verbose "$_" }
    $ErrorActionPreference = 'Stop'
    $root = Get-ChildItem -LiteralPath $x -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'script-corecycler.ps1') } | Select-Object -First 1
    if (-not $root) { throw 'Unpacking failed: script-corecycler.ps1 not found in the archive.' }

    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest | Out-Null
    foreach ($n in 'script-corecycler.ps1', 'Run CoreCycler.bat', 'config.ini', 'configs', 'helpers', 'LICENSE', 'readme.txt', 'test_programs', 'tools') {
        $src = Join-Path $root.FullName $n
        if (-not (Test-Path -LiteralPath $src)) { throw "Expected '$n' is missing from the CoreCycler archive." }
        Copy-Item -LiteralPath $src -Destination $dest -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $dest 'logs') -Force | Out-Null
    foreach ($must in 'test_programs\y-cruncher\y-cruncher.exe', 'test_programs\y-cruncher\Binaries\24-ZN5 ~ Komari.exe',
                      'tools\ryzen-smu-cli\ryzen-smu-cli.exe', 'tools\APICID.exe', 'helpers\WriteConsoleToWriteFileWrapper.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $dest $must))) { throw "Expected '$must' is missing after extraction." }
    }
    Set-Content -LiteralPath $stamp -Value $version -Encoding ASCII
    $mb = [math]::Round((Get-ChildItem -LiteralPath $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "CoreCycler $version ready in $dest ($mb MB)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
