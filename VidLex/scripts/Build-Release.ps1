<#
.SYNOPSIS
    Собирает Release-версию VidLex и упаковывает её в ZIP.

.PARAMETER IncludeTools
    Положить в сборку FFmpeg и whisper.cpp из папки данных (сначала запустите Install-Dependencies.ps1).
    Получится переносная сборка: модель всё равно хранится отдельно (в %LocalAppData%\VidLex\Models).

.PARAMETER RunTests
    Перед упаковкой запустить модульные тесты.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1 -RunTests -IncludeTools
#>
[CmdletBinding()]
param(
    [switch]$IncludeTools,
    [switch]$RunTests,
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "VidLex.sln"

Write-Host "==> Сборка (Release)" -ForegroundColor Green
dotnet build $sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой" }

if ($RunTests) {
    Write-Host "==> Модульные тесты" -ForegroundColor Green
    dotnet test (Join-Path $root "tests\VidLex.Core.Tests\VidLex.Core.Tests.csproj") -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Тесты не пройдены" }
}

$appOut = Join-Path $root "src\VidLex.App\bin\Release\net48"
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$dist = Join-Path $root "artifacts\VidLex-$version"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "==> Копирование файлов в $dist" -ForegroundColor Green
Copy-Item (Join-Path $appOut "*") $dist -Recurse -Force -Exclude "*.pdb"
$cliOut = Join-Path $root "tools\VidLex.Cli\bin\Release\net48"
New-Item -ItemType Directory -Force -Path (Join-Path $dist "cli") | Out-Null
Copy-Item (Join-Path $cliOut "*") (Join-Path $dist "cli") -Recurse -Force -Exclude "*.pdb"
Copy-Item (Join-Path $root "README.md") $dist
Copy-Item (Join-Path $root "docs\INSTALL.md") $dist
New-Item -ItemType Directory -Force -Path (Join-Path $dist "scripts") | Out-Null
Copy-Item (Join-Path $root "scripts\Install-Dependencies.ps1") (Join-Path $dist "scripts")

if ($IncludeTools) {
    if ([string]::IsNullOrWhiteSpace($DataDir)) {
        $DataDir = if ($env:VIDLEX_DATA_DIR) { $env:VIDLEX_DATA_DIR } else { Join-Path $env:LOCALAPPDATA "VidLex" }
    }
    $tools = Join-Path $DataDir "tools"
    if (-not (Test-Path (Join-Path $tools "whisper\whisper-cli.exe"))) { throw "Компоненты не найдены в $tools — запустите Install-Dependencies.ps1" }
    Write-Host "==> Добавление FFmpeg и whisper.cpp" -ForegroundColor Green
    Copy-Item $tools (Join-Path $dist "tools") -Recurse -Force
}

$zip = "$dist.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip
Write-Host ("==> Готово: {0} ({1:N1} МБ)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
