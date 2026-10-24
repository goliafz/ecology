<#
.SYNOPSIS
    Проверка интерфейса VidLex на Windows (CI): запуск, навигация через UI Automation, скриншоты,
    обработка папки из параметров запуска, корректное закрытие.
#>
param(
    [Parameter(Mandatory = $true)][string]$AppExe,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ShotsDir,
    [string]$Prefix = "",
    [switch]$NoStart,
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
New-Item -ItemType Directory -Force -Path $ShotsDir | Out-Null
$env:VIDLEX_DATA_DIR = $DataDir
$Title = "VidLex — разработчик Ходаковский Александр"
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { Write-Host "  [FAIL] $m" -ForegroundColor Red; $failures.Add($m) }
function Ok([string]$m) { Write-Host "  [OK]   $m" -ForegroundColor Green }

function Find-Window([int]$seconds) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Title)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Shot($win, [string]$name) {
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [void][Win32]::ShowWindow($hwnd, 9); [void][Win32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 700
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $path = Join-Path $ShotsDir "$Prefix$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  скриншот: $path"
}

function Click($win, [string]$name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $b = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $b) { Fail "кнопка «$name» не найдена"; return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    return $true
}

function Has-Text($win, [string]$text) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
    return $null -ne $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$appArgs = @("--source", "`"$Source`"")
if (-not $NoStart) { $appArgs += "--start" }
$proc = Start-Process -FilePath $AppExe -ArgumentList $appArgs -PassThru
$win = Find-Window 60
if (-not $win) { Fail "окно «$Title» не появилось за 60 с"; if (-not $proc.HasExited) { $proc.Kill() }; exit 1 }
Ok "окно открыто, заголовок: $($win.Current.Name)"
if (Has-Text $win "Разработчик: Ходаковский Александр") { Ok "подпись разработчика на главной странице" } else { Fail "нет подписи «Разработчик: Ходаковский Александр»" }

Start-Sleep -Seconds 6
Shot $win "01-home"
if (Click $win "Настройки") { Shot $win "02-settings"; if (Has-Text $win "Добавлять временные метки") { Ok "страница настроек: переключатель временных меток" } else { Fail "на странице настроек нет «Добавлять временные метки»" } }
if (Click $win "Журнал") { Shot $win "03-log" }
if (Click $win "О программе") { Shot $win "04-about"; if (Has-Text $win "Разработчик: Ходаковский Александр") { Ok "раздел «О программе»: разработчик указан" } else { Fail "в «О программе» нет разработчика" } }
[void](Click $win "Главная")

if (-not $NoStart) {
    $logDir = Join-Path $DataDir "Logs"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $done = $false
    $midShot = $false
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Seconds 3
        if (-not $midShot -and $sw.Elapsed.TotalSeconds -gt 12) { Shot $win "05-processing"; $midShot = $true }
        $log = Get-ChildItem $logDir -Filter "VidLex_*.log" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }
        if ($log -match "Очередь (завершена|остановлена)") { $done = $true; break }
    }
    if ($done) { Ok "обработка из интерфейса завершена за $([int]$sw.Elapsed.TotalSeconds) с" } else { Fail "обработка не завершилась за $TimeoutSec с" }
    Start-Sleep -Seconds 2
    Shot $win "06-finished"
    $txt = Get-ChildItem $Source -Recurse -Filter "*.txt" | Where-Object { $_.Name -notlike "*.vidlex-*" }
    if ($txt.Count -ge 3) { Ok "создано TXT: $($txt.Count)" } else { Fail "создано мало TXT: $($txt.Count)" }
}

# Корректное закрытие окна.
$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
if ($proc.WaitForExit(30000)) { Ok "приложение закрыто, код $($proc.ExitCode)" } else { Fail "приложение не закрылось за 30 с"; $proc.Kill() }

$log = Get-ChildItem (Join-Path $DataDir "Logs") -Filter "VidLex_*.log" | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }
if ($log -match "Запуск VidLex") { Ok "журнал: запуск записан" } else { Fail "в журнале нет записи о запуске" }
if ($log -match "Завершение работы VidLex") { Ok "журнал: завершение записано" } else { Fail "в журнале нет записи о завершении" }
if ($log -match "Необработанная ошибка|Критическая ошибка") { Fail "в журнале есть необработанные ошибки" } else { Ok "необработанных ошибок нет" }

if ($failures.Count -gt 0) { Write-Host "Не пройдено проверок: $($failures.Count)"; exit 1 }
Write-Host "GUI-проверки пройдены"
