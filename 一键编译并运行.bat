@echo off
chcp 65001 >nul
cd /d "%~dp0"

call "%~dp0ensure-dotnet-sdk.bat"
if errorlevel 1 (
    echo.
    echo 编译环境准备失败，请按上面的提示处理后重试。
    pause
    exit /b 1
)

echo [1/2] 正在编译 HMOL（首次编译需联网还原依赖包，耗时较长）...
dotnet build src\HMOL.App\HMOL.App.csproj -c Debug --nologo
if errorlevel 1 (
    echo.
    echo 编译失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo [2/2] 正在启动...
start "" "src\HMOL.App\bin\Debug\net8.0-windows\HMOL.exe"
