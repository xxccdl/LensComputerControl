# Make the Lens plugin's config editable in Settings → Lens 电脑控制.
#
# Why: the Host serves a settings namespace only for plugin rows it sees in the
# PROFILE patch document. Lens' row is inserted by the package's own
# cordis.patch.yml (the bundle layer), which is not part of that document, so the
# namespace never appears and the panel stays empty. Adding a merge row here pins
# the config into the profile patch — exactly how dsh-computer-use-win does it.
#
# Idempotent: rerunning it does nothing once the row exists.
#
# Usage:  powershell -NoProfile -File D:\code\computerconlex\tools\ensure-profile-settings-row.ps1
param(
  [string]$Profile = 'web',
  [int]$TimeoutMs = 25000
)

$ErrorActionPreference = 'Stop'
$patch = Join-Path $env:DSH_HOME "profiles\$Profile\cordis.patch.yml"
if (-not (Test-Path $patch)) { throw "profile patch not found: $patch" }

$text = [System.IO.File]::ReadAllText($patch)
if ($text -match '(?m)^-\s+id:\s*lens-computer-control\s*$') {
  Write-Host 'row already present - nothing to do'
  exit 0
}

$row = @"
- id: lens-computer-control
  name: dsh-lens-computer-control
  config:
    timeoutMs: $TimeoutMs
    hudPort: 45871
"@

$sep = if ($text.EndsWith("`n")) { '' } else { "`r`n" }
$next = $text + $sep + $row
# keep a rollback copy before touching the profile document
Copy-Item $patch "$patch.bak-lens-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
[System.IO.File]::WriteAllText($patch, $next, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "appended lens-computer-control row -> $patch"
