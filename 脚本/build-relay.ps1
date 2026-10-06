<#
    编译「游戏内 UDP 中继」MoLanRelay.dll

    为什么要这么绕：中继必须是一个 .NET Framework 4.0 的程序集（游戏客户端是老 runtime），
    所以不能直接 dotnet build net48（这台机器上的 SDK 不支持 net48 target），
    改为直接调 Roslyn 的 csc.dll，并显式指定 mscorlib / System / System.Core。

    引用里还必须带上「原版（未打补丁）的 clientxna.exe」—— 它提供 LANPlayerInfo 类型，
    而且打补丁后的 exe 强名称签名已失效，会报 CS0009「公钥无效」。

    用法：
        powershell -ExecutionPolicy Bypass -File build-relay.ps1
    或者指定输出：
        powershell -ExecutionPolicy Bypass -File build-relay.ps1 -Out "D:\x\MoLanRelay.dll"
#>
param(
    [string]$Out      = "",
    # 原版（未打补丁）的客户端 exe，用来提供 LANPlayerInfo 的类型引用
    [string]$RefExe   = "d:\F\MO\CNC\_lanipbak\clientxna.exe",
    # 编译期引用的框架程序集目录
    [string]$FxDir    = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
    # 其余引用（SharpDX / MonoGame / XNAUI 等），只取这个目录可以避免重复程序集报 CS1704
    [string]$RefExtra = "d:\F\MO\CNC\Mental Omega\Resources\Binaries\Windows",
    [string]$DotNet   = "d:\F\MO\CNC\.dotnet10\dotnet.exe",
    [string]$Csc      = "d:\F\MO\CNC\.dotnet10\sdk\10.0.100\Roslyn\bincore\csc.dll"
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot '..\中继\MoLanRelay.cs'
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $PSScriptRoot '..\..\_build\MoLanRelay.dll' }

$outDir = Split-Path $Out -Parent
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$refs = @(
    (Join-Path $FxDir 'mscorlib.dll'),
    (Join-Path $FxDir 'System.dll'),
    (Join-Path $FxDir 'System.Core.dll'),
    (Join-Path $FxDir 'System.Drawing.dll'),
    (Join-Path $FxDir 'System.Windows.Forms.dll'),
    $RefExe
)
$refs += (Get-ChildItem (Join-Path $RefExtra '*.dll') | ForEach-Object { $_.FullName })

$a = @($Csc, '-noconfig', '-nostdlib+', '-target:library', "-out:$Out", '-platform:anycpu', '-optimize+', '-deterministic+')
$a += ($refs | ForEach-Object { "-reference:$_" })
$a += $src

& $DotNet @a
if ($LASTEXITCODE -ne 0) { throw "csc 编译失败（exit $LASTEXITCODE）" }

Write-Host ("OK -> {0}  ({1} bytes)" -f $Out, (Get-Item $Out).Length)
