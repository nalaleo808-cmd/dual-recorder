@echo off
REM Builds DualRecorder.exe as a single self-contained file.
REM Needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

setlocal
cd /d "%~dp0"

dotnet publish src\DualRecorder\DualRecorder.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)

echo.
echo Done. The app is here:
echo %CD%\src\DualRecorder\bin\Release\net8.0-windows\win-x64\publish\DualRecorder.exe
explorer "%CD%\src\DualRecorder\bin\Release\net8.0-windows\win-x64\publish"
pause
