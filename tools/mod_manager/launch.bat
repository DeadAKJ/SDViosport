@echo off
title Stardew Valley iOS Mod Manager
cd /d "%~dp0..\.."
python "%~dp0main.py"
if %errorlevel% neq 0 (
    echo.
    echo Application exited with code %errorlevel%.
    pause
)
