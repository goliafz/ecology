# VidLex: установка, сборка и запуск

## 1. Системные требования

| Компонент | Требование |
| --- | --- |
| ОС | Windows 10 или 11, 64-бит |
| .NET | .NET Framework 4.8 (входит в Windows 10 версии 1903 и новее и в Windows 11; для более старых сборок Windows 10 установите его с сайта Microsoft) |
| Процессор | x64 с AVX2 (рекомендуется; whisper.cpp использует SIMD-инструкции) |
| ОЗУ | по таблице README whisper.cpp: small ~852 МБ, medium ~2,1 ГБ, large ~3,9 ГБ (модели f16); квантованные модели (`-q5_0`) требуют меньше |
| Диск | модель (0,5–3 ГБ) + временный WAV: около 115 МБ на час видео (16 кГц, моно, 16 бит) |
| GPU | необязательно; для NVIDIA можно установить CUDA-сборку whisper.cpp (`-Gpu`) |

Скорость распознавания сильно зависит от процессора и модели. VidLex измеряет её во время работы и по ней
оценивает оставшееся время. Заранее обещать конкретную скорость нельзя.

## 2. Установка компонентов распознавания

VidLex использует внешние программы: **FFmpeg** (чтение видео, извлечение аудио) и **whisper.cpp** (`whisper-cli.exe`,
распознавание), а также **модель** в формате ggml. Всё это ставит один скрипт:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Install-Dependencies.ps1
```

Скрипт скачивает компоненты в `%LocalAppData%\VidLex`:

| Что | Откуда | Куда |
| --- | --- | --- |
| FFmpeg (LGPL-сборка) | github.com/BtbN/FFmpeg-Builds, `ffmpeg-master-latest-win64-lgpl.zip` | `tools\ffmpeg\` |
| whisper.cpp (последний релиз) | github.com/ggml-org/whisper.cpp, `whisper-bin-x64.zip` (или CUDA-сборка с `-Gpu`) | `tools\whisper\` |
| Модель | huggingface.co/ggerganov/whisper.cpp, `ggml-<модель>.bin` | `Models\` |

Модель проверяется по SHA-1 из `models/README.md` репозитория whisper.cpp.

Параметры:

```powershell
# Модель по умолчанию — large-v3-turbo-q5_0 (547 МиБ): лучшее соотношение качества и размера.
.\Install-Dependencies.ps1 -Model small          # быстрее, но больше ошибок
.\Install-Dependencies.ps1 -Model large-v3-turbo # без квантования, 1,5 ГиБ
.\Install-Dependencies.ps1 -Gpu                  # сборка whisper.cpp с CUDA (нужен драйвер NVIDIA)
.\Install-Dependencies.ps1 -Force                # переустановить
```

### Ручная установка (без скрипта)

1. Скачайте `whisper-bin-x64.zip` со страницы релизов whisper.cpp и распакуйте в любую папку.
2. Скачайте модель, например `ggml-large-v3-turbo-q5_0.bin`, со страницы huggingface.co/ggerganov/whisper.cpp.
3. Установите FFmpeg (например, `winget install Gyan.FFmpeg`) или распакуйте архив сборки.
4. В VidLex откройте «Настройки» → «Распознавание речи», укажите пути к `whisper-cli.exe`, модели, `ffmpeg.exe`
   и `ffprobe.exe`, нажмите «Проверить» и «Сохранить».

Порядок поиска, если путь в настройках пуст: папка `tools` рядом с `VidLex.exe` → `%LocalAppData%\VidLex\tools`
→ переменная PATH. Модель ищется в `%LocalAppData%\VidLex\Models`, затем в папке `models` рядом с программой;
если моделей несколько, берётся наиболее точная (large-v3-turbo → large → medium → small → base → tiny).

> **Совместимость whisper.cpp.** VidLex передаёт аргументы `whisper-cli` через файл ответов (`@args.txt`) в UTF-8.
> Так кириллические пути к модели и русская подсказка с терминами работают при любой системной локали.
> Проверено с whisper.cpp 1.8.5; рекомендуется версия 1.8 или новее.

## 3. Сборка из исходников

Нужен .NET SDK 8 (подходит и Visual Studio 2022 с рабочей нагрузкой «Разработка классических приложений .NET»).
Справочные сборки .NET Framework 4.8 подключаются пакетом `Microsoft.NETFramework.ReferenceAssemblies`,
поэтому отдельный Developer Pack не обязателен.

```powershell
dotnet build VidLex.sln -c Release
dotnet test tests\VidLex.Core.Tests -c Release
```

Готовое приложение: `src\VidLex.App\bin\Release\net48\VidLex.exe`.

Сборка ZIP-дистрибутива (с компонентами из `%LocalAppData%\VidLex\tools`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -RunTests -IncludeTools
```

Результат: `artifacts\VidLex-1.0.0.zip`. Распакуйте его в любую папку и запустите `VidLex.exe`.
Модель в архив не входит, её ставит `Install-Dependencies.ps1 -SkipFfmpeg -SkipWhisper`.

## 4. Использование

1. Нажмите **«Выбрать папку…»** или перетащите папку на окно программы. VidLex найдёт видео во всех вложенных папках.
2. При необходимости откройте **«Настройки»**: режим сохранения, временные метки, поведение при существующих TXT,
   пользовательский словарь.
3. Нажмите **«Начать обработку»**. Кнопки **«Приостановить»**, **«Продолжить»**, **«Остановить»** управляют очередью.
4. Двойной щелчок по строке открывает готовый TXT. В контекстном меню строки есть пункты «Показать в папке»,
   «Обработать заново» и «Копировать текст ошибки».

Запуск из командной строки:

```powershell
VidLex.exe --source "D:\Видео\Курс"            # открыть папку
VidLex.exe --source "D:\Видео\Курс" --start    # открыть и сразу начать обработку
cli\VidLex.Cli.exe --source "D:\Видео" --output-folder "D:\Тексты" --timestamps   # без интерфейса
```

Горячие клавиши: `Ctrl+1` — главная, `Ctrl+2` — настройки, `Ctrl+3` — журнал, `F1` — о программе,
`Alt` + подчёркнутая буква — кнопки.

## 5. Где хранятся данные

| Путь | Содержимое |
| --- | --- |
| `%LocalAppData%\VidLex\settings.json` | настройки (повреждённый файл сохраняется как `settings.corrupted-ДАТА.json`) |
| `%LocalAppData%\VidLex\Logs\` | журналы `VidLex_ГГГГ-ММ-ДД.log`, ротация по дням и размеру |
| `%LocalAppData%\VidLex\Temp\` | временные WAV и контрольные точки (удаляются после обработки) |
| `%LocalAppData%\VidLex\State\` | состояние очереди для восстановления после сбоя |
| `%LocalAppData%\VidLex\Recovered\` | резервные копии расшифровок, которые не удалось сохранить в целевую папку |
| `%LocalAppData%\VidLex\Models\`, `tools\` | модели и компоненты, установленные скриптом |

Переменная окружения `VIDLEX_DATA_DIR` переопределяет эту папку (например, для переносной установки).
