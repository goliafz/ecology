<#
.SYNOPSIS
    Устанавливает внешние компоненты VidLex: FFmpeg, whisper.cpp (whisper-cli) и модель распознавания.

.DESCRIPTION
    Компоненты скачиваются в папку данных VidLex (по умолчанию %LocalAppData%\VidLex):
      tools\ffmpeg\   — ffmpeg.exe, ffprobe.exe (сборка BtbN, LGPL — только декодирование, без GPL-кодеков)
      tools\whisper\  — whisper-cli.exe и библиотеки ggml (официальный релиз whisper.cpp)
      Models\         — модель ggml-*.bin (Hugging Face, ggerganov/whisper.cpp)
    VidLex находит их автоматически, указывать пути в настройках не нужно.

.PARAMETER Model
    Модель whisper.cpp. Рекомендации для русского языка:
      large-v3-turbo-q5_0 — лучшее качество на размер (547 МиБ), по умолчанию;
      large-v3-turbo      — то же без квантования (1,5 ГиБ);
      medium              — 1,5 ГиБ;  large-v3 / large-v3-q5_0 — 2,9 / 1,1 ГиБ (медленнее на процессоре);
      small               — быстрее, но заметно больше ошибок (466 МиБ);  base, tiny — только для проверки.
    Размеры и контрольные суммы SHA-1 — из models/README.md репозитория whisper.cpp.

.PARAMETER Gpu
    Скачать сборку whisper.cpp с поддержкой NVIDIA CUDA (нужен драйвер NVIDIA). Без ключа — сборка для процессора.

.PARAMETER DataDir
    Папка данных VidLex. По умолчанию %LocalAppData%\VidLex (или значение переменной VIDLEX_DATA_DIR).

.PARAMETER SkipFfmpeg, SkipWhisper, SkipModel
    Пропустить установку соответствующего компонента.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-Dependencies.ps1
    powershell -ExecutionPolicy Bypass -File .\Install-Dependencies.ps1 -Model small -Gpu
#>
[CmdletBinding()]
param(
    [string]$Model = "large-v3-turbo-q5_0",
    [switch]$Gpu,
    [string]$DataDir = "",
    [switch]$SkipFfmpeg,
    [switch]$SkipWhisper,
    [switch]$SkipModel,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # иначе Invoke-WebRequest в PowerShell 5.1 качает в разы медленнее
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# SHA-1 моделей по таблице models/README.md репозитория whisper.cpp.
$KnownModels = @{
    "tiny"                = "bd577a113a864445d4c299885e0cb97d4ba92b5f"
    "base"                = "465707469ff3a37a2b9b8d8f89f2f99de7299dac"
    "small"               = "55356645c2b361a969dfd0ef2c5a50d530afd8d5"
    "medium"              = "fd9727b6e1217c2f614f9b698455c4ffd82463b4"
    "large-v3"            = "ad82bf6a9043ceed055076d0fd39f5f186ff8062"
    "large-v3-q5_0"       = "e6e2ed78495d403bef4b7cff42ef4aaadcfea8de"
    "large-v3-turbo"      = "4af2b29d7ec73d781377bfd1758ca957a807e941"
    "large-v3-turbo-q5_0" = "e050f7970618a659205450ad97eb95a18d69c9ee"
}
if (-not $KnownModels.ContainsKey($Model)) {
    throw "Неизвестная модель «$Model». Допустимые: $(($KnownModels.Keys | Sort-Object) -join ', ')"
}

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = if ($env:VIDLEX_DATA_DIR) { $env:VIDLEX_DATA_DIR } else { Join-Path $env:LOCALAPPDATA "VidLex" }
}
$ToolsDir = Join-Path $DataDir "tools"
$ModelsDir = Join-Path $DataDir "Models"
$Downloads = Join-Path $DataDir "Downloads"
New-Item -ItemType Directory -Force -Path $ToolsDir, $ModelsDir, $Downloads | Out-Null

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Green }

function Get-File([string]$url, [string]$target) {
    Write-Host "    Загрузка: $url"
    $tmp = "$target.part"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    $headers = @{ "User-Agent" = "VidLex-Installer" }
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -Headers $headers
    Move-Item -Force $tmp $target
    $size = (Get-Item $target).Length
    Write-Host ("    Готово: {0:N1} МБ" -f ($size / 1MB))
}

function Expand-To([string]$zip) {
    $tmp = Join-Path $Downloads ("unpack-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    return $tmp
}

# ---------------- FFmpeg ----------------
if (-not $SkipFfmpeg) {
    Write-Step "FFmpeg"
    $ffDir = Join-Path $ToolsDir "ffmpeg"
    if ((Test-Path (Join-Path $ffDir "ffmpeg.exe")) -and -not $Force) {
        Write-Host "    Уже установлен: $ffDir (для переустановки используйте -Force)"
    } else {
        $zip = Join-Path $Downloads "ffmpeg.zip"
        Get-File "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip" $zip
        $tmp = Expand-To $zip
        $ffmpeg = Get-ChildItem -Path $tmp -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
        if (-not $ffmpeg) { throw "В архиве FFmpeg не найден ffmpeg.exe" }
        if (Test-Path $ffDir) { Remove-Item $ffDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
        Copy-Item (Join-Path $ffmpeg.DirectoryName "*") $ffDir -Recurse -Force
        Remove-Item $tmp -Recurse -Force
        Remove-Item $zip -Force
        & (Join-Path $ffDir "ffmpeg.exe") -hide_banner -version | Select-Object -First 1
    }
}

# ---------------- whisper.cpp ----------------
if (-not $SkipWhisper) {
    Write-Step "whisper.cpp"
    $wDir = Join-Path $ToolsDir "whisper"
    if ((Test-Path (Join-Path $wDir "whisper-cli.exe")) -and -not $Force) {
        Write-Host "    Уже установлен: $wDir (для переустановки используйте -Force)"
    } else {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest" -Headers @{ "User-Agent" = "VidLex-Installer" } -UseBasicParsing
        Write-Host "    Версия whisper.cpp: $($release.tag_name)"
        # Имена архивов меняются от релиза к релизу, поэтому сначала ищем точное известное имя, затем — по признакам.
        $zips = @($release.assets | Where-Object { $_.name -match '\.zip$' })
        Write-Host "    Архивы релиза: $(($zips | ForEach-Object { $_.name }) -join ', ')"
        $gpuRx = 'cublas|cuda'
        $otherRx = 'vulkan|openvino|sycl|hip|rocm|xcframework|arm|aarch64|win32|x86(?!_64)|macos|linux|android|ios|jar'
        if ($Gpu) {
            $asset = $zips | Where-Object { $_.name -match $gpuRx -and $_.name -match 'x64' } | Sort-Object name -Descending | Select-Object -First 1
        } else {
            $asset = $zips | Where-Object { $_.name -eq 'whisper-bin-x64.zip' } | Select-Object -First 1
            if (-not $asset) {
                $asset = $zips | Where-Object {
                    $_.name -match 'x64' -and $_.name -match '^whisper' -and $_.name -notmatch $gpuRx -and $_.name -notmatch 'blas' -and $_.name -notmatch $otherRx
                } | Sort-Object { $_.name.Length } | Select-Object -First 1
            }
        }
        if (-not $asset) { throw "В релизе $($release.tag_name) не найден подходящий архив для Windows x64 ($(if ($Gpu) { 'CUDA' } else { 'CPU' }))" }
        Write-Host "    Выбран архив: $($asset.name)"
        $zip = Join-Path $Downloads $asset.name
        Get-File $asset.browser_download_url $zip
        $tmp = Expand-To $zip
        $cli = Get-ChildItem -Path $tmp -Recurse -Filter "whisper-cli.exe" | Select-Object -First 1
        if (-not $cli) { throw "В архиве whisper.cpp не найден whisper-cli.exe" }
        if (Test-Path $wDir) { Remove-Item $wDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $wDir | Out-Null
        Copy-Item (Join-Path $cli.DirectoryName "*") $wDir -Recurse -Force
        Set-Content -Path (Join-Path $wDir "VERSION.txt") -Value "$($release.tag_name) ($($asset.name))" -Encoding UTF8
        Remove-Item $tmp -Recurse -Force
        Remove-Item $zip -Force
        Write-Host "    Установлен в $wDir"
    }
}

# ---------------- Модель ----------------
if (-not $SkipModel) {
    Write-Step "Модель ggml-$Model.bin"
    $target = Join-Path $ModelsDir "ggml-$Model.bin"
    if ((Test-Path $target) -and -not $Force) {
        Write-Host "    Уже загружена: $target"
    } else {
        Get-File "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$Model.bin" $target
    }
    # Проверка целостности: контрольная сумма SHA-1 из документации whisper.cpp.
    Write-Host "    Проверка контрольной суммы…"
    $sha = (Get-FileHash -Algorithm SHA1 -Path $target).Hash.ToLowerInvariant()
    if ($sha -ne $KnownModels[$Model]) {
        Remove-Item $target -Force
        throw "Контрольная сумма модели не совпала (получено $sha, ожидалось $($KnownModels[$Model])). Файл удалён — повторите установку."
    }
    Write-Host "    Модель проверена (SHA-1 совпадает): $target"
}

Write-Step "Готово"
Write-Host "    Папка данных VidLex: $DataDir"
Write-Host "    Запустите VidLex — компоненты будут найдены автоматически (Настройки → Проверить)."
