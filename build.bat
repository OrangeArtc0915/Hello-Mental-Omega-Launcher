@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem ============================================================
rem   HMOL 发行构建
rem   用法：build.bat [--skip-obfuscate] [--skip-sign]
rem     --skip-obfuscate  跳过混淆，出一个未混淆的版本（调试用）
rem     --skip-sign       跳过代码签名（签名需要 签名\ 目录里的证书与 signtool）
rem ============================================================

set "DISTDIR=publish"
rem 变量名不能叫 OUTDIR：bat 的 set 会写进环境变量，而 MSBuild 会把环境变量当属性读，
rem OUTDIR 会被当成 OutDir，编译产物就会被丢到各项目目录下的 publish\ 里。
rem 同理 OutDir 用绝对路径：相对路径会让 MSBuild 在每个项目目录下各解析一次。
set "DISTABS=%~dp0publish"
set "APPINFO=src\HMOL.Core\App\AppInfo.cs"
set "CSPROJ=src\HMOL.App\HMOL.App.csproj"
set "BINDIR=%~dp0src\HMOL.App\bin\Release\net8.0-windows\win-x64"
rem 打单文件包时各程序集的来源并不都在 App\bin（实测）：HMOL.dll 来自 App 的 obj，
rem HMOL.Core.dll 来自 Core 的 bin。混淆产物必须分发回这些位置，否则 exe 里还是明文。
set "APPOBJDIR=%~dp0src\HMOL.App\obj\Release\net8.0-windows\win-x64"
set "COREBINDIR=%~dp0src\HMOL.Core\bin\Release\net8.0-windows"
set "COREOBJDIR=%~dp0src\HMOL.Core\obj\Release\net8.0-windows"
set "OBFDIRS=%APPOBJDIR%;%COREBINDIR%;%COREOBJDIR%"
set "OBFDIR=%~dp0obj\obfuscated"
set "PREPDIR=%~dp0obj\prepublish"
set "OBFUSCATE=1"
set "SIGN=1"
set "SIGNED=0"
rem 代码签名：证书、私钥与 signtool 都在仓库根的 签名\ 目录（该目录不入库，见 .gitignore）。
rem PFX 口令不写进本脚本：优先读环境变量 HMOL_PFX_PASSWORD，没有就停下来交互输入。
set "SIGNDIR=%~dp0签名"
set "SIGNPFX=%SIGNDIR%\HMOL-mmm.pfx"
set "SIGNTOOL=%SIGNDIR%\signtool.exe"

:parseArgs
if "%~1"=="" goto :argsDone
if /i "%~1"=="--skip-obfuscate" set "OBFUSCATE=0"
if /i "%~1"=="--skip-sign" set "SIGN=0"
shift
goto :parseArgs
:argsDone

echo ============================================
echo   HMOL - 发行构建
echo ============================================
echo.

call "%~dp0ensure-dotnet-sdk.bat"
if errorlevel 1 goto :failed

rem ---------- 读取版本号（唯一来源：AppInfo.cs）----------
powershell -NoProfile -Command "$m=[regex]::Match((Get-Content -Raw '%APPINFO%'), 'public const string Version = .([0-9]+[.][0-9]+[.][0-9]+).'); if($m.Success){$m.Groups[1].Value}" > "_version.tmp"
set /p VERSION=<"_version.tmp"
del /f /q "_version.tmp" 2>nul

if not defined VERSION (
    echo [错误] 没能从 %APPINFO% 里读到版本号，请检查该文件是否被改动过。
    goto :failed
)
set "ZIPNAME=HMOL-v%VERSION%-win-x64.zip"
echo 版本号：%VERSION%
echo.

rem ---------- 1/7 清理 ----------
rem 每次都清 bin 与 obj：一是保证 app.manifest 的改动能被重新嵌进 apphost
rem （清单改了但增量构建不重新嵌入，会得到"能编译但双击打不开"的 exe），
rem 二是避免上一次混淆过的程序集残留在 bin 里被下一次构建复用。
echo [1/7] 清理上次产物与中间产物...
if exist "%DISTDIR%\HMOL.exe" del /f /q "%DISTDIR%\HMOL.exe"
if exist "%DISTDIR%\*.zip" del /f /q "%DISTDIR%\*.zip"
if exist "%DISTDIR%\_pkg" rmdir /s /q "%DISTDIR%\_pkg"
if exist "src\HMOL.App\bin" rmdir /s /q "src\HMOL.App\bin"
if exist "src\HMOL.App\obj" rmdir /s /q "src\HMOL.App\obj"
if exist "src\HMOL.App\publish" rmdir /s /q "src\HMOL.App\publish"
if exist "src\HMOL.Core\bin" rmdir /s /q "src\HMOL.Core\bin"
if exist "src\HMOL.Core\obj" rmdir /s /q "src\HMOL.Core\obj"
if exist "src\HMOL.Core\publish" rmdir /s /q "src\HMOL.Core\publish"

rem ---------- 2/7 预发布 ----------
rem 这一步不能用 dotnet build：singlefilehost.exe 只在带 PublishSingleFile 的 publish
rem 里才会被放进 obj，光 build 不生成它，第 4 步 --no-build 的 publish 就会报
rem "Could not find file ... singlefilehost.exe"。这里的产物只用来把 obj 填好，
rem 真正发货的是第 4 步覆盖生成的混淆版，所以输出丢到 obj\prepublish 当临时目录。
echo [2/7] 预发布（首次执行需下载运行时，请耐心等待）...
dotnet publish "%CSPROJ%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:Version=%VERSION% ^
    -o "%PREPDIR%" ^
    --nologo
if errorlevel 1 goto :failed

rem ---------- 3/7 混淆 ----------
rem 只能混淆托管程序集：最终的单文件 exe 是原生 apphost，混淆器读不了
if "%OBFUSCATE%"=="1" (
    echo.
    echo [3/7] 混淆 HMOL.dll + HMOL.Core.dll...
    powershell -NoProfile -ExecutionPolicy Bypass -File "build-tools\confuserex\obfuscate.ps1" -BaseDir "%BINDIR%" -WorkDir "%OBFDIR%" -Modules "HMOL.dll,HMOL.Core.dll" -DistributeTo "%OBFDIRS%"
    if errorlevel 1 goto :failed
) else (
    echo.
    echo [3/7] 已按 --skip-obfuscate 跳过混淆
)

rem ---------- 4/7 打单文件 exe ----------
rem --no-build 是必须的：要复用第 2 步编译、第 3 步混淆过的程序集，不能再重新编译一遍
echo.
echo [4/7] 打包自包含单文件 exe...
dotnet publish "%CSPROJ%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:Version=%VERSION% ^
    --no-build ^
    -o "%DISTABS%" ^
    --nologo
if errorlevel 1 goto :failed

if not exist "%DISTDIR%\HMOL.exe" (
    echo [错误] 没有生成 %DISTDIR%\HMOL.exe
    goto :failed
)

rem ---------- 5/7 代码签名 ----------
rem 必须赶在组装发行包之前签：zip 里装的就是这个 exe，签完再打包才不用重做一遍。
rem 证书是自签的（CN=mmm），别的机器上仍会提示未知发布者；要消掉提示，得把 签名\HMOL-mmm.cer
rem 装进那台机器的「受信任的根证书颁发机构」与「受信任的发布者」（需管理员）。
echo.
if "%SIGN%"=="0" (
    echo [5/7] 已按 --skip-sign 跳过代码签名
    goto :signed
)

if not exist "%SIGNTOOL%" (
    echo [5/7] [警告] 没找到 %SIGNTOOL%，跳过签名
    goto :signed
)
if not exist "%SIGNPFX%" (
    echo [5/7] [警告] 没找到 %SIGNPFX%，跳过签名
    goto :signed
)

echo [5/7] 代码签名 %DISTDIR%\HMOL.exe ...
if not defined HMOL_PFX_PASSWORD (
    set /p "HMOL_PFX_PASSWORD=  请输入 PFX 口令（%SIGNPFX%）："
)
if not defined HMOL_PFX_PASSWORD (
    echo [警告] 没有输入口令，跳过签名
    goto :signed
)

"%SIGNTOOL%" sign /f "%SIGNPFX%" /p "%HMOL_PFX_PASSWORD%" /fd sha256 /tr http://timestamp.digicert.com /td sha256 "%DISTDIR%\HMOL.exe"
if errorlevel 1 (
    echo [警告] 签名失败，发行包里将是一个不带签名的 exe
    goto :signed
)
set "SIGNED=1"

"%SIGNTOOL%" verify /pa "%DISTDIR%\HMOL.exe"
if errorlevel 1 echo [警告] 签名链校验未通过 —— 自签根没装进本机受信任存储时属正常，装了就没事

:signed

rem ---------- 6/7 清掉被混淆污染的程序集 ----------
echo.
echo [6/7] 清理中间产物...
if exist "src\HMOL.App\bin" rmdir /s /q "src\HMOL.App\bin"
if exist "src\HMOL.App\obj" rmdir /s /q "src\HMOL.App\obj"
if exist "src\HMOL.Core\bin" rmdir /s /q "src\HMOL.Core\bin"
if exist "src\HMOL.Core\obj" rmdir /s /q "src\HMOL.Core\obj"
if exist "%OBFDIR%" rmdir /s /q "%OBFDIR%"
if exist "%PREPDIR%" rmdir /s /q "%PREPDIR%"

rem ---------- 7/7 组装发行包 ----------
echo.
echo [7/7] 组装发行包...
set "PKG=%DISTDIR%\_pkg"
mkdir "%PKG%" 2>nul
copy /y "%DISTDIR%\HMOL.exe" "%PKG%\HMOL.exe" >nul
if exist "README.md" copy /y "README.md" "%PKG%\README.md" >nul
if exist "NOTICE"    copy /y "NOTICE"    "%PKG%\NOTICE"    >nul
if exist "LICENSE"   copy /y "LICENSE"   "%PKG%\LICENSE"   >nul

rem 公钥证书不随包分发：签名后的 exe 里已经带着证书，发行包里不再放 .cer 散文件。
rem 私钥只在 %SIGNPFX%，绝不随包分发。

rem runtime\ 不再随包分发：组网组件（EasyTier / n2n / TAP / WinIPBroadcast）与 7-Zip
rem 改为运行时按需下载（仓库 Resources 分支的「HMOL Online Required Files」目录），
rem 解压回 exe 旁的 runtime\。首次启动会提示下载 7-Zip，联机时按方案下载组网组件。
rem 本地保留 runtime\ 只用于开发调试，不再进发行包。

powershell -NoProfile -Command "Compress-Archive -Path '%PKG%\*' -DestinationPath '%DISTDIR%\%ZIPNAME%' -Force"
if errorlevel 1 goto :failed
rmdir /s /q "%PKG%"

echo.
echo ============================================
echo   完成
echo ============================================
echo.
echo   单文件 exe : %cd%\%DISTDIR%\HMOL.exe
echo   发行压缩包 : %cd%\%DISTDIR%\%ZIPNAME%
if "%OBFUSCATE%"=="1" (echo   混淆状态   : 已混淆 HMOL.dll + HMOL.Core.dll) else (echo   混淆状态   : 未混淆（--skip-obfuscate）)
if "%SIGNED%"=="1" (echo   签名状态   : 已签名（自签证书 CN=mmm，带 DigiCert 时间戳）) else (echo   签名状态   : 未签名)
echo.
echo 提示：exe 已自带 .NET 8 运行时，拷到任意 Windows 10 1809+ / 11 x64 上双击即可运行。
echo       %DISTDIR%\Data 是运行时数据（设置、实例、日志），本次构建未做改动。
echo.
pause
exit /b 0

:failed
echo.
echo 构建失败，请查看上面的错误信息。
if exist "%DISTDIR%\_pkg" rmdir /s /q "%DISTDIR%\_pkg"
if exist "src\HMOL.App\bin" rmdir /s /q "src\HMOL.App\bin"
if exist "src\HMOL.App\obj" rmdir /s /q "src\HMOL.App\obj"
if exist "src\HMOL.Core\bin" rmdir /s /q "src\HMOL.Core\bin"
if exist "src\HMOL.Core\obj" rmdir /s /q "src\HMOL.Core\obj"
if exist "%OBFDIR%" rmdir /s /q "%OBFDIR%"
if exist "%PREPDIR%" rmdir /s /q "%PREPDIR%"
pause
exit /b 1
