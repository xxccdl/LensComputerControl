# 轮询 npm 网页 2FA 完成状态，拿到一次性 pass 后立刻审批已暂存的 0.8.7
# 用法: pwsh -File approve-stage.ps1 -DoneUrl "<doneUrl>" -StageId "<uuid>"
param(
  [Parameter(Mandatory=$true)][string]$DoneUrl,
  [Parameter(Mandatory=$true)][string]$StageId
)

$ErrorActionPreference = 'Stop'

$line  = (Get-Content 'D:\code\computerconlex\.npmrc.t2') | Where-Object { $_ -like '*_authToken=*' }
$token = $line.Substring($line.LastIndexOf('=')+1).Trim()
$h     = @{ Authorization = "Bearer $token"; 'npm-auth-type' = 'web' }

$deadline = (Get-Date).AddMinutes(12)
$otp = $null

while ((Get-Date) -lt $deadline) {
  try {
    $r = Invoke-WebRequest -Uri $DoneUrl -Headers $h -UseBasicParsing -TimeoutSec 20
    $j = $r.Content | ConvertFrom-Json
    if ($j.token) { $otp = $j.token; break }
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -and $code -ne 202) { Write-Host "done 轮询 HTTP $code：$($_.ErrorDetails.Message)" }
  }
  Start-Sleep -Seconds 3
}

if (-not $otp) { Write-Host '超时：浏览器里没有完成 2FA 验证'; exit 1 }

Write-Host "拿到一次性 pass（长度 $($otp.Length)），开始审批 $StageId ..."

$h2 = @{ Authorization = "Bearer $token"; 'npm-auth-type' = 'web'; 'npm-otp' = $otp }
try {
  $r = Invoke-WebRequest -Uri "https://registry.npmjs.org/-/stage/$StageId/approve" -Method POST -Headers $h2 -ContentType 'application/json' -Body '{}' -UseBasicParsing -TimeoutSec 60
  Write-Host "审批成功：HTTP $($r.StatusCode) $($r.Content)"
} catch {
  Write-Host "审批失败：HTTP $($_.Exception.Response.StatusCode.value__) $($_.ErrorDetails.Message)"
  exit 1
}
