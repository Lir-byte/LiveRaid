@echo off
chcp 65001 >nul
rem Builds "LiveRaid Studio.exe" (one file) with PyInstaller from Tools\pytools and puts it into the game folder.
set PYTHONPATH=D:\Старое\Working Claude\Tools\pytools;%PYTHONPATH%
cd /d "%~dp0"
python -m PyInstaller --noconfirm --onefile --noconsole --name "LiveRaid Studio" --add-data "web;web" --hidden-import make_demo --workpath build --distpath dist server.py
if errorlevel 1 exit /b 1
if not exist "C:\Games\SPT\LiveRaid Studio" mkdir "C:\Games\SPT\LiveRaid Studio"
copy /Y "dist\LiveRaid Studio.exe" "C:\Games\SPT\LiveRaid Studio\LiveRaid Studio.exe"
echo Done: C:\Games\SPT\LiveRaid Studio\LiveRaid Studio.exe
