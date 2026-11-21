# Build the Lens engines with the in-box csc.exe (zero third-party deps).
#
#   lens.exe       main engine (capture / OCR / UIA / vision / check). Needs the WinRT
#                  projection: Windows SDK union Windows.winmd + .NET Framework reference
#                  assemblies (UIAutomation dlls + Facades).
#   lenshud.exe    on-screen HUD (GDI drawing, no WPF/WinForms dependencies).
#
# Usage:  powershell -NoProfile -File D:\code\computerconlex\tools\build-lens.ps1
param(
  [string]$LensDir = 'D:\code\computerconlex\src',
  [string]$OutDir  = 'D:\code\computerconlex\bin'
)

$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found' }
$fx = Split-Path $csc -Parent

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# ---------------------------------------------------------------- lens.exe / lenshud.exe
# WinRT references: union Windows.winmd + the .NET Framework reference assemblies
# (they carry the reference UIAutomation dlls and the Facades used by the projection).
$refRoot = Get-ChildItem 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework' -Directory -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match '^v4\.' } | Sort-Object Name -Descending | Select-Object -First 1

$refs = New-Object System.Collections.Generic.List[string]
foreach ($p in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll',
                 'System.Windows.Forms.dll', 'WindowsBase.dll', 'System.Runtime.WindowsRuntime.dll',
                 'UIAutomationClient.dll', 'UIAutomationTypes.dll')) {
  $candidates = @()
  if ($refRoot) { $candidates += (Join-Path $refRoot.FullName $p) }
  $candidates += (Join-Path $fx $p)
  $candidates += (Join-Path $fx ('WPF\' + $p))
  foreach ($c in $candidates) { if (Test-Path $c) { $refs.Add($c); break } }
}
if ($refRoot) {
  foreach ($p in @('System.Runtime.dll')) {
    $f = Join-Path (Join-Path $refRoot.FullName 'Facades') $p
    if (Test-Path $f) { $refs.Add($f) }
  }
}

$winmd = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata' -Recurse -Filter 'Windows.winmd' -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\Facade\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if ($winmd) { $refs.Add($winmd.FullName) } else { Write-Warning 'Windows.winmd not found: lens.exe cannot be rebuilt here' }

$all = Get-ChildItem $LensDir -Filter *.cs | ForEach-Object { $_.FullName }
$det = Get-ChildItem (Join-Path $LensDir 'Det') -Filter *.cs | ForEach-Object { $_.FullName }
$mainSrc = @($all | Where-Object { $_ -notmatch 'HudApp\.cs$' -and $_ -notmatch 'PromoGen\.cs$' })
$mainSrc += $det

$refArgs = @($refs | ForEach-Object { '/r:' + $_ })
$common = @('/nologo', '/noconfig', '/nostdlib+', '/unsafe+', '/platform:anycpu', '/optimize+', '/codepage:65001') + $refArgs

$ok = $true
& $csc (@($common + @('/target:exe', ('/out:' + (Join-Path $OutDir 'lens.exe')))) + $mainSrc)
if ($LASTEXITCODE -ne 0) { Write-Warning "lens.exe build failed (exit $LASTEXITCODE) - keep the existing binary"; $ok = $false }

$hudSrc = @('HudApp.cs', 'HudCtl.cs', 'Win32.cs', 'Model.cs') | ForEach-Object { Join-Path $LensDir $_ } |
  Where-Object { Test-Path $_ }
& $csc (@($common + @('/target:winexe', ('/out:' + (Join-Path $OutDir 'lenshud.exe')))) + @($hudSrc))
if ($LASTEXITCODE -ne 0) { Write-Warning "lenshud.exe build failed (exit $LASTEXITCODE) - keep the existing binary"; $ok = $false }

Get-ChildItem $OutDir -Filter *.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
if (-not $ok) { Write-Warning 'engine binaries were NOT refreshed' }
