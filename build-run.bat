@echo off
rem Builds Soft Color Calibrator (Release), then starts it.
setlocal
cd /d "%~dp0"
call "%~dp0build.bat" || exit /b 1
call "%~dp0run.bat"
