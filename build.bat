@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo Building Shush...
dotnet publish Shush.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the error text above and send it to Claude.
  pause
  exit /b 1
)
if not exist "release" mkdir "release"
if not exist "release\config" mkdir "release\config"
copy /y "out\shush.exe" "release\shush.exe" >nul
rmdir /s /q out 2>nul
rmdir /s /q bin 2>nul
rmdir /s /q obj 2>nul
echo.
echo Done. Run release\shush.exe   (configs and logs are saved in release\config)
pause
