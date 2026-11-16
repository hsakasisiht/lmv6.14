@echo off
python "%~dp0get_game_version.py" 2>nul
if %errorlevel% neq 0 (
    powershell -ExecutionPolicy Bypass -File "%~dp0get_game_version.ps1"
)
