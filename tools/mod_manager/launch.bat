@echo off
cd /d "%~dp0..\.."
set PYTHONW_EXE=pythonw
if exist "C:\Python314\pythonw.exe" set PYTHONW_EXE=C:\Python314\pythonw.exe
start "" "%PYTHONW_EXE%" "%~dp0main.py" %*
exit
