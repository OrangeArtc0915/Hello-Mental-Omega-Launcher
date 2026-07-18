@echo off
chcp 65001 >nul
REM ============================================================
REM HMOL Launcher Build Script (v2.2)
REM ============================================================
REM
REM Security:
REM   - PyInstaller --key (encrypted bytecode)
REM   - --noupx (no UPX)
REM   - explicit hidden-imports
REM   - pre-build security audit gate
REM   - SHA-256 checksum
REM
REM Usage:
REM   build.bat                    Default build (--onedir)
REM   build.bat --secure           Encrypted bytecode
REM   build.bat --strict           Abort on audit failure
REM   build.bat --onefile          Single-file mode
REM   build.bat --onedir           Directory mode (default)
REM   build.bat --verbose          Verbose output
REM   build.bat --skip-audit       Skip pre-build audit
REM   build.bat --help             Show this help
REM
REM ============================================================

setlocal enabledelayedexpansion
set "ERROR_OCCURRED=0"

cd /d "%~dp0"

set "VERSION=2.2"
set "NAME=HMOL"
set "PYTHON=python"
set "PYINSTALLER=pyinstaller"
set "ENTRY=HMOL_qt.py"

set "USE_SECURE=0"
set "USE_STRICT=0"
set "EXTRA_ARGS="
set "VERBOSE=0"
set "SKIP_AUDIT=0"

:parse_args
if "%~1"=="" goto :end_parse
if /i "%~1"=="--secure"       set "USE_SECURE=1"  & shift & goto :parse_args
if /i "%~1"=="--strict"       set "USE_STRICT=1"  & shift & goto :parse_args
if /i "%~1"=="--onefile"      set "EXTRA_ARGS=--onefile %EXTRA_ARGS%" & shift & goto :parse_args
if /i "%~1"=="--onedir"       set "EXTRA_ARGS=--onedir %EXTRA_ARGS%"  & shift & goto :parse_args
if /i "%~1"=="--verbose"      set "VERBOSE=1"     & shift & goto :parse_args
if /i "%~1"=="--skip-audit"   set "SKIP_AUDIT=1"  & shift & goto :parse_args
if /i "%~1"=="-h"             goto :show_help
if /i "%~1"=="--help"         goto :show_help
echo [WARN] Unknown option: %~1
shift
goto :parse_args
:end_parse

echo ============================================================
echo   HMOL Launcher Build Script v%VERSION%
echo ============================================================
echo   Mode:    secure=%USE_SECURE%, strict=%USE_STRICT%
echo   Output:  %EXTRA_ARGS% (default: --onedir)
echo   Python:  %PYTHON%
echo   PyInstaller: %PYINSTALLER%
echo.

REM Step 0: pre-flight checks
echo [Step 0/5] Running pre-flight checks...

%PYTHON% --version >nul 2>&1
if errorlevel 1 (
    echo   [ERROR] Python not installed or not in PATH
    echo           Install Python 3.10+ and ensure 'python' is available
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
for /f "tokens=2" %%V in ('%PYTHON% --version 2^>^&1') do set "PY_VERSION=%%V"
echo   [OK] Python %PY_VERSION%

%PYTHON% -c "import PyInstaller; print(PyInstaller.__version__)" >nul 2>&1
if errorlevel 1 (
    echo   [ERROR] PyInstaller not installed
    echo           pip install pyinstaller
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
for /f %%V in ('%PYTHON% -c "import PyInstaller; print(PyInstaller.__version__)"') do set "PYI_VERSION=%%V"
echo   [OK] PyInstaller %PYI_VERSION%

%PYINSTALLER% --version >nul 2>&1
if errorlevel 1 (
    echo   [WARN] pyinstaller command unavailable, falling back to 'python -m PyInstaller'
    set "PYINSTALLER=%PYTHON% -m PyInstaller"
)
echo.

if not exist "%ENTRY%" (
    echo   [ERROR] %ENTRY% not found
    echo           Run this script from project root
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
echo   [OK] %ENTRY% found

if not exist "icon.ico" (
    echo   [WARN] icon.ico not found, using default icon
)
echo.

REM Step 1: security audit
if "%SKIP_AUDIT%"=="1" (
    echo [Step 1/5] Skipping security audit --skip-audit
    goto :step2
)
echo [Step 1/5] Running pre-release security audit...
%PYTHON% security_audit.py
set "AUDIT_RC=%errorlevel%"
if !AUDIT_RC! neq 0 (
    echo   [WARN] Security audit found issues - exit !AUDIT_RC!
    if "%USE_STRICT%"=="1" (
        echo   [ABORT] Strict mode refuses to build with security issues
        set "ERROR_OCCURRED=1"
        goto :error_exit
    )
) else (
    echo   [OK] Security audit passed
)
echo.

REM Step 2: cleanup
:step2
echo [Step 2/5] Cleaning old build artifacts...
if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist "%NAME%.spec" del /F /Q "%NAME%.spec" >nul 2>&1
echo   [OK] Build artifacts cleaned
echo.

REM Step 3: prepare PyInstaller args
:step3
echo [Step 3/5] Preparing PyInstaller arguments...

set "PYI_ARGS=--noconfirm --windowed --icon=icon.ico --name %NAME%"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=requests"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=cryptography --hidden-import=cryptography.hazmat.primitives"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=msal"
set "PYI_ARGS=%PYI_ARGS% --collect-all=cryptography"
set "PYI_ARGS=%PYI_ARGS% --noupx"
set "PYI_ARGS=%PYI_ARGS% --clean"

if "%USE_SECURE%"=="1" (
    REM 动态生成字节码加密密钥 (HMOL111.txt FINDING-03)
    REM 之前使用硬编码 "HMOL-2026-BuildKey" 已被任何获得源码者可复现反编译.
    REM 现改为: 优先使用环境变量 PYI_KEY (CI 可复现), 其次每次构建临时生成.
    REM 安全模型: 不同 release 必须使用不同密钥, 否则上一个版本的反编译成本
    REM 会平移到下一个版本——相当于没保护.
    if not defined PYI_KEY (
        for /f "tokens=*" %%T in ('powershell -NoProfile -Command "[System.BitConverter]::ToString((1..16 | ForEach-Object { Get-Random -Maximum 256 }) -replace '-','')"') do set "PYI_KEY=%%T"
    )
    set "PYI_ARGS=%PYI_ARGS% --key=%PYI_KEY%"
    echo   [SECURE] Bytecode encryption: ENABLED
    echo   [SECURE] Build key (record for THIS release only, do not reuse):
    echo             %PYI_KEY%
)

if "%USE_STRICT%"=="1" (
    set "PYI_ARGS=%PYI_ARGS% --noconsole"
    echo   [STRICT] Console disabled
)

if "%EXTRA_ARGS%"=="" set "EXTRA_ARGS=--onedir"

echo   [OK] PyInstaller arguments prepared
echo.

REM Step 4: build
:step4
echo [Step 4/5] Running PyInstaller...
echo   Command: %PYINSTALLER% %PYI_ARGS% %EXTRA_ARGS% %ENTRY%
echo.

%PYINSTALLER% %PYI_ARGS% %EXTRA_ARGS% %ENTRY%
set "BUILD_RC=%errorlevel%"

if !BUILD_RC! neq 0 (
    echo.
    echo   [ERROR] Build FAILED - exit !BUILD_RC!
    echo   Common issues:
    echo     - PyInstaller too old: pip install --upgrade pyinstaller
    echo     - Missing deps: pip install -r requirements.txt
    echo     - Code errors: try running 'python %ENTRY%' first
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
echo.
echo   [OK] PyInstaller build succeeded
echo.

REM Step 5: checksum
:step5
echo [Step 5/5] Computing checksums...
if exist "dist\%NAME%.exe" (
    echo   SHA-256 of dist\%NAME%.exe:
    certutil -hashfile "dist\%NAME%.exe" SHA256 | findstr /v "hash CertUtil"
) else if exist "dist\%NAME%\%NAME%.exe" (
    echo   SHA-256 of dist\%NAME%\%NAME%.exe:
    certutil -hashfile "dist\%NAME%\%NAME%.exe" SHA256 | findstr /v "hash CertUtil"
) else (
    echo   [WARN] No EXE found in dist\
)
echo.

REM Success exit
echo ============================================================
echo   Build SUCCEEDED!
echo ============================================================
echo   Output: dist\%NAME%\ (or dist\%NAME%.exe with --onefile)
echo.
echo   Next Steps:
echo     1. Test the EXE on a clean machine
echo     2. Run: python security_audit.py
echo     3. Sign the EXE with code signing certificate (recommended)
echo ============================================================
echo.
echo   Tip: if your antivirus is screaming at the new exe, it's
echo        a false positive triggered by PyInstaller — sign it or
echo        submit to your AV vendor. Don't disable protection.
echo.

if "%CI%"=="" if "%GITHUB_ACTIONS%"=="" (
    echo Press any key to exit...
    pause >nul
)
endlocal
exit /b 0

REM Error exit
:error_exit
echo.
echo ============================================================
echo   Build FAILED!
echo ============================================================
echo   Please check the error messages above.
echo ============================================================
echo.

if "%CI%"=="" if "%GITHUB_ACTIONS%"=="" (
    echo Press any key to exit...
    pause >nul
)
endlocal
exit /b 1

REM Help
:show_help
echo.
echo HMOL Launcher Build Script v%VERSION%
echo.
echo USAGE:
echo   build.bat [options]
echo.
echo OPTIONS:
echo   (no args)         Default build (no encryption)
echo   --secure          Enable bytecode encryption (requires pyinstaller^=5.13)
echo   --strict          Strict mode (abort on security issues)
echo   --onefile         Single-file mode
echo   --onedir          Directory mode (default)
echo   --verbose         Verbose output
echo   --skip-audit      Skip pre-release security audit
echo   --help, -h        Show this help message
echo.
echo EXAMPLES:
echo   build.bat                      # Standard build
echo   build.bat --secure             # Build with bytecode encryption
echo   build.bat --secure --strict    # Production build
echo   build.bat --onefile --secure   # Single-file encrypted build
echo.
echo OUTPUT:
echo   dist\HMOL\HMOL.exe             # --onedir mode
echo   dist\HMOL.exe                  # --onefile mode
echo.
echo TROUBLESHOOTING:
echo   - PyInstaller not found: pip install pyinstaller
echo   - Crypto not found: pip install cryptography
echo   - Permission errors: run as Administrator
echo   - Antivirus blocking: temporarily disable real-time protection
echo.
endlocal
exit /b 0
