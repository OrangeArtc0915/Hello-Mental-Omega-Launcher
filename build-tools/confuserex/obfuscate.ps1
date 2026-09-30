# HMOL 发行构建用的混淆步骤。由 build.bat 调用，不单独使用。
#
# 为什么不能直接混淆最终的单文件 exe：
#   自包含单文件 exe 是原生 apphost，托管程序集被塞在 bundle 里，
#   ConfuserEx 会报 "Assembly does not appear to be a .NET assembly"。
#   所以必须在「编译完成之后、单文件打包之前」对托管程序集做混淆。
#
# 为什么混淆完还要 -DistributeTo 分发：
#   dotnet publish 打单文件包时，各程序集的来源不是同一处（实测）：
#     HMOL.dll      读自 src\HMOL.App\obj\...\win-x64\HMOL.dll
#     HMOL.Core.dll 读自 src\HMOL.Core\bin\...\HMOL.Core.dll
#   只改 bin 里的副本，打出来的 exe 仍是未混淆的。所以混淆后要把产物覆盖回
#   所有会被读取的位置，保证各处都是同一份（同一次混淆的同一套重命名映射）。
#
# 工具来源：ConfuserEx 1.6.0（mkaring/ConfuserEx，MIT），CLI 版，随仓库放在 cli\ 下。
#
# 注意：本文件必须保存为「UTF-8 带 BOM」，否则 Windows PowerShell 会按 ANSI 解码，
# 中文注释会把行冲掉导致脚本语法报错。
param(
    [Parameter(Mandatory = $true)][string]$BaseDir,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [Parameter(Mandatory = $true)][string]$Modules,
    [string]$DistributeTo = ''
)

$ErrorActionPreference = 'Stop'

# 用 -File 方式调用时逗号不会被解析成数组，所以这里自己拆
$moduleList = @($Modules -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($moduleList.Count -eq 0) {
    Write-Host '[混淆] 没有指定任何模块'
    exit 1
}

$cli = Join-Path $PSScriptRoot 'cli\Confuser.CLI.exe'
if (-not (Test-Path $cli)) {
    Write-Host "[混淆] 找不到 $cli"
    exit 1
}

if (-not (Test-Path $BaseDir)) {
    Write-Host "[混淆] 编译输出目录不存在：$BaseDir"
    exit 1
}

foreach ($module in $moduleList) {
    if (-not (Test-Path (Join-Path $BaseDir $module))) {
        Write-Host "[混淆] $BaseDir 下找不到 $module"
        exit 1
    }
}

if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# 防调试 + 常量加密 + 控制流混淆 + 引用代理 + 资源加密 + 重命名
# 关于重命名强度（实测结论，别轻易改）：
#   ConfuserEx 默认不重命名 public 成员，所以在当前配置下 HMOL.dll 的符号被改名，
#   但 HMOL.Core.dll 的 public 类型与方法名会原样留在元数据里。
#   加 <argument name="renPublic" value="true" /> 想把它一起改掉会直接失败：
#   "Infinite loop detected while resolving name references"
#   （ConfuserEx 1.6.0 在 WPF 应用上开 public 重命名的已知问题，日志里会先打印
#    "WPF found, enabling compatibility."）。
#   若要更强的保护，需要换混淆器（如 .NET Reactor），不要在这里瞎试参数。
$moduleTags = ($moduleList | ForEach-Object { "  <module path=`"$_`" />" }) -join "`r`n"
$crproj = @"
<project outputDir="$WorkDir" baseDir="$BaseDir" xmlns="http://confuser.codeplex.com">
  <rule pattern="true" preset="none" inherit="false">
    <protection id="anti debug" />
    <protection id="constants" />
    <protection id="ctrl flow" />
    <protection id="ref proxy" />
    <protection id="resources" />
    <protection id="rename" />
  </rule>
$moduleTags
</project>
"@

$projFile = Join-Path $WorkDir 'hmol.crproj'
Set-Content -Path $projFile -Value $crproj -Encoding UTF8

Write-Host "[混淆] 处理：$($moduleList -join ', ')"
& $cli -n $projFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "[混淆] 失败，退出码 $LASTEXITCODE"
    exit 1
}

foreach ($module in $moduleList) {
    if (-not (Test-Path (Join-Path $WorkDir $module))) {
        Write-Host "[混淆] 未生成 $module"
        exit 1
    }
}

# 1) 回填混淆源目录
foreach ($module in $moduleList) {
    Copy-Item (Join-Path $WorkDir $module) (Join-Path $BaseDir $module) -Force
}

# 2) 分发到其它会被读取的位置（该位置存在同名文件才覆盖）
$extraDirs = @($DistributeTo -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($dir in $extraDirs) {
    if (-not (Test-Path $dir)) { continue }

    foreach ($module in $moduleList) {
        $target = Join-Path $dir $module
        if (-not (Test-Path $target)) { continue }

        Copy-Item (Join-Path $WorkDir $module) $target -Force
        Write-Host "[混淆] 已分发到 $target"
    }
}

Write-Host '[混淆] 完成'
exit 0
