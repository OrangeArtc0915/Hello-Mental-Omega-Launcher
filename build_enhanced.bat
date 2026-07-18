@echo off
chcp 65001 >nul
REM ============================================================
REM HMOL Launcher Build Script (v2.2 - Enhanced)
REM ============================================================
REM
REM Same as build.bat but with:
REM   - Bytecode encryption (--key)
REM   - Strict mode (--strict)
REM   - Aborts on security audit failure
REM
REM Suitable for: production releases, anti-tamper builds
REM
REM ============================================================

call build.bat --secure --strict %*
exit /b %errorlevel%
