@echo off
title LordsBot Local Licensing & Auth Server
echo ========================================================
echo   Starting Local Auth & Licensing Server (Port 5000)
echo ========================================================
cd /d "%~dp0local_server"
if exist server.py (
    python server.py --host 0.0.0.0 --port 5000
) else (
    cd /d "%~dp0"
    python server.py --host 0.0.0.0 --port 5000
)
pause
