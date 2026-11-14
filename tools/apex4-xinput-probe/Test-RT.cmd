@echo off
cd /d "%~dp0"
echo Experimental test: low RT resistance for 2 seconds, followed by a reset of both triggers.
echo Close games and Flydigi Space Station. Connect only your APEX 4 in XInput mode.
echo Hold the controller. Gently press RT immediately after pressing a key below.
pause
Apex4XInputProbe.exe --test-rt > trigger-result.txt 2>&1
set "probe_exit=%ERRORLEVEL%"
echo Exit code: %probe_exit% >> trigger-result.txt
type trigger-result.txt
echo.
echo If resistance remains, power-cycle the controller. Share the sensation and trigger-result.txt.
pause
