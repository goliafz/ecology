<#
.SYNOPSIS
    Подготовка и запуск GUI-проверок VidLex в CI.
    Отдельный файл в UTF-8 с BOM: встроенные в YAML скрипты Windows PowerShell 5.1 читает в ANSI-кодировке
    и искажает кириллические пути.
#>
param(
    [ValidateSet("NoDeps", "Full")][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Temp,
    [string]$Deps = ""
)
$ErrorActionPreference = "Stop"
$app = Join-Path $Repo "VidLex\src\VidLex.App\bin\Release\net48\VidLex.exe"
$media = Join-Path $Repo "VidLex\tests\e2e\media"
$shots = Join-Path $Repo "artifacts\screenshots"
$smoke = Join-Path $Repo "VidLex\scripts\ci\Gui-Smoke.ps1"

if ($Mode -eq "NoDeps") {
    # Запуск без компонентов: должна появиться плашка «Не все компоненты найдены», приложение не падает.
    $src = Join-Path $Temp "gui-empty"
    New-Item -ItemType Directory -Force $src | Out-Null
    Copy-Item (Join-Path $media "Урок 01.mp4") $src
    & $smoke -AppExe $app -Source $src -DataDir (Join-Path $Temp "gui-nodeps") -ShotsDir $shots -Prefix "nodeps-" -NoStart
    exit $LASTEXITCODE
}

# Полный сценарий: обработка папки из интерфейса (без двух длинных лекций — ради времени).
$src = Join-Path $Temp "gui-media"
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
Copy-Item -Recurse $media $src
Remove-Item (Join-Path $src "Лекция с шумом.mpeg"), (Join-Path $src "Длинная лекция.mp4")
& $smoke -AppExe $app -Source $src -DataDir $Deps -ShotsDir $shots -TimeoutSec 900
$code = $LASTEXITCODE
$logs = Join-Path $Repo "artifacts\gui-logs"
New-Item -ItemType Directory -Force $logs | Out-Null
Copy-Item (Join-Path $Deps "Logs\*") $logs -ErrorAction SilentlyContinue
exit $code
