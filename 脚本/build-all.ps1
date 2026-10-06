<#
    一键从「原版文件」重建两个补丁包：
        补丁1-中文输入  = Resources\Binaries\Windows\MonoGame.Framework.dll   （工具模式: ime）
        补丁2-联机      = Resources\{clientdx,clientogl,clientxna}.exe          （工具模式: lanip → relay → relaysend → relayhost）
                        + Resources\MoLanRelay.dll + Resources\Binaries\MoLanRelay.dll（C# 编译）

    流水线的每一步都用「原版→成品」逐字节比对验证过（见 构建说明.md）。

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
    # ---- 输入：原版（未打补丁）文件 ----
    [string]$OriginalExeDir = "d:\F\MO\CNC\_lanipbak",                                        # clientdx/ogl/xna.exe
    [string]$OriginalMono    = "d:\F\MO\CNC\Mental Omega\backup-original\Windows\MonoGame.Framework.dll",

    # ---- 输入：源码 ----
    [string]$PatcherProj = "",       # 默认 ..\IL补丁工具\dnlibpatch.csproj
    [string]$RelaySource = "",       # 默认 ..\中继\MoLanRelay.cs
    [string]$NotesDir    = "",       # 默认 ..\说明文本

    # ---- 工具链 ----
    [string]$DotNet       = "d:\F\MO\CNC\.dotnet10\dotnet.exe",
    [string]$Csc          = "d:\F\MO\CNC\.dotnet10\sdk\10.0.100\Roslyn\bincore\csc.dll",
    [string]$FxDir        = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
    [string]$RelayRefDir  = "d:\F\MO\CNC\Mental Omega\Resources\Binaries\Windows",

    # ---- 输出 ----
    [string]$OutDir = "",            # 默认 ..\..\_build

    # ---- 可选：构建后与「当前已部署」的文件比对哈希，验证是否一致 ----
    [string]$VerifyAgainst = "d:\F\MO\CNC\Mental Omega\Resources"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrEmpty($PatcherProj)) { $PatcherProj = Join-Path $root 'IL补丁工具\dnlibpatch.csproj' }
if ([string]::IsNullOrEmpty($RelaySource)) { $RelaySource = Join-Path $root '中继\MoLanRelay.cs' }
if ([string]::IsNullOrEmpty($NotesDir))    { $NotesDir    = Join-Path $root '说明文本' }
if ([string]::IsNullOrEmpty($OutDir))      { $OutDir      = Join-Path $root '..\..\_build' }

$ExeNames    = @('clientdx.exe', 'clientogl.exe', 'clientxna.exe')
$PatchModes  = @('lanip', 'relay', 'relaysend', 'relayhost', 'lobbyfix', 'kick', 'mention', 'shot', 'hashcheck')

$p1   = Join-Path $OutDir '补丁1-中文输入'
$p2   = Join-Path $OutDir '补丁2-联机'
$work = Join-Path $OutDir '_work'
foreach ($d in @($p1, $p2, $work)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# ------------------------------------------------------------------
# 0) 编译 IL 补丁工具
# ------------------------------------------------------------------
Write-Host '[1/5] 编译 IL 补丁工具 ...'
$tools = Join-Path $OutDir '_tools'
& $DotNet build $PatcherProj -c Release -o $tools --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '编译 IL 补丁工具失败' }
$patcher = Join-Path $tools 'dnlibpatch.dll'

function Invoke-Patch([string]$In, [string]$Out, [string]$Mode) {
    & $DotNet $patcher $In $Out $Mode
    if ($LASTEXITCODE -ne 0) { throw "dnlibpatch $Mode 失败: $In" }
}

# ------------------------------------------------------------------
# 1) 编译中继 DLL
# ------------------------------------------------------------------
Write-Host '[2/5] 编译中继 MoLanRelay.dll ...'
$relayDll = Join-Path $work 'MoLanRelay.dll'
$refs = @((Join-Path $FxDir 'mscorlib.dll'), (Join-Path $FxDir 'System.dll'), (Join-Path $FxDir 'System.Core.dll'),
          (Join-Path $FxDir 'System.Drawing.dll'), (Join-Path $FxDir 'System.Windows.Forms.dll'),
          (Join-Path $OriginalExeDir 'clientxna.exe'))
$refs += (Get-ChildItem (Join-Path $RelayRefDir '*.dll') | ForEach-Object { $_.FullName })
$ca = @($Csc, '-noconfig', '-nostdlib+', '-target:library', "-out:$relayDll", '-platform:anycpu', '-optimize+', '-deterministic+')
$ca += ($refs | ForEach-Object { "-reference:$_" })
$ca += $RelaySource
& $DotNet @ca
if ($LASTEXITCODE -ne 0) { throw '编译中继 DLL 失败' }

# ------------------------------------------------------------------
# 2) 补丁1：中文输入（只一个文件）
# ------------------------------------------------------------------
Write-Host '[3/5] 生成 补丁1-中文输入 ...'
$mgOut = Join-Path $p1 'Resources\Binaries\Windows\MonoGame.Framework.dll'
$mgBak = Join-Path $p1 '原版备份\Resources\Binaries\Windows\MonoGame.Framework.dll'
New-Item -ItemType Directory -Force -Path (Split-Path $mgOut -Parent), (Split-Path $mgBak -Parent) | Out-Null
Invoke-Patch $OriginalMono $mgOut 'ime'
Copy-Item $OriginalMono $mgBak -Force

# ------------------------------------------------------------------
# 3) 补丁2：联机（三个 exe + 中继 DLL）
# ------------------------------------------------------------------
Write-Host '[4/5] 生成 补丁2-联机 ...'
New-Item -ItemType Directory -Force -Path (Join-Path $p2 'Resources\Binaries'), (Join-Path $p2 '原版备份\Resources') | Out-Null
foreach ($n in $ExeNames) {
    $cur = Join-Path $OriginalExeDir $n
    $step = 0
    foreach ($m in $PatchModes) {
        $step++
        $next = Join-Path $work ("$n.$step.bin")
        Invoke-Patch $cur $next $m
        $cur = $next
    }
    Copy-Item $cur (Join-Path $p2 "Resources\$n") -Force
    Copy-Item (Join-Path $OriginalExeDir $n) (Join-Path $p2 "原版备份\Resources\$n") -Force
    Write-Host ("      {0} -> {1}" -f $n, ($PatchModes -join ' → '))
}
Copy-Item $relayDll (Join-Path $p2 'Resources\MoLanRelay.dll') -Force
Copy-Item $relayDll (Join-Path $p2 'Resources\Binaries\MoLanRelay.dll') -Force

# ------------------------------------------------------------------
# 4) 说明书 + 打包
# ------------------------------------------------------------------
Write-Host '[5/5] 打包 ...'
$n1 = Join-Path $NotesDir '补丁1-安装说明.txt'
$n2 = Join-Path $NotesDir '补丁2-安装说明.txt'
if (Test-Path $n1) { Copy-Item $n1 (Join-Path $p1 '安装说明.txt') -Force }
if (Test-Path $n2) { Copy-Item $n2 (Join-Path $p2 '安装说明.txt') -Force }

$z1 = Join-Path $OutDir 'MO-补丁1-中文输入.zip'
$z2 = Join-Path $OutDir 'MO-补丁2-联机.zip'
Remove-Item $z1, $z2 -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $p1 '*') -DestinationPath $z1 -Force
Compress-Archive -Path (Join-Path $p2 '*') -DestinationPath $z2 -Force

# ------------------------------------------------------------------
# 5) 可选自检：与已部署文件比哈希
# ------------------------------------------------------------------
if ($VerifyAgainst -and (Test-Path $VerifyAgainst)) {
    Write-Host ''
    Write-Host '== 与已部署文件比对 =='
    $pairs = @(
        @($mgOut,        (Join-Path $VerifyAgainst 'Binaries\Windows\MonoGame.Framework.dll')),
        @((Join-Path $p2 'Resources\MoLanRelay.dll'),        (Join-Path $VerifyAgainst 'MoLanRelay.dll')),
        @((Join-Path $p2 'Resources\Binaries\MoLanRelay.dll'), (Join-Path $VerifyAgainst 'Binaries\MoLanRelay.dll'))
    )
    foreach ($n in $ExeNames) { $pairs += ,@((Join-Path $p2 "Resources\$n"), (Join-Path $VerifyAgainst $n)) }
    foreach ($pr in $pairs) {
        $a = $pr[0]; $b = $pr[1]
        if (!(Test-Path $b)) { Write-Host ("  [跳过] {0}（已部署文件不存在）" -f (Split-Path $a -Leaf)); continue }
        $ha = (Get-FileHash $a).Hash; $hb = (Get-FileHash $b).Hash
        Write-Host ("  {0,-32} {1}" -f (Split-Path $a -Leaf), $(if ($ha -eq $hb) { '一致' } else { '不一致 <-- 注意' }))
    }
}

Write-Host ''
Write-Host ("完成：`n  {0}`n  {1}" -f $z1, $z2)
