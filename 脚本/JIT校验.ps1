param(
    [string]$folder,
    [string]$asmFile,
    [string]$typeName,
    [string]$methodName
)
# 用法（必须用 32 位 PowerShell，游戏 exe 是 32 位）：
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File .\JIT校验.ps1 `
#     -folder "D:\MO\Mental Omega\Resources" -asmFile clientxna.exe `
#     -typeName DTAClient.DXGUI.Multiplayer.GameLobby.LANGameLobby -methodName SendChatMessage
# 依赖（Rampastring.XNAUI / MonoGame 等）从 -folder、<folder>\Binaries\Windows、同目录里找。

$ErrorActionPreference = 'Stop'

$probe = New-Object System.Collections.Generic.List[string]
$probe.Add($folder)
$probe.Add((Join-Path $folder 'Binaries\Windows'))
$probe.Add((Join-Path $folder 'Binaries'))
$probe.Add((Get-Location).Path)

[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler]{
    param($s, $e)
    $simple = ($e.Name -split ',')[0]
    foreach ($p in $probe) {
        foreach ($ext in @('.dll', '.exe')) {
            $f = Join-Path $p ($simple + $ext)
            if (Test-Path $f) { try { return [Reflection.Assembly]::LoadFrom($f) } catch { } }
        }
    }
    return $null
})

if ([IntPtr]::Size -ne 4) {
    Write-Host "WARN: 当前 PowerShell 不是 32 位，加载 32 位 exe 会失败（用 SysWOW64 那个）"
}

try {
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path $folder $asmFile))
    $t = $asm.GetType($typeName)
    if ($null -eq $t) { Write-Host "TYPE-NOT-FOUND"; exit 2 }

    if ($methodName -eq ".ctor" -or $methodName -eq ".cctor") {
        $list = $t.GetConstructors([Reflection.BindingFlags]"NonPublic,Instance,Public,Static")
    } else {
        $list = $t.GetMethods([Reflection.BindingFlags]"NonPublic,Instance,Public,Static") |
                Where-Object { $_.Name -eq $methodName }
    }
    if (-not $list) { Write-Host "METHOD-NOT-FOUND"; exit 3 }

    foreach ($m in @($list)) {
        [Runtime.CompilerServices.RuntimeHelpers]::PrepareMethod($m.MethodHandle)
        $sig = ($m.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ','
        Write-Host ("JIT-OK  {0}({1})" -f $methodName, $sig)
    }
    exit 0
} catch {
    $e = $_.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    Write-Host ("JIT-FAIL: " + $e.GetType().Name + " - " + $e.Message)
    exit 1
}
