@echo off
setlocal
set "APP_DIR=%~dp0outputs\ZoomRecorder-0.2.0"

if not exist "%APP_DIR%\ZoomRecorder.App.exe" (
  echo Zoom Recorder could not be found at:
  echo %APP_DIR%\ZoomRecorder.App.exe
  pause
  exit /b 1
)

start "" /d "%APP_DIR%" "%APP_DIR%\ZoomRecorder.App.exe"
