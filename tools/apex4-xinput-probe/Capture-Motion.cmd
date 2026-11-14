@echo off
cd /d "%~dp0"
echo Close ApexSenseBridge Tray, active bridge sessions, Space Station and games first.
echo Connect only your APEX 4 in XInput mode. Run this script as administrator.
echo This reads the reported profile slot, then guides a 76-second motion capture if enabled/unknown.
Apex4XInputProbe.exe --capture-motion
set "capture_exit=%ERRORLEVEL%"
echo Exit code: %capture_exit%
pause
