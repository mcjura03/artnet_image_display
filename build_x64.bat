@echo off
setlocal

dotnet publish -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:IncludeNativeLibrariesForSelfExtract=true ^
  /p:EnableCompressionInSingleFile=true

if errorlevel 1 (
  echo.
  echo BUILD FEHLGESCHLAGEN.
  pause
  exit /b 1
)

echo.
echo Compilation succeded, find .exe at:
echo   bin\Release\net8.0-windows\win-x64\publish\ArtnetImageViewer.exe
echo.
pause
