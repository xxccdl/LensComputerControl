# 一条命令完成发布：写 token → 校验 → npm publish → 核对 registry → 打 tag
#
#   powershell -NoProfile -File publish-tools/publish-now.ps1 -Token npm_xxx
#   powershell -NoProfile -File publish-tools/publish-now.ps1 -Token npm_xxx -SkipTag
#
# 只写入 .npmrc.t2（已在 .gitignore 里），不会把 token 带进 git。
param(
  [Parameter(Mandatory = $true)][string]$Token,
  [switch]$SkipTag,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Token -notmatch '^npm_[A-Za-z0-9]{20,}$') { throw "token 形状不对（应以 npm_ 开头）" }

$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
Write-Host "[1/5] 版本 $version；写入 .npmrc.t2" -ForegroundColor Cyan
$rc = "registry=https://registry.npmjs.org/`n//registry.npmjs.org/:_authToken=$Token`n"
[System.IO.File]::WriteAllText((Join-Path $root '.npmrc.t2'), $rc, (New-Object System.Text.UTF8Encoding($false)))
if (-not (git check-ignore -q .npmrc.t2; $LASTEXITCODE -eq 0)) { Write-Warning '.npmrc.t2 没有被 git 忽略，检查 .gitignore' }

Write-Host '[2/5] 校验身份与权限' -ForegroundColor Cyan
try {
  $who = Invoke-RestMethod -Uri 'https://registry.npmjs.org/-/whoami' -Headers @{ Authorization = "Bearer $Token" } -TimeoutSec 20
  Write-Host "      whoami = $($who.username)"
} catch {
  throw "token 无效或过期：$($_.Exception.Message)"
}

Write-Host '[3/5] 跑测试' -ForegroundColor Cyan
npm test 2>&1 | Select-String '合计' | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw '测试没通过，停止发布' }

if ($DryRun) {
  Write-Host '[4/5] --DryRun：跳过真正的发布' -ForegroundColor Yellow
  npm pack 2>&1 | Select-String 'filename|size' | ForEach-Object { $_.Line }
  Remove-Item *.tgz -ErrorAction SilentlyContinue
  return
}

Write-Host '[4/5] 发布' -ForegroundColor Cyan
$out = npm publish --userconfig .npmrc.t2 --access public 2>&1
$out | Select-Object -Last 6 | ForEach-Object { $_ }
if ($out -match 'EOTP') {
  throw '需要 2FA：这个 token 没勾「Bypass two-factor authentication」，或者改用 npm publish --otp=xxxxxx'
}
if ($out -match 'npm error') { throw '发布失败，看上面输出' }

Write-Host '[5/5] 核对 registry' -ForegroundColor Cyan
Start-Sleep -Seconds 2
$meta = Invoke-RestMethod -Uri "https://registry.npmjs.org/dsh-lens-computer-control/$version" -TimeoutSec 20
Write-Host "      已上线：$($meta.name)@$($meta.version)   tarball=$($meta.dist.tarball)"
Write-Host "      shasum=$($meta.dist.shasum)"

if (-not $SkipTag) {
  $tag = "v$version"
  if (-not (git tag --list $tag)) {
    git tag -a $tag -m "$version"
    Write-Host "      已打 tag $tag（推送：git push origin $tag）" -ForegroundColor Cyan
  } else {
    Write-Host "      tag $tag 已存在"
  }
}
