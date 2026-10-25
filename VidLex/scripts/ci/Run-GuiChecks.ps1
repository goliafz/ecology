<#
.SYNOPSIS
    Подготовка и запуск GUI-проверок VidLex в CI.
    Отдельный файл в UTF-8 с BOM: встроенные в YAML скрипты Windows PowerShell 5.1 читает в ANSI-кодировке
    и искажает кириллические пути.
#>
param(
    [ValidateSet("NoDeps", "Full", "CloseDuring")][string]$Mode,
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

# Чистая папка данных для каждого сценария (компоненты копируются из кэша) — журналы и состояние прошлых
# запусков не должны влиять на проверки.
function New-DataDir([string]$name) {
    $dir = Join-Path $Temp $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item -Recurse (Join-Path $Deps "tools") (Join-Path $dir "tools")
    Copy-Item -Recurse (Join-Path $Deps "Models") (Join-Path $dir "Models")
    return $dir
}

if ($Mode -eq "CloseDuring") {
    $src = Join-Path $Temp "gui-close"
    if (Test-Path $src) { Remove-Item $src -Recurse -Force }
    New-Item -ItemType Directory -Force $src | Out-Null
    Copy-Item (Join-Path $media "Длинная лекция.mp4") $src
    $data = New-DataDir "gui-close-data"
    & $smoke -AppExe $app -Source $src -DataDir $data -ShotsDir $shots -Scenario CloseDuringProcessing -TimeoutSec 600
    $code = $LASTEXITCODE
} else {
    # Полный сценарий: обработка папки из интерфейса (без двух длинных лекций — ради времени).
    $src = Join-Path $Temp "gui-media"
    if (Test-Path $src) { Remove-Item $src -Recurse -Force }
    Copy-Item -Recurse $media $src
    Remove-Item (Join-Path $src "Лекция с шумом.mpeg"), (Join-Path $src "Длинная лекция.mp4")
    $data = New-DataDir "gui-full-data"
    & $smoke -AppExe $app -Source $src -DataDir $data -ShotsDir $shots -TimeoutSec 900
    $code = $LASTEXITCODE
}
$logs = Join-Path $Repo ("artifacts\gui-logs\" + $Mode)
New-Item -ItemType Directory -Force $logs | Out-Null
Copy-Item (Join-Path $data "Logs\*") $logs -ErrorAction SilentlyContinue
exit $code
