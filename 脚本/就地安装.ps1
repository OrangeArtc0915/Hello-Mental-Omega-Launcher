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

# GPL-3.0 §5(a)：改别人的程序，必须给出**显著**的修改声明（改了哪个文件 + 日期）。
# 这里在安装前先把声明打出来，用户不一定去看 README。
Say ''
Say '本补丁根据开源的 CnCNet 客户端（XNA CnCNet Client）修改而来，完全遵照其开源许可协议'
Say '（GNU GPL-3.0-or-later）发布。'
Say '本项目与 Mental Omega（MO）开发组、MO 客户端官方没有任何关联，不是官方补丁。'
Say '源码（patch 分支）: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/tree/patch'
Say '许可与第三方组件: 见本包 README.md / NOTICE.md / LICENSE / LICENSE-CnCNet-Client.md'
Say ''
Say '修改日期: 首次 2026-10-06，最近 2026-10-25'
Say '将被修改的文件（都在 <游戏根>\Resources\ 下，改动前会自动备份）:'
if ($PatchSet -eq '2') {
    Say '  clientdx.exe / clientogl.exe / clientxna.exe'
    Say '  MainMenu.ini（隐藏 CnCNet 线上大厅入口，局域网入口上移）'
} else {
    Say '  Binaries\Windows\MonoGame.Framework.dll'
}
Say ''

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
$exeModes = @('lanip', 'relay', 'relaysend', 'relayhost', 'lobbyfix', 'kick', 'chatcmd', 'deny', 'mention', 'tab', 'preflight', 'verlabel', 'rxbuf', 'lanport', 'mapmsg', 'lantick')

# 键 = 相对游戏根的路径；值 = 依次执行的 dnlibpatch 模式
$jobs = @{}
if ($PatchSet -eq '2') {
    foreach ($n in @('clientdx.exe', 'clientogl.exe', 'clientxna.exe')) {
        $jobs['Resources\' + $n] = $exeModes
    }
} else {
    $jobs['Resources\Binaries\Windows\MonoGame.Framework.dll'] = @('ime')
    # 输入法组词时吞掉 Enter/Esc：不这么做的话，按回车选词会同时触发
    # 「发送聊天 + 清空输入框」，打好的中文直接没了。
    $jobs['Resources\Binaries\Windows\Rampastring.XNAUI.dll'] = @('imekey')
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
$iniRel = 'Resources\MainMenu.ini'
$iniFull = Join-Path $root $iniRel
$iniBak = Join-Path $backupDir $iniRel

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

if ($PatchSet -eq '2') {
    if (!(Test-Path $iniFull)) { throw ('缺文件: ' + $iniFull + '（游戏版本不对？本补丁对应 MO 客户端 2.6.10.3）') }
    if (Test-Path $iniBak) {
        $h1 = (Get-FileHash $iniFull).Hash
        $h2 = (Get-FileHash $iniBak).Hash
        if ($h1 -ne $h2) {
            throw ($iniRel + ' 已经不是原版了（和备份不一致）——它可能已经打过补丁或被你改过。' + "`r`n" +
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
$added = New-Object System.Collections.ArrayList      # 本补丁新增的文件（回滚时要删掉）

function Restore-Done {
    # 出错时回滚：把已经动过的文件都从备份恢复回去，并删掉已经放进去的新增文件
    foreach ($rel in $done) {
        $bak = Join-Path $backupDir $rel
        if (Test-Path -LiteralPath $bak) {
            Copy-Item -LiteralPath $bak -Destination (Join-Path $root $rel) -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($f in $added) {
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }
}

# 在指定 [section] 里设置一行 key=value：有就替换，没有就加在段尾。
# 用逐行处理而不是正则回调，避免 PowerShell 5.1 里闭包变量取不到。
function Set-IniSectionValue([string]$text, [string]$section, [string]$key, [string]$value) {
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = $text -split '\r?\n', -1
    $result = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    $done = $false

    foreach ($line in $lines) {
        $trim = $line.Trim()

        if ($trim -match '^\[(.+)\]$') {
            if ($inSection -and -not $done) {
                $result.Add($key + '=' + $value)
                $done = $true
            }

            $inSection = ($matches[1] -ieq $section)
            $result.Add($line)
            continue
        }

        if ($inSection) {
            if ($trim -match ('^' + [regex]::Escape($key) + '\s*=')) {
                $result.Add($key + '=' + $value)
                $done = $true
                continue
            }
        }

        $result.Add($line)
    }

    if ($inSection -and -not $done) { $result.Add($key + '=' + $value) }

    return ($result -join $nl)
}

# 读取指定 [section] 里某个键的值（同名键取最后一个，和客户端的解析顺序一致）
function Get-IniValue([string]$text, [string]$section, [string]$key) {
    $lines = $text -split '\r?\n', -1
    $inSection = $false
    $value = ''
    foreach ($line in $lines) {
        $trim = $line.Trim()
        if ($trim -match '^\[(.+)\]$') { $inSection = ($matches[1] -ieq $section); continue }
        if ($inSection -and $trim -match ('^' + [regex]::Escape($key) + '\s*=\s*(.*)$')) { $value = $matches[1].Trim() }
    }
    return $value
}

# 隐藏 CnCNet 线上入口：按**当前**按钮坐标把下面的按钮整体上移 42 像素，
# 而不是写死坐标 —— 原版布局和第三方主题（例如 Bt Theme Pack 的 x=13）都不会被弄乱。
# 如果 [btnCnCNet] 已经是 Visible=0（重复安装），就不再上移，避免二次位移。
function Hide-CnCNetEntry([string]$text) {
    $shift = 42
    $anchors = @('btnLan', 'btnLoadGame', 'btnOptions', 'btnMapEditor', 'btnStatistics', 'btnExit')

    if ((Get-IniValue $text 'btnCnCNet' 'Visible') -ne '0') {
        foreach ($sec in $anchors) {
            $loc = Get-IniValue $text $sec 'Location'
            if ($loc -match '^\s*(-?\d+)\s*,\s*(-?\d+)\s*$') {
                $text = Set-IniSectionValue $text $sec 'Location' ($matches[1] + ',' + ([int]$matches[2] - $shift))
            }
        }
    }

    $text = Set-IniSectionValue $text 'btnCnCNet' 'Visible' '0'
    $text = Set-IniSectionValue $text 'lblCnCNetStatus' 'Visible' '0'
    $text = Set-IniSectionValue $text 'lblCnCNetPlayerCount' 'Visible' '0'
    return $text
}

try {
    foreach ($rel in $jobs.Keys) {
        $modes = $jobs[$rel]
        $full = Join-Path $root $rel
        $bak = Join-Path $backupDir $rel

        # 备份（只在你本机）
        if (!(Test-Path -LiteralPath $bak)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
            Copy-Item -LiteralPath $full -Destination $bak -Force
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
            if (!(Test-Path -LiteralPath $next) -or (Get-Item -LiteralPath $next).Length -eq 0) { throw ('补丁器没产出有效文件（模式 ' + $m + '）') }
            $cur = $next
        }

        # 覆盖回去
        [void]$done.Add($rel)
        Move-Item -LiteralPath $cur -Destination $full -Force
        Say ('  完成: ' + $rel + '  （' + ($modes -join ' → ') + '）')
    }

    # 隐藏客户端主菜单的 CnCNet 线上大厅入口，并把下面的按钮整体上移 42 像素
    if ($PatchSet -eq '2') {
        if (!(Test-Path -LiteralPath $iniBak)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $iniBak -Parent) | Out-Null
            Copy-Item -LiteralPath $iniFull -Destination $iniBak -Force
            Say ('  备份: ' + $iniRel)
        }

        # 编码：有 BOM 就跟 BOM；没有就按 UTF-8 严格解码试一次。
        # 否则遇到 ANSI/UTF-16 的 ini 会把中文读成乱码再写回去（备份是好的，但用户不会想到还原）。
        $iniBytes = [IO.File]::ReadAllBytes($iniFull)
        $iniHasBom = ($iniBytes.Length -ge 3 -and $iniBytes[0] -eq 0xEF -and $iniBytes[1] -eq 0xBB -and $iniBytes[2] -eq 0xBF)
        $iniStrict = New-Object Text.UTF8Encoding($false, $true)
        try { $iniText = $iniStrict.GetString($iniBytes) }
        catch { throw ($iniRel + ' 不是 UTF-8 编码，为避免写坏它，安装已停止。请先把该文件另存为 UTF-8 再重试。') }
        if ($iniText.Length -gt 0 -and $iniText[0] -eq [char]0xFEFF) { $iniText = $iniText.Substring(1) }

        $iniText = Hide-CnCNetEntry $iniText

        # 先记入回滚清单，再"写临时文件 + 替换"，避免掉电/占用留下半个 ini
        [void]$done.Add($iniRel)
        $iniNew = $iniFull + '.new'
        [IO.File]::WriteAllText($iniNew, $iniText, (New-Object Text.UTF8Encoding($iniHasBom)))
        Move-Item -LiteralPath $iniNew -Destination $iniFull -Force
        Say ('  完成: ' + $iniRel + '（隐藏 CnCNet 线上入口，局域网按钮上移）')
    }

    # 放入中继 DLL（我们的代码）
    foreach ($rel in $addFiles) {
        $full = Join-Path $root $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $ToolsDir 'MoLanRelay.dll') -Destination $full -Force
        [void]$added.Add($full)          # 回滚时要把这两份也删掉，不然"已回滚"是假的
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
$selfCheckFailed = $false
foreach ($rel in $jobs.Keys) {
    $full = Join-Path $root $rel
    $ok = (Test-Path -LiteralPath $full) -and ((Get-Item -LiteralPath $full).Length -gt 0)
    if (!$ok) { $selfCheckFailed = $true }
    $size = if ($ok) { (Get-Item -LiteralPath $full).Length } else { 0 }
    $sha  = if ($ok) { (Get-FileHash -LiteralPath $full).Hash.Substring(0, 16) + '…' } else { '-' }
    Say ('  ' + $(if ($ok) { '[OK]  ' } else { '[失败]' }) + ' ' + $rel + '  ' + $size + ' 字节  SHA256=' + $sha)
}
foreach ($rel in $addFiles) {
    $full = Join-Path $root $rel
    if (!(Test-Path -LiteralPath $full)) { $selfCheckFailed = $true; Say ('  [失败] ' + $rel + '（没有写进去）') }
    else { Say ('  [OK]  ' + $rel) }
}

if ($PatchSet -eq '2') {
    $iniText = [IO.File]::ReadAllText($iniFull, [Text.Encoding]::UTF8)
    $okIni = (Get-IniValue $iniText 'btnCnCNet' 'Visible') -eq '0'
    if (!$okIni) { $selfCheckFailed = $true }
    Say ('  ' + $(if ($okIni) { '[OK]  ' } else { '[失败]' }) + ' ' + $iniRel + '（CnCNet 线上入口已隐藏；btnLan=' + (Get-IniValue $iniText 'btnLan' 'Location') + '）')
}

Say ''
Say '安装完成。原文件已备份到: ' + $backupDir
Say '（要回到原版，双击 还原.bat 即可。该备份只在你本机，不要往外发。）'

if ($PatchSet -eq '2') {
    Say ''
    Say '提醒：参与联机的每一台机器都要装同样的包，中继版本号要一致'
    Say '      （进游戏后看 MoLanRelay.log 第一行的 ======== MoLanRelay v... ========）。'
}

# 自检不过就拿非零退出码，让 安装.bat 明确报 FAILED（以前只打印 [失败]，.bat 仍显示 Done）
if ($selfCheckFailed) {
    Say ''
    Say '***** 自检有失败项：文件可能没写全，请不要用于联机，建议先还原再重装。*****'
    exit 1
}
