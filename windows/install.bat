@echo off
setlocal
rem Installs WheelFix for the current user, without administrator rights:
rem compiles WheelFix and wheelfix-tools with the C# compiler of the .NET Framework included in
rem Windows, copies it to %LOCALAPPDATA%\WheelFix, adds a shortcut to the
rem Startup folder and starts it. Run it again to update; wheelfix.ini is kept.

set "DEST=%LOCALAPPDATA%\WheelFix"

rem Every .NET Framework 4.x version (4.0 to 4.8.1) installs into the same
rem v4.0.30319 folder; any later v4 folder would be found too. csc.exe is
rem not in PATH, so look for it. 64-bit first, then 32-bit.
rem Keep this search in sync with .github/workflows/build.yml.
set "CSC="
for /d %%D in ("%WINDIR%\Microsoft.NET\Framework64\v4*" "%WINDIR%\Microsoft.NET\Framework\v4*") do (
    if not defined CSC if exist "%%D\csc.exe" set "CSC=%%D\csc.exe"
)
if not defined CSC goto :no_compiler

if not exist "%DEST%" mkdir "%DEST%"
rem Both programs are compiled next to the installed ones first, so a
rem failed build leaves the installed version untouched.
echo Compiling WheelFix...
"%CSC%" /nologo /target:winexe /optimize+ /out:"%DEST%\WheelFix.new.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%~dp0WheelFilter.cs" "%~dp0WheelHook.cs" "%~dp0WheelFix.cs"
if errorlevel 1 goto :failed
"%CSC%" /nologo /target:exe /optimize+ /out:"%DEST%\wheelfix-tools.new.exe" /reference:System.Windows.Forms.dll "%~dp0WheelFilter.cs" "%~dp0WheelHook.cs" "%~dp0WheelFixTools.cs"
if errorlevel 1 goto :failed

move /y "%DEST%\wheelfix-tools.new.exe" "%DEST%\wheelfix-tools.exe" >nul
if errorlevel 1 goto :tools_running

rem Close the running copy, if any, so its file can be replaced.
start "" /wait "%DEST%\WheelFix.new.exe" --exit
if errorlevel 1 goto :still_running

move /y "%DEST%\WheelFix.new.exe" "%DEST%\WheelFix.exe" >nul
if errorlevel 1 goto :failed

rem A batch file cannot create a shortcut, so Windows PowerShell does it.
rem The paths are passed through environment variables to avoid quoting.
set "TARGET=%DEST%\WheelFix.exe"
powershell -NoProfile -NonInteractive -Command "$ErrorActionPreference = 'Stop'; $link = Join-Path ([Environment]::GetFolderPath('Startup')) 'WheelFix.lnk'; $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link); $shortcut.TargetPath = $env:TARGET; $shortcut.WorkingDirectory = $env:DEST; $shortcut.Save()"
if errorlevel 1 goto :failed

start "" "%DEST%\WheelFix.exe"

echo.
echo WheelFix is installed and running: its icon is near the clock.
echo It starts automatically with Windows. Right-click the icon for the
echo settings, or to pause or exit WheelFix.
echo.
pause
exit /b 0

:no_compiler
echo The C# compiler of the .NET Framework 4 was not found in %WINDIR%\Microsoft.NET.
goto :failed

:tools_running
echo wheelfix-tools is running. Close its console window, then run install.bat again.
goto :failed

:still_running
echo WheelFix is running and could not be closed.
echo Right-click its icon near the clock, choose Exit, then run install.bat again.
goto :failed

:failed
del "%DEST%\*.new.exe" >nul 2>&1
echo.
echo WheelFix was not installed.
pause
exit /b 1
