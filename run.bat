@echo off
rem Starts the last Release build of Soft Color Calibrator (run build.bat first).
setlocal
cd /d "%~dp0"
set "APP=src\SoftCal.App\bin\Release\net10.0-windows10.0.22621.0\SoftColorCalibrator.exe"
if not exist "%APP%" (
  echo The app is not built yet: run build.bat first.
  exit /b 1
)
start "" "%APP%"
