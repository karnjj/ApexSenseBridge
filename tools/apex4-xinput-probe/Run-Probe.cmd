@echo off
cd /d "%~dp0"
Apex4XInputProbe.exe --identify > probe-result.txt 2>&1
set "probe_exit=%ERRORLEVEL%"
echo Exit code: %probe_exit% >> probe-result.txt
type probe-result.txt
echo.
echo Please share probe-result.txt in the chat.
pause
