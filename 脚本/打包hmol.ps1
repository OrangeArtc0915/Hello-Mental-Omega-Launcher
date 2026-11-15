<#
    把补丁发行包打成 .HMOL（格式 v1）

    两种输入方式，二选一：
        -ZipPath   <已有 zip>      直接转换
        -SourceDir <目录>          先临时压成 zip 再转换（发布目录里不会留下 zip）

    用法：
        powershell -ExecutionPolicy Bypass -File 打包hmol.ps1 `
            -SourceDir "d:\MO\CNC\_build\MO-补丁1+2-直接覆盖" `
            -OutFile   "d:\MO\CNC\发布\MO-补丁1+2-直接覆盖-2026-11-07.hmol" `
            -PackageName "MO-补丁1+2-直接覆盖" -PackageVersion "0.4.14"

    格式说明见 发布\HMOL包格式与解密说明.md
#>
[CmdletBinding()]
param(
    [string]$ZipPath = "",
    [string]$SourceDir = "",
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$PackageName = "",
    [string]$PackageVersion = "0.0.0",
    [string]$Title = ""
)

$ErrorActionPreference = 'Stop'

$toolDir = [string](Resolve-Path (Join-Path $PSScriptRoot '..\打包工具\HmolPack'))
$tool = Join-Path $toolDir 'bin\Release\net8.0\HmolPack.exe'

if (!(Test-Path $tool)) {
    Write-Host '编译 HmolPack ...'
    & dotnet build (Join-Path $toolDir 'HmolPack.csproj') -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '编译 HmolPack 失败' }
}

$tempZip = $null
try {
    if ($SourceDir) {
        if (!(Test-Path $SourceDir)) { throw ('目录不存在: ' + $SourceDir) }
        $tempZip = Join-Path $env:TEMP ('hmol_' + [Guid]::NewGuid().ToString('N') + '.zip')
        Compress-Archive -Path (Join-Path $SourceDir '*') -DestinationPath $tempZip -Force
        $ZipPath = $tempZip
    }

    if (!$ZipPath) { throw '需要 -ZipPath 或 -SourceDir 之一' }
    if (!(Test-Path $ZipPath)) { throw ('zip 不存在: ' + $ZipPath) }

    if (!$PackageName) { $PackageName = [IO.Path]::GetFileNameWithoutExtension($OutFile) }

    $outFull = [IO.Path]::GetFullPath($OutFile)
    $outDir = Split-Path $outFull -Parent
    if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

    $cli = @('pack', $ZipPath, $outFull, '--name', $PackageName, '--version', $PackageVersion)
    if ($Title) { $cli += @('--title', $Title) }

    & $tool @cli
    if ($LASTEXITCODE -ne 0) { throw '打包失败' }

    Write-Host ''
    & $tool verify $outFull
    if ($LASTEXITCODE -ne 0) { throw '自检失败' }
}
finally {
    if ($tempZip -and (Test-Path $tempZip)) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
}
