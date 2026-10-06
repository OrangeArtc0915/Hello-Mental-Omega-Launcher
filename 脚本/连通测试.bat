@echo off
chcp 65001 >nul
title MO direct-connect test listener
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_connect_test.ps1"
