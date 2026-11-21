# 发布 Lens 到 npm
# 用法：在 PowerShell 里运行  pwsh -File D:\code\computerconlex\publish-tools\publish-npm.ps1
# 运行后 npm 会提示 "This operation requires a one-time password. Enter OTP:"，直接输 6 位验证码即可。

$ErrorActionPreference = 'Stop'

$userconfig = 'D:\code\computerconlex\.npmrc.t2'   # 里面是 xxccdl 的 granular token
$pkgDir     = 'D:\code\computerconlex\plugin'

if (-not (Test-Path $userconfig)) { throw "缺少 token 文件：$userconfig" }
$env:npm_config_userconfig = $userconfig

Set-Location $pkgDir

# 发布前的一次性检查
$pkg = Get-Content (Join-Path $pkgDir 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "包名: $($pkg.name)   版本: $($pkg.version)" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $pkgDir 'bin\lens.exe'))) { throw "缺少 bin\lens.exe，引擎没打进包" }

npm publish --registry=https://registry.npmjs.org/ --access public
if ($LASTEXITCODE -ne 0) {
  Write-Host "`n发布失败（退出码 $LASTEXITCODE）。常见原因：" -ForegroundColor Yellow
  Write-Host "  EOTP/403 -> 验证码错了或过期，重跑本脚本，输新的 6 位码"
  Write-Host "  E409     -> 这个版本已经存在，先把 package.json 的 version 加一"
  exit $LASTEXITCODE
}

Write-Host "`n已发布：https://www.npmjs.com/package/$($pkg.name)" -ForegroundColor Green
npm view $pkg.name version --registry=https://registry.npmjs.org/
