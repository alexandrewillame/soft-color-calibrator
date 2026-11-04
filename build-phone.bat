@echo off
rem Builds SoftCal Camera, the phone app (Android), and puts it here: SoftCalCamera.apk, to install on a phone, and
rem SoftCalCamera.aab, the app bundle that Google Play takes.
rem Needs the .NET Android workload, a JDK 17 and the Android SDK: see docs\phone-link.md, "Building the phone app".
setlocal
cd /d "%~dp0"
set "TOOLS=%USERPROFILE%\Android"
set "KEYSTORE=%TOOLS%\softcal-camera.keystore"
set "PASSWORD=%TOOLS%\softcal-camera.password"
set "KEYTOOL=%TOOLS%\openjdk\bin\keytool.exe"
set "OUT=%TOOLS%\build\SoftCal.Phone\bin\Release\net10.0-android"
set "APK=com.softcolorcalibrator.camera-Signed.apk"
set "AAB=com.softcolorcalibrator.camera-Signed.aab"

rem The app is signed with its own key, made once and kept with the Android tools (never in this folder), so a new
rem build installs over the old one on the phone. On Google Play it is the upload key: back it up with its password.
if not exist "%KEYSTORE%" (
  if not exist "%KEYTOOL%" (
    echo The JDK was not found in %TOOLS%\openjdk: see docs\phone-link.md.
    exit /b 1
  )
  echo Making the app's signing key in %TOOLS% ...
  rem A random password, on two lines: the signer reads the store's and then the key's from the same file.
  powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $p = [Convert]::ToBase64String($b); [IO.File]::WriteAllText('%PASSWORD%', $p + [Environment]::NewLine + $p + [Environment]::NewLine)"
  if errorlevel 1 (
    echo The signing key's password could not be made.
    exit /b 1
  )
  "%KEYTOOL%" -genkeypair -keystore "%KEYSTORE%" -alias softcal-camera -keyalg RSA -keysize 3072 -validity 20000 -dname "CN=SoftCal Camera" -storepass:file "%PASSWORD%" -keypass:file "%PASSWORD%" -noprompt
  if errorlevel 1 (
    echo The signing key could not be made.
    exit /b 1
  )
)

rem A signing that failed leaves an unsigned app under the signed name, which the next build would keep: start clean.
if exist "%OUT%\%APK%" del /q "%OUT%\%APK%"
if exist "%OUT%\%AAB%" del /q "%OUT%\%AAB%"
if exist "SoftCalCamera.apk" del /q "SoftCalCamera.apk"
if exist "SoftCalCamera.aab" del /q "SoftCalCamera.aab"

dotnet build src\SoftCal.Phone\SoftCal.Phone.csproj -c Release -nologo
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

copy /y "%OUT%\%APK%" "SoftCalCamera.apk" >nul
if errorlevel 1 (
  echo The built app was not found.
  exit /b 1
)
copy /y "%OUT%\%AAB%" "SoftCalCamera.aab" >nul
if errorlevel 1 (
  echo The built app bundle was not found.
  exit /b 1
)

echo.
echo Built: SoftCalCamera.apk and SoftCalCamera.aab
echo - SoftCalCamera.apk installs on a phone: copy it there (or open it from OneDrive on the phone), tap it, and allow
echo   installing apps from that source when Android asks. See docs\phone-link.md.
echo - SoftCalCamera.aab is for Google Play: upload it in Play Console.
