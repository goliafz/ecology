@echo off
chcp 65001 >nul
rem Сборка «Летописи ВКС». Нужен .NET SDK 6+ или Visual Studio 2019/2022.
dotnet build "%~dp0src\LetopisVKS\LetopisVKS.csproj" -c Release
if errorlevel 1 (
  echo Сборка не удалась.
  pause
  exit /b 1
)
echo Готово: %~dp0src\LetopisVKS\bin\Release\LetopisVKS.exe
pause
