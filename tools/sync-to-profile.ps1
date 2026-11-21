# Sync the local plugin (D:\code\computerconlex\plugin) into the DSH profile generation copy.
#
# Why a copy and not a junction/symlink: the desktop host validates that an enabled
# generation's package root is a REAL directory. A junction makes the projection fail
# with "Enabled generation package root is not a real directory" and drops DSH into
# safe mode. So: real directory, refreshed content.
#
# The copy keeps the generation's version string (0.8.7) on purpose — the generation id
# and generation.json pin that version, and reporting 0.9.0 here would make the host
# try to install 0.9.0 from the registry on the next reconcile.
#
# Usage:  powershell -NoProfile -File D:\code\computerconlex\tools\sync-to-profile.ps1
param(
  [string]$Source = 'D:\code\computerconlex\plugin',
  [string]$Generation = 'dsh-lens-computer-control+0.8.7+98223aa3d447'
)

$ErrorActionPreference = 'Stop'

$gen = Join-Path (Join-Path $env:DSH_HOME 'profiles\.generations\live') $Generation
$target = Join-Path $gen 'node_modules\dsh-lens-computer-control'

if (-not (Test-Path $gen)) { throw "generation not found: $gen" }
if (-not (Test-Path $target)) { throw "package dir not found: $target" }
$link = (Get-Item $target).LinkType
if ($link) { throw "package root is a $link, not a real directory - remove it first (the host rejects links)" }

robocopy $Source $target /MIR /NFL /NDL /NJH /NJS /NP /XD node_modules .git | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

# Keep the generation's version so the host does not try to fetch a version that
# is not published yet.
$pkgPath = Join-Path $target 'package.json'
$pkg = Get-Content $pkgPath -Raw -Encoding UTF8
$genVersion = (Get-Content (Join-Path $gen 'generation.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
$pkg = $pkg -replace '"version":\s*"[^"]+"', ('"version": "' + $genVersion + '"')
[System.IO.File]::WriteAllText($pkgPath, $pkg, (New-Object System.Text.UTF8Encoding($false)))

$check = Get-Content (Join-Path $target 'index.js') -Raw -Encoding UTF8
Write-Host ("synced -> " + $target)
Write-Host ("version  : " + $genVersion + "  (local source is " + ((Get-Content (Join-Path $Source 'package.json') -Raw -Encoding UTF8) -replace '\s+','' -replace '.*"version":"([^"]+)".*','$1') + ")")
Write-Host ("engine   : " + (Get-Item (Join-Path $target 'bin\lens.exe')).Length + " bytes")
Write-Host ("tools    : " + (([regex]::Matches($check, "name: 'computer_([a-z_]+)'") | ForEach-Object { $_.Groups[1].Value }) -join ', '))
