@echo off
setlocal
rem Removes WheelFix for the current user: closes it, deletes its shortcut
rem in the Startup folder and deletes %LOCALAPPDATA%\WheelFix, settings
rem included.

set "DEST=%LOCALAPPDATA%\WheelFix"

if not exist "%DEST%\WheelFix.exe" goto :remove
start "" /wait "%DEST%\WheelFix.exe" --exit
if errorlevel 1 goto :still_running

:remove
rem Windows PowerShell finds the Startup folder, also when it was moved.
powershell -NoProfile -NonInteractive -Command "$ErrorActionPreference = 'Stop'; $link = Join-Path ([Environment]::GetFolderPath('Startup')) 'WheelFix.lnk'; if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }"
if errorlevel 1 goto :failed
if exist "%DEST%" rmdir /s /q "%DEST%"
if exist "%DEST%" goto :failed

echo WheelFix has been removed.
pause
exit /b 0

:still_running
echo WheelFix is running and could not be closed.
echo Right-click its icon near the clock, choose Exit, then run uninstall.bat again.
goto :failed

:failed
echo.
echo WheelFix was not completely removed.
pause
exit /b 1
