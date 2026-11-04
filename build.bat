@echo off
rem Builds Soft Color Calibrator (Release). Close the app first: a running copy locks its files.
setlocal
cd /d "%~dp0"
rem SoftCal's calibration loader runs from this build and keeps its files in use: ask it to stop first.
rem The app starts it again the next time it runs.
powershell -NoProfile -Command "try { [void][System.Threading.EventWaitHandle]::OpenExisting('Local\SoftColorCalibrator.Loader.Stop').Set(); Start-Sleep -Seconds 2 } catch { }"
dotnet build src\SoftCal.App\SoftCal.App.csproj -c Release -nologo
if errorlevel 1 (
  echo.
  echo Build failed. If it says a file is in use, close Soft Color Calibrator and try again.
  exit /b 1
)
echo.
echo Built: src\SoftCal.App\bin\Release\net10.0-windows10.0.22621.0\SoftColorCalibrator.exe
