@echo off
chcp 65001 >nul
setlocal

rem 供 build.bat / 一键编译并运行.bat 调用：确保系统里有 .NET 8 或更高版本的 SDK
rem 退出码：0 = 可用，1 = 不可用

where dotnet >nul 2>nul || set "PATH=%ProgramFiles%\dotnet;%PATH%"
set "HERE=%~dp0"

call :detect
if defined SDK_OK (
    echo [环境] 已检测到 .NET SDK：
    dotnet --list-sdks
    echo.
    exit /b 0
)

echo [环境] 未检测到 .NET 8 或更高版本的 SDK。

set "SDK_EXE="
for %%F in ("%HERE%dotnet-sdk-*.exe" "%USERPROFILE%\Downloads\dotnet-sdk-*-win-x64.exe" "%TEMP%\dotnet-sdk-*-win-x64.exe") do if exist "%%~fF" set "SDK_EXE=%%~fF"
if not defined SDK_EXE goto :tryDownload

set "DL=%SDK_EXE%"
set "SDK_EXE="
call :acceptPkg
if not defined SDK_EXE goto :tryDownload

echo [环境] 找到本地安装包：%SDK_EXE%
goto :install

:tryDownload
echo [环境] 未找到可用的本地安装包，尝试自动下载 .NET 8 SDK（约 215 MB）...
call :download
if not defined SDK_EXE goto :needManual

:install
echo [环境] 安装包：%SDK_EXE%
echo [环境] 安装需要管理员权限，稍后会弹出 UAC 授权窗口，请点“是”。
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $p = Start-Process -FilePath '%SDK_EXE%' -ArgumentList '/install','/quiet','/norestart' -Verb RunAs -Wait -PassThru; exit $p.ExitCode } catch { Write-Host ('[环境] 安装失败：' + $_.Exception.Message); exit 1 }"

call :detect
if not defined SDK_OK goto :installFailed

echo [环境] .NET SDK 安装完成：
dotnet --list-sdks
echo.
exit /b 0

rem ---------- 自动下载（微软官方链路，国内可能不通）----------
:download
set "SDK_EXE="
where curl.exe >nul 2>nul
if errorlevel 1 goto :eof
set "DL=%TEMP%\dotnet-sdk-8-win-x64.exe"

echo [环境] 下载源 1/2：aka.ms
curl.exe -L -C - --connect-timeout 20 --retry 3 -o "%DL%" "https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe"
call :acceptPkg
if defined SDK_EXE goto :eof

echo [环境] 下载源 2/2：builds.dotnet.microsoft.com
curl.exe -L -C - --connect-timeout 20 --retry 3 -o "%DL%" "https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.exe"
call :acceptPkg
if defined SDK_EXE goto :eof

del /f /q "%DL%" 2>nul
goto :eof

rem ---------- 校验安装包大小，合格才记入 SDK_EXE ----------
:acceptPkg
set "SDK_EXE="
set "DLSIZE="
for %%A in ("%DL%") do set "DLSIZE=%%~zA"
if not defined DLSIZE goto :eof
if %DLSIZE% LSS 100000000 (
    del /f /q "%DL%" 2>nul
    goto :eof
)
set "SDK_EXE=%DL%"
goto :eof

rem ---------- 检测已装 SDK 的主版本是否 >= 8 ----------
:detect
set "SDK_OK="
for /f "tokens=1 delims=. " %%V in ('dotnet --list-sdks 2^>nul') do if %%V GEQ 8 set "SDK_OK=1"
exit /b 0

:needManual
echo.
echo [环境] 自动获取 .NET 8 SDK 安装包失败：这台机器到微软 CDN 的链路不通。
echo.
echo         请手动下载（约 215 MB，8.0.425 版为 225549280 字节）：
echo           https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe
echo         下载页（选 .NET 8.0 SDK - Windows x64）：
echo           https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
echo.
echo         下载完成后把 dotnet-sdk-8.0.xxx-win-x64.exe 放到：
echo           %HERE%
echo         再重新运行本脚本，会自动识别并静默安装。
echo.
exit /b 1

:installFailed
echo.
echo [环境] 安装包已执行，但仍未检测到 .NET 8 SDK。
echo         请确认 UAC 弹窗点的是“是”，且安装包完整，然后重新运行本脚本。
echo.
exit /b 1
