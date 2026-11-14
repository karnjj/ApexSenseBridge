@echo off
cd /d "%~dp0"
echo Close ApexSenseBridge Tray, active bridge sessions, Space Station and games first.
echo Connect only your APEX 4 in XInput mode. Run this script as administrator.
echo The next screen guides a 48-second capture of stillness, yaw, pitch, roll and sticks.
Apex4XInputProbe.exe --capture-motion
set "capture_exit=%ERRORLEVEL%"
echo Exit code: %capture_exit%
pause
