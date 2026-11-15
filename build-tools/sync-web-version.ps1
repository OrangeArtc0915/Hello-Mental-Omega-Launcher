# 把主仓库的 AppInfo.cs 同步到官网仓库（Web 子目录），提交并尝试推送。
# build.bat 在组装完发行包后调用，保证官网显示的版本号与发行版本一致。
#
# 只处理 Web\AppInfo.cs 一个文件，避免把 Web 仓库里其他未提交改动卷进来：
#   - 版本没变 -> 不提交，避免空提交；
#   - Web 仓库当前不在 Web 分支 -> 只提交不推送，防止推错分支；
#   - 推送失败 -> 只打印警告，不影响构建结果。

param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'Web'
$source = Join-Path $root 'src\HMOL.Core\App\AppInfo.cs'
$target = Join-Path $web 'AppInfo.cs'

if (-not (Test-Path $source)) { Write-Host '  [同步] 找不到 AppInfo.cs，跳过'; exit 0 }
if (-not (Test-Path $web)) { Write-Host '  [同步] 没有 Web 目录，跳过'; exit 0 }
if (-not (Test-Path (Join-Path $web '.git'))) { Write-Host '  [同步] Web 不是独立仓库，跳过'; exit 0 }

Copy-Item $source $target -Force

git -C $web add AppInfo.cs
git -C $web diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host '  [同步] 官网版本副本已是最新'
    exit 0
}

$message = if ($Version) { "chore: sync AppInfo.cs for v$Version" } else { 'chore: sync AppInfo.cs' }
git -C $web commit -q -m $message
if ($LASTEXITCODE -ne 0) {
    Write-Host '  [同步] 提交失败，请手动检查 Web 仓库'
    exit 0
}

$branch = (git -C $web rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'Web') {
    Write-Host "  [同步] 已提交，但当前分支是 $branch（不是 Web），未推送"
    exit 0
}

git -C $web push github Web 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host '  [同步] 官网版本副本已提交并推送'
} else {
    Write-Host '  [同步] 已提交，推送失败（网络或凭据），请稍后手动推送 Web 分支'
}
