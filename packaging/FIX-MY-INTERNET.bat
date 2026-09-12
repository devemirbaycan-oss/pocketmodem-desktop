@echo off
REM ---------------------------------------------------------------------------
REM  Emergency recovery.
REM
REM  Run this if PocketModem crashed or was killed and the PC has no internet.
REM  It removes any routes the tunnel left behind and restores normal routing.
REM
REM  Safe to run at any time: if nothing was left behind, it does nothing.
REM ---------------------------------------------------------------------------
setlocal

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting Administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

echo.
echo ==========================================================
echo   PocketModem - restore normal internet routing
echo ==========================================================
echo.

pocketmodem.exe recover

echo.
echo Also clearing any stray tunnel routes...
route delete 0.0.0.0 mask 128.0.0.0 >nul 2>&1
route delete 128.0.0.0 mask 128.0.0.0 >nul 2>&1

echo.
echo Done. Your internet should work normally again.
echo If it still does not, disable and re-enable your Wi-Fi adapter.
echo.
pause
