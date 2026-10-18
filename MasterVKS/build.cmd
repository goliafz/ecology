@echo off
chcp 65001 >nul
rem Сборка «МастерВКС». Нужен .NET SDK 6+ или Visual Studio 2019/2022.
dotnet build "%~dp0src\MasterVKS\MasterVKS.csproj" -c Release
if errorlevel 1 (
  echo Сборка не удалась.
  pause
  exit /b 1
)
echo Готово: %~dp0src\MasterVKS\bin\Release\MasterVKS.exe
pause
