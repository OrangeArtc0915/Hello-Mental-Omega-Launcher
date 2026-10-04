param([string]$folder, [string]$asmFile, [string]$typeName, [string]$methodName)
try {
    $path = Join-Path $folder $asmFile
    $asm = [Reflection.Assembly]::LoadFrom($path)
    $t = $asm.GetType($typeName)
    if ($null -eq $t) { Write-Host "TYPE-NOT-FOUND"; exit 2 }
    $m = $null
    if ($methodName -eq ".ctor" -or $methodName -eq ".cctor") {
        $m = ($t.GetConstructors([Reflection.BindingFlags]"NonPublic,Instance,Public,Static") | Select-Object -First 1)
    } else {
        $m = ($t.GetMethods([Reflection.BindingFlags]"NonPublic,Instance,Public,Static") | Where-Object { $_.Name -eq $methodName } | Select-Object -First 1)
    }
    if ($null -eq $m) { Write-Host "METHOD-NOT-FOUND"; exit 3 }
    [Runtime.CompilerServices.RuntimeHelpers]::PrepareMethod($m.MethodHandle)
    Write-Host "JIT-OK"
    exit 0
} catch {
    $e = $_.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    Write-Host ("JIT-FAIL: " + $e.GetType().Name + " - " + $e.Message)
    exit 1
}
