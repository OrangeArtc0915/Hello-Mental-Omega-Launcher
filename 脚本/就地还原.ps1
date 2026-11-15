<#
    MO 局域网补丁 —— 一键还原

    把 <游戏根>\_MO补丁备份\ 里的原版文件覆盖回去，
    并删掉补丁放进去的 MoLanRelay.dll（原版安装里没有这两个文件）。
    备份本来就只存在于你本机，所以这个操作完全在你自己的机器上完成。
#>
param([string]$GameRoot = '')

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$ToolsDir = $PSScriptRoot
$PkgDir = Split-Path $ToolsDir -Parent

function Say([string]$s) { Write-Host $s }
function Head([string]$s) { Write-Host ''; Write-Host ('=== ' + $s + ' ===') }

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

Head 'MO 补丁 · 还原'

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

foreach ($pn in @('clientdx', 'clientogl', 'clientxna', 'gamemd', 'MentalOmegaClient')) {
    $ps = @(Get-Process -Name $pn -ErrorAction SilentlyContinue)
    if ($ps.Count -gt 0) {
        throw ('检测到 ' + $pn + '.exe 正在运行。请先把客户端和游戏完全关掉再还原。')
    }
}

$backupDir = Join-Path $root '_MO补丁备份'
if (!(Test-Path $backupDir)) {
    throw ('没有找到备份目录: ' + $backupDir + "`r`n" + '说明这台机器没有用本工具打过补丁（或者备份被删了）。')
}

# 1) 把备份里的文件原样覆盖回去
$n = 0
$skipped = New-Object System.Collections.ArrayList
Get-ChildItem -LiteralPath $backupDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($backupDir.Length).TrimStart('\')
    $dst = Join-Path $root $rel

    # 备份文件是 0 字节/读不出来时绝不覆盖 —— 那多半是半拷贝，会把能用的客户端写坏
    if ($_.Length -eq 0) { [void]$skipped.Add($rel); return }

    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
    Say ('  还原: ' + $rel)
    $n++
}

# 2) 删掉补丁新加进去的文件（备份里没有的那些）
$removed = New-Object System.Collections.ArrayList
foreach ($rel in @('Resources\MoLanRelay.dll', 'Resources\Binaries\MoLanRelay.dll')) {
    $inBackup = Test-Path -LiteralPath (Join-Path $backupDir $rel)
    $full = Join-Path $root $rel
    if (!$inBackup -and (Test-Path -LiteralPath $full)) {
        Remove-Item -LiteralPath $full -Force
        [void]$removed.Add($rel)
        Say ('  删除: ' + $rel + '（本补丁新加的）')
    }
}

# 3) 补丁自己生成的运行期文件（设置/日志/直连临时文件），一并清掉，回到原版状态
foreach ($rel in @('Resources\MoLanPatch.ini', 'MoLanRelay.log', 'Resources\lanip.txt')) {
    $full = Join-Path $root $rel
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        [void]$removed.Add($rel)
        Say ('  删除: ' + $rel + '（本补丁生成的）')
    }
}

Say ''
if ($n -eq 0) {
    Say '***** 一个文件都没恢复：备份目录是空的，或里面的文件是 0 字节。*****'
    Say ('请检查 ' + $backupDir + '，必要时用你自己的备份重装客户端。')
    exit 1
}
if ($skipped.Count -gt 0) {
    Say '注意：下面这些备份是 0 字节，已跳过（没有覆盖）：'
    foreach ($s in $skipped) { Say ('    ' + $s) }
}
Say ('还原完成，共恢复 ' + $n + ' 个文件。备份目录保留在 ' + $backupDir + '，确认没问题后可以自己删掉。')
