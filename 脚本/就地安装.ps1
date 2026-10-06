<#
    MO 局域网补丁 —— 就地安装（直接在你自己的客户端上打补丁）

    ── 为什么不随包发打好的 exe ──
    被改的 clientdx / clientogl / clientxna.exe 是 CnCNet 客户端（GPL-3.0-or-later）的衍生作品。
    随包分发二进制，就得同时提供"对应源码"；而 MO 客户端自身的那部分改动不在我们手上。
    所以改成在你本机就地打补丁 —— 我们只分发自己写的工具（dnlibpatch + 中继 + 本脚本），
    需要提供的源码就只有我们自己的那份，地址在 README.md 里。

    ── 安全性 ──
    · 打补丁前先把原文件按原目录结构备份到 <游戏根>\_MO补丁备份\（只在你本机，不随包分发）
    · 每份文件的所有步骤先在临时文件里跑完，成功后才覆盖回去；中途任何一步失败 → 自动回滚
    · 当前文件和备份不一致（= 已经打过补丁）时会拒绝执行，提示你先运行"还原.bat"
#>
param(
    [ValidateSet('1', '2')][string]$PatchSet = '2',
    [string]$GameRoot = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$ToolsDir = $PSScriptRoot
$PkgDir = Split-Path $ToolsDir -Parent

function Say([string]$s) { Write-Host $s }
function Head([string]$s) { Write-Host ''; Write-Host ('=== ' + $s + ' ===') }

# ------------------------------------------------------------------
# 0) 找游戏根目录（含 Resources\clientdx.exe 的那层）
# ------------------------------------------------------------------
function Find-GameRoot([string]$start) {
    if ([string]::IsNullOrWhiteSpace($start)) { return '' }
    $d = $start
    for ($i = 0; $i -lt 5; $i++) {
        if (Test-Path (Join-Path $d 'Resources\clientdx.exe')) {
            return (Resolve-Path $d).Path
        }
        $p = Split-Path $d -Parent
        if ([string]::IsNullOrEmpty($p) -or $p -eq $d) { break }
        $d = $p
    }
    return ''
}

Head ('MO 补丁 ' + $PatchSet + ' · 就地安装')

Say ('工具目录: ' + $ToolsDir)

$root = Find-GameRoot $GameRoot
if ($root -eq '') { $root = Find-GameRoot $PkgDir }

while ($root -eq '') {
    Say ''
    Say '没找到游戏根目录（那一层应该有 MentalOmegaClient.exe 和 Resources\clientdx.exe）。'
    Say '请把游戏根目录文件夹直接拖进这个窗口，然后回车。'
    $line = Read-Host '游戏根目录'
    $root = Find-GameRoot $line.Trim('"').Trim()
}

Say ('游戏根目录: ' + $root)

# ------------------------------------------------------------------
# 1) 环境检查：客户端/游戏在跑的话文件是锁的，绝不半更新
# ------------------------------------------------------------------
foreach ($pn in @('clientdx', 'clientogl', 'clientxna', 'gamemd', 'MentalOmegaClient')) {
    $ps = @(Get-Process -Name $pn -ErrorAction SilentlyContinue)
    if ($ps.Count -gt 0) {
        throw ('检测到 ' + $pn + '.exe 正在运行。请先把客户端和游戏完全关掉（任务管理器确认没了）再运行本脚本。')
    }
}

$dnlib = Join-Path $ToolsDir 'dnlibpatch.exe'
if (!(Test-Path $dnlib)) { throw ('缺文件: ' + $dnlib) }

# ------------------------------------------------------------------
# 2) 本次要改哪些文件
# ------------------------------------------------------------------
$exeModes = @('lanip', 'relay', 'relaysend', 'relayhost', 'lobbyfix', 'kick', 'mention', 'tab')

# 键 = 相对游戏根的路径；值 = 依次执行的 dnlibpatch 模式
$jobs = @{}
if ($PatchSet -eq '2') {
    foreach ($n in @('clientdx.exe', 'clientogl.exe', 'clientxna.exe')) {
        $jobs['Resources\' + $n] = $exeModes
    }
} else {
    $jobs['Resources\Binaries\Windows\MonoGame.Framework.dll'] = @('ime')
}

# 打完补丁后还要放进去的新文件（原版安装里没有，还原时删掉）
$addFiles = @()
if ($PatchSet -eq '2') {
    $addFiles = @('Resources\MoLanRelay.dll', 'Resources\Binaries\MoLanRelay.dll')
}

# ------------------------------------------------------------------
# 3) 前置检查：目标文件都在、且都还是原版（没被改过）
# ------------------------------------------------------------------
$backupDir = Join-Path $root '_MO补丁备份'

foreach ($rel in $jobs.Keys) {
    $full = Join-Path $root $rel
    if (!(Test-Path $full)) { throw ('缺文件: ' + $full + '（游戏版本不对？本补丁对应 MO 客户端 2.6.10.3）') }

    $bak = Join-Path $backupDir $rel
    if (Test-Path $bak) {
        $h1 = (Get-FileHash $full).Hash
        $h2 = (Get-FileHash $bak).Hash
        if ($h1 -ne $h2) {
            throw ($rel + ' 已经不是原版了（和备份不一致）——它可能已经打过补丁或被你改过。' + "`r`n" +
                   '请先运行 还原.bat 把它恢复成原版，再重新安装。')
        }
    }
}

# ------------------------------------------------------------------
# 4) 开始：备份 → 打补丁（临时文件）→ 覆盖
# ------------------------------------------------------------------
$tmp = Join-Path $env:TEMP ('mo_patch_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$done = New-Object System.Collections.ArrayList

function Restore-Done {
    # 出错时回滚：把已经动过的文件都从备份恢复回去
    foreach ($rel in $done) {
        $bak = Join-Path $backupDir $rel
        if (Test-Path $bak) {
            Copy-Item $bak (Join-Path $root $rel) -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    foreach ($rel in $jobs.Keys) {
        $modes = $jobs[$rel]
        $full = Join-Path $root $rel
        $bak = Join-Path $backupDir $rel

        # 备份（只在你本机）
        if (!(Test-Path $bak)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
            Copy-Item $full $bak -Force
            Say ('  备份: ' + $rel)
        }

        # 逐模式链式打补丁，全部落在临时目录里
        $cur = $full
        $step = 0
        foreach ($m in $modes) {
            $step++
            $next = Join-Path $tmp ($step.ToString() + '.bin')
            $o = & $dnlib $cur $next $m 2>&1
            $o | ForEach-Object { Say ('    ' + $_) }
            if ($LASTEXITCODE -ne 0) { throw ('打补丁失败（模式 ' + $m + '，文件 ' + $rel + '）') }
            if (!(Test-Path $next) -or (Get-Item $next).Length -eq 0) { throw ('补丁器没产出有效文件（模式 ' + $m + '）') }
            $cur = $next
        }

        # 覆盖回去
        [void]$done.Add($rel)
        Move-Item $cur $full -Force
        Say ('  完成: ' + $rel + '  （' + ($modes -join ' → ') + '）')
    }

    # 放入中继 DLL（我们的代码）
    foreach ($rel in $addFiles) {
        $full = Join-Path $root $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
        Copy-Item (Join-Path $ToolsDir 'MoLanRelay.dll') $full -Force
        Say ('  放入: ' + $rel)
    }
}
catch {
    Say ''
    Say ('出错：' + $_.Exception.Message)
    Say '正在回滚已经改动过的文件 ...'
    Restore-Done
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    throw '安装失败，已回滚。'
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------
# 5) 自检
# ------------------------------------------------------------------
Head '自检'
foreach ($rel in $jobs.Keys) {
    $full = Join-Path $root $rel
    $ok = (Test-Path $full) -and ((Get-Item $full).Length -gt 0)
    Say ('  ' + $(if ($ok) { '[OK]  ' } else { '[失败]' }) + ' ' + $rel + '  ' + (Get-Item $full).Length + ' 字节  SHA256=' + (Get-FileHash $full).Hash.Substring(0, 16) + '…')
}
foreach ($rel in $addFiles) { Say ('  [OK]  ' + $rel) }

Say ''
Say '安装完成。原文件已备份到: ' + $backupDir
Say '（要回到原版，双击 还原.bat 即可。该备份只在你本机，不要往外发。）'

if ($PatchSet -eq '2') {
    Say ''
    Say '提醒：参与联机的每一台机器都要装同样的包，中继版本号要一致'
    Say '      （进游戏后看 MoLanRelay.log 第一行的 ======== MoLanRelay v... ========）。'
}
