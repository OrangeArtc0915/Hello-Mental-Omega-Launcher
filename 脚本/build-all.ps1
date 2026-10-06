<#
    一键重建两个补丁包（**工具包**，不含任何第三方二进制）

        补丁1-中文输入 = 安装器（对 Resources\Binaries\Windows\MonoGame.Framework.dll 打 ime）
        补丁2-联机     = 安装器（对三个 exe 打 lanip → relay → relaysend → relayhost
                                  → lobbyfix → kick → mention → tab）
                         + 工具\MoLanRelay.dll（我们自己编译的中继）

    ── 为什么不再产出成品 exe ──
    被改的 clientdx/ogl/xna.exe 是 CnCNet 客户端（GPL-3.0-or-later）的衍生作品。
    随包分发二进制就得同时提供"对应源码"，而 MO 客户端自身那部分改动不在我们手上。
    改成"包里只有工具、在你本机就地打补丁"之后，我们只分发自己的代码，
    GPL 的义务只剩"提供我们自己的源码"——源码在 GitHub（见 README.md 第 0 节）。

    ── dnlibpatch 为什么要 net48 版 ──
    装补丁的人不一定有 .NET 10 运行时，但 Win10/11 自带 .NET Framework 4.8。
    dnlib 4.4.0 有 net45 目标，所以直接用 csc 编一个 net48 的 exe 出来。

    用法（默认值就是开发机上的布局，换机器时改下面 param）：
        powershell -ExecutionPolicy Bypass -File build-all.ps1

    产物：
        <OutDir>\补丁1-中文输入\...
        <OutDir>\补丁2-联机\...
        <OutDir>\MO-补丁1-中文输入.zip
        <OutDir>\MO-补丁2-联机.zip
#>
[CmdletBinding()]
param(
    # ---- 输入：原版（未打补丁）文件，只用于端到端自检 ----
    [string]$OriginalExeDir = "d:\F\MO\CNC\_lanipbak",                                        # clientdx/ogl/xna.exe
    [string]$OriginalMono    = "d:\F\MO\CNC\Mental Omega\backup-original\Windows\MonoGame.Framework.dll",

    # ---- 输入：源码 ----
    [string]$RootDir     = "",       # 默认 ..（源码根，README/LICENSE 从这里拿）
    [string]$RelaySource = "",       # 默认 ..\中继\MoLanRelay.cs
    [string]$NotesDir    = "",       # 默认 ..\说明文本

    # ---- 工具链 ----
    [string]$DotNet       = "d:\F\MO\CNC\.dotnet10\dotnet.exe",
    [string]$Csc          = "d:\F\MO\CNC\.dotnet10\sdk\10.0.100\Roslyn\bincore\csc.dll",
    [string]$FxDir        = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
    [string]$RelayRefDir  = "d:\F\MO\CNC\Mental Omega\Resources\Binaries\Windows",
    [string]$DnlibNet45   = "$env:USERPROFILE\.nuget\packages\dnlib\4.4.0\lib\net45\dnlib.dll",

    # ---- 输出 ----
    [string]$OutDir = "",            # 默认 ..\..\_build

    # ---- 端到端自检：把原版文件铺成一个假游戏目录，跑一遍"就地安装"，
    #      再和这些已验证过的成品逐个比 SHA256。传不存在的路径则跳过。 ----
    [string]$VerifyAgainst = "d:\F\MO\CNC\Mental Omega\Resources"
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($RootDir))     { $RootDir     = Resolve-Path (Join-Path $PSScriptRoot '..') }
if ([string]::IsNullOrEmpty($RelaySource)) { $RelaySource = Join-Path $RootDir '中继\MoLanRelay.cs' }
if ([string]::IsNullOrEmpty($NotesDir))    { $NotesDir    = Join-Path $RootDir '说明文本' }
if ([string]::IsNullOrEmpty($OutDir))      { $OutDir      = Join-Path $RootDir '..\..\_build' }

$ExeNames = @('clientdx.exe', 'clientogl.exe', 'clientxna.exe')

$p1   = Join-Path $OutDir '补丁1-中文输入'
$p2   = Join-Path $OutDir '补丁2-联机'
$work = Join-Path $OutDir '_work'
foreach ($d in @($p1, $p2, $work)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# 组一个包里要带的文档清单（README / 许可 / 安装说明）
$Docs = @('README.md', 'LICENSE', 'LICENSE-CnCNet-Client.md', '构建说明.md')

# ------------------------------------------------------------------
# 1) 编译 net48 版 IL 补丁工具（用户在没装 .NET 的机器上也能跑）
# ------------------------------------------------------------------
Write-Host '[1/6] 编译 IL 补丁工具（net48）...'
if (!(Test-Path $DnlibNet45)) {
    throw ('找不到 dnlib 的 net45 目标: ' + $DnlibNet45 + '  （NuGet 缓存里应该有 net35/net45/net6.0/netstandard2.0）')
}
$toolsOut = Join-Path $OutDir '_tools48'
New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
$ca = @($Csc, '-noconfig', '-nostdlib+', '-target:exe', "-out:$toolsOut\dnlibpatch.exe",
        '-platform:anycpu', '-optimize+', '-deterministic+',
        "-reference:$FxDir\mscorlib.dll", "-reference:$FxDir\System.dll", "-reference:$FxDir\System.Core.dll",
        "-reference:$DnlibNet45", (Join-Path $RootDir 'IL补丁工具\Program.cs'))
& $DotNet @ca
if ($LASTEXITCODE -ne 0) { throw '编译 IL 补丁工具失败' }
Copy-Item $DnlibNet45 (Join-Path $toolsOut 'dnlib.dll') -Force

# ------------------------------------------------------------------
# 2) 编译中继 MoLanRelay.dll
# ------------------------------------------------------------------
Write-Host '[2/6] 编译中继 MoLanRelay.dll ...'
$relayDll = Join-Path $work 'MoLanRelay.dll'
$refs = @((Join-Path $FxDir 'mscorlib.dll'), (Join-Path $FxDir 'System.dll'), (Join-Path $FxDir 'System.Core.dll'),
          (Join-Path $FxDir 'System.Drawing.dll'), (Join-Path $FxDir 'System.Windows.Forms.dll'),
          (Join-Path $OriginalExeDir 'clientxna.exe'))
$refs += (Get-ChildItem (Join-Path $RelayRefDir '*.dll') | ForEach-Object { $_.FullName })
$ra = @($Csc, '-noconfig', '-nostdlib+', '-target:library', "-out:$relayDll", '-platform:anycpu', '-optimize+', '-deterministic+')
$ra += ($refs | ForEach-Object { "-reference:$_" })
$ra += $RelaySource
& $DotNet @ra
if ($LASTEXITCODE -ne 0) { throw '编译中继 DLL 失败' }

# ------------------------------------------------------------------
# 3~4) 组装两个工具包
#
#   <包>\安装.bat / 还原.bat        ← ASCII 内容（cmd 按 OEM 码页读 .bat，不能放中文）
#   <包>\安装说明.txt / README.md / LICENSE / LICENSE-CnCNet-Client.md / 构建说明.md
#   <包>\工具\                      ← 名字保持中文；.bat 里不引用它，全在 .ps1 里拼
#         dnlibpatch.exe  dnlib.dll  就地安装.ps1  就地还原.ps1  [MoLanRelay.dll]
# ------------------------------------------------------------------
function New-Batch([string]$OutFile, [string]$PatchSet) {
    $lines = @(
        '@echo off',
        'chcp 65001 >nul',
        'title MO patch ' + $PatchSet,
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" -PatchSet ' + $PatchSet,
        'set RC=%ERRORLEVEL%',
        'echo.',
        'if not "%RC%"=="0" (echo ***** FAILED - read the messages above *****) else (echo ***** Done *****)',
        'echo.',
        'pause'
    )
    # .bat 必须 ASCII + CRLF
    [IO.File]::WriteAllText($OutFile, ($lines -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
}

function Copy-Common([string]$Pkg) {
    # 目录名用 ASCII 的 tools\：.bat 的内容必须是纯 ASCII（cmd 按 OEM 码页读 .bat），
    # 里面要引用这个目录，所以它不能是中文名。
    $t = New-Item -ItemType Directory -Force -Path (Join-Path $Pkg 'tools')
    Copy-Item (Join-Path $toolsOut 'dnlibpatch.exe') $t -Force
    Copy-Item (Join-Path $toolsOut 'dnlib.dll')      $t -Force
    Copy-Item (Join-Path $RootDir '脚本\就地安装.ps1') (Join-Path $t 'install.ps1') -Force
    Copy-Item (Join-Path $RootDir '脚本\就地还原.ps1') (Join-Path $t 'restore.ps1') -Force
    foreach ($f in $Docs) {
        $src = Join-Path $RootDir $f
        if (Test-Path $src) { Copy-Item $src $Pkg -Force }
    }
}

Write-Host '[3/6] 组装 补丁1-中文输入 ...'
Copy-Common $p1
New-Batch (Join-Path $p1 '安装.bat') '1'
New-Batch (Join-Path $p1 '还原.bat') '1'
Copy-Item (Join-Path $NotesDir '补丁1-安装说明.txt') (Join-Path $p1 '安装说明.txt') -Force

Write-Host '[4/6] 组装 补丁2-联机 ...'
Copy-Common $p2
Copy-Item $relayDll (Join-Path $p2 'tools\MoLanRelay.dll') -Force
New-Batch (Join-Path $p2 '安装.bat') '2'
New-Batch (Join-Path $p2 '还原.bat') '2'
Copy-Item (Join-Path $NotesDir '补丁2-安装说明.txt') (Join-Path $p2 '安装说明.txt') -Force

# ------------------------------------------------------------------
# 5) 打包
# ------------------------------------------------------------------
Write-Host '[5/6] 打包 ...'
$z1 = Join-Path $OutDir 'MO-补丁1-中文输入.zip'
$z2 = Join-Path $OutDir 'MO-补丁2-联机.zip'
Remove-Item $z1, $z2 -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $p1 '*') -DestinationPath $z1 -Force
Compress-Archive -Path (Join-Path $p2 '*') -DestinationPath $z2 -Force

# ------------------------------------------------------------------
# 6) 端到端自检：假游戏目录 + 真跑安装器 + 比 SHA256（再测一次还原）
# ------------------------------------------------------------------
if ($VerifyAgainst -and (Test-Path $VerifyAgainst)) {
    Write-Host ''
    Write-Host '[6/6] 端到端自检：铺一个假游戏目录，真跑一遍安装器'
    $fake = Join-Path $OutDir '_selftest\Game'
    if (Test-Path (Join-Path $OutDir '_selftest')) { Remove-Item (Join-Path $OutDir '_selftest') -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $fake 'Resources\Binaries\Windows') | Out-Null
    foreach ($n in $ExeNames) { Copy-Item (Join-Path $OriginalExeDir $n) (Join-Path $fake 'Resources') -Force }
    Copy-Item $OriginalMono (Join-Path $fake 'Resources\Binaries\Windows') -Force

    foreach ($set in @('1', '2')) {
        $pkg = if ($set -eq '1') { $p1 } else { $p2 }
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'tools\install.ps1') -PatchSet $set -GameRoot $fake | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ('自检失败：补丁' + $set + ' 的就地安装脚本返回 ' + $LASTEXITCODE) }
    }

    $pairs = @(
        @((Join-Path $fake 'Resources\Binaries\Windows\MonoGame.Framework.dll'), (Join-Path $VerifyAgainst 'Binaries\Windows\MonoGame.Framework.dll')),
        @((Join-Path $fake 'Resources\MoLanRelay.dll'),        (Join-Path $VerifyAgainst 'MoLanRelay.dll')),
        @((Join-Path $fake 'Resources\Binaries\MoLanRelay.dll'), (Join-Path $VerifyAgainst 'Binaries\MoLanRelay.dll'))
    )
    foreach ($n in $ExeNames) { $pairs += ,@((Join-Path $fake ('Resources\' + $n)), (Join-Path $VerifyAgainst $n)) }

    $bad = 0
    foreach ($pr in $pairs) {
        $a = $pr[0]; $b = $pr[1]
        if (!(Test-Path $a)) { Write-Host ("  [缺失] {0}" -f (Split-Path $a -Leaf)); $bad++; continue }
        if (!(Test-Path $b)) { Write-Host ("  [跳过] {0}（成品不存在）" -f (Split-Path $a -Leaf)); continue }
        $ha = (Get-FileHash $a).Hash; $hb = (Get-FileHash $b).Hash
        if ($ha -eq $hb) { Write-Host ("  {0,-32} 一致" -f (Split-Path $a -Leaf)) }
        else { Write-Host ("  {0,-32} 不一致 <-- 注意" -f (Split-Path $a -Leaf)); $bad++ }
    }

    # 还原：跑一遍 restore，确认文件回到原版
    foreach ($set in @('1', '2')) {
        $pkg = if ($set -eq '1') { $p1 } else { $p2 }
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'tools\restore.ps1') -GameRoot $fake | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ('自检失败：补丁' + $set + ' 的还原脚本返回 ' + $LASTEXITCODE) }
    }
    $restoreOk = $true
    foreach ($n in $ExeNames) {
        if ((Get-FileHash (Join-Path $fake ('Resources\' + $n))).Hash -ne (Get-FileHash (Join-Path $OriginalExeDir $n)).Hash) { $restoreOk = $false }
    }
    if ((Get-FileHash (Join-Path $fake 'Resources\Binaries\Windows\MonoGame.Framework.dll')).Hash -ne (Get-FileHash $OriginalMono).Hash) { $restoreOk = $false }
    if (Test-Path (Join-Path $fake 'Resources\MoLanRelay.dll')) { $restoreOk = $false }
    Write-Host ('  还原自检: ' + $(if ($restoreOk) { '三个 exe + MonoGame 都回到原版，新增的 MoLanRelay.dll 已删除  [OK]' } else { '失败 <-- 注意' }))
    if (!$restoreOk) { $bad++ }

    if ($bad -gt 0) { Write-Host ('自检有 ' + $bad + ' 项不一致，请检查。') }
    else { Write-Host '自检全部通过。' }
}

Write-Host ''
Write-Host ("完成：`n  {0}`n  {1}" -f $z1, $z2)
Write-Host '（包内只含本项目的工具与文档，不含任何来自 MO / CnCNet 客户端的二进制。）'
