@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================================
echo  Unblock MotW (Mark of the Web) for a folder
echo ============================================================
echo.
echo  This removes MotW (Zone.Identifier) from all files in the
echo  chosen folder so Windows Explorer can preview them.
echo  Windows cannot preview MotW files by design.
echo.
echo  Only run this on folders whose files you trust.
echo  Copy this .bat into the folder you want to unblock, then run it.
echo.
set "FOLDER="
set /p "FOLDER=Folder to unblock (press Enter to use this bat's folder): "
if "%FOLDER%"=="" set "FOLDER=%~dp0"
if not exist "%FOLDER%" (
  echo Folder not found: %FOLDER%
  pause
  exit /b 1
)
echo Unblocking: %FOLDER%
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%FOLDER%' -Recurse -File | Unblock-File"
if errorlevel 1 (
  echo Failed.
) else (
  echo Done. Restart Explorer if previews don't appear immediately.
)
pause
