using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>Контекст запуска очереди, общий для всех заданий.</summary>
    public sealed class ProcessingContext
    {
        public AppSettings Settings { get; set; }
        public ToolPaths Tools { get; set; }
        public PauseGate Gate { get; set; }
        /// <summary>Потоков whisper.cpp на одно задание.</summary>
        public int ThreadsPerJob { get; set; }
        /// <summary>Сохранять контрольные точки при остановке (закрытие приложения).</summary>
        public Func<bool> KeepCheckpointsOnStop { get; set; } = () => false;
    }

    /// <summary>
    /// Обработка одного видео: проверка → ffprobe → извлечение аудио → поиск пауз → распознавание по фрагментам →
    /// постобработка → сохранение TXT. Ошибка оформляется в статус задания и не выбрасывается наружу,
    /// поэтому сбой одного файла не останавливает очередь.
    /// </summary>
    public sealed class JobProcessor
    {
        private const double PctProbe = 1, PctExtractEnd = 8, PctSilenceEnd = 10, PctRecognizeEnd = 92, PctTextEnd = 96;

        private readonly IMediaProbeService _probe;
        private readonly IAudioExtractionService _audio;
        private readonly ISilenceDetectionService _silence;
        private readonly ITranscriptionEngine _engine;
        private readonly ITextCorrectionService _text;
        private readonly ITranscriptExportService _export;
        private readonly ILogService _log;
        private readonly AppPaths _paths;

        public JobProcessor(IMediaProbeService probe, IAudioExtractionService audio, ISilenceDetectionService silence,
            ITranscriptionEngine engine, ITextCorrectionService text, ITranscriptExportService export, ILogService log, AppPaths paths)
        {
            _probe = probe;
            _audio = audio;
            _silence = silence;
            _engine = engine;
            _text = text;
            _export = export;
            _log = log;
            _paths = paths;
        }

        /// <summary>Сведения о ходе распознавания для оценки времени.</summary>
        public event Action<VideoJob, LiveJobProgress> LiveProgress;

        public async Task<JobStatistics> ProcessAsync(VideoJob job, ProcessingContext ctx, CancellationToken ct)
        {
            var s = ctx.Settings;
            var stats = new JobStatistics();
            var tempDir = Path.Combine(_paths.TempDirectory, "job-" + job.Id);
            var name = job.FileName;
            bool keepTemp = false;

            job.Reset();
            job.Status = JobStatus.PreparingAudio;
            job.Detail = "Проверка файла";
            _log.Info($"Начало обработки: {job.RelativePath}", name, "Задание");

            using (var total = new PausableStopwatch(ctx.Gate))
            {
                try
                {
                    // 1. Доступность файла.
                    CheckReadable(job.FullPath);

                    // 2. Нужно ли вообще обрабатывать (результат уже есть?).
                    var decision = _export.Decide(job, s);
                    if (decision.Skip)
                    {
                        job.Status = JobStatus.Skipped;
                        job.Detail = decision.Reason;
                        job.OutputPath = decision.TargetPath;
                        job.Progress = 100;
                        _log.Info($"Пропущено: {decision.Reason}", name, "Сохранение");
                        return stats;
                    }
                    if (!string.IsNullOrEmpty(decision.Reason)) _log.Info(decision.Reason, name, "Сохранение");

                    // 3. Параметры медиаданных.
                    job.Detail = "Чтение параметров видео";
                    var media = await _probe.ProbeAsync(job.FullPath, ct).ConfigureAwait(false);
                    if (media.Duration.HasValue) job.Duration = media.Duration;
                    job.Progress = PctProbe;
                    _log.Debug($"Формат: {media.FormatName}; длительность: {job.DurationText}; аудиодорожек: {media.AudioStreams.Count}", name, "Подготовка аудио");

                    var stream = MediaProbeService.SelectAudioStream(media, s.AudioTrackIndex, out var trackNote);
                    if (stream == null)
                    {
                        job.Status = JobStatus.Skipped;
                        job.Detail = "В файле нет звуковой дорожки";
                        job.Progress = 100;
                        _log.Warning("В файле нет звуковой дорожки — распознавать нечего", name, "Подготовка аудио");
                        return stats;
                    }
                    if (!string.IsNullOrEmpty(trackNote)) _log.Info(trackNote, name, "Подготовка аудио");

                    Directory.CreateDirectory(tempDir);
                    var checkpoint = LoadCheckpoint(tempDir, job, s, stream.AudioIndex, ctx.Tools.Model);
                    var wavPath = Path.Combine(tempDir, "audio.wav");

                    // 4. Извлечение аудио (или продолжение по контрольной точке).
                    if (checkpoint != null && File.Exists(wavPath))
                    {
                        _log.Info($"Восстановление после сбоя: найдено распознанных фрагментов — {checkpoint.Completed.Count} из {checkpoint.Chunks.Count}", name, "Восстановление");
                        stats.AudioSeconds = checkpoint.TotalSeconds;
                        job.Progress = PctSilenceEnd;
                    }
                    else
                    {
                        checkpoint = null;
                        CleanDirectory(tempDir);
                        CheckFreeSpace(tempDir, job.Duration, s.ChunkLengthSeconds);
                        await ctx.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);

                        job.Detail = "Извлечение аудиодорожки (FFmpeg)";
                        var t0 = total.Elapsed;
                        await _audio.ExtractAsync(job.FullPath, stream.AudioIndex, wavPath, job.Duration, new SyncProgress<double>(f => job.Progress = PctProbe + (PctExtractEnd - PctProbe) * f), ct).ConfigureAwait(false);
                        stats.ExtractSeconds = (total.Elapsed - t0).TotalSeconds;
                        stats.AudioSeconds = WavDurationSeconds(wavPath);
                        if (!job.Duration.HasValue && stats.AudioSeconds > 0) job.Duration = TimeSpan.FromSeconds(stats.AudioSeconds);
                        _log.Info($"Аудио извлечено за {stats.ExtractSeconds:0.0} с (длительность {TimeFormat.Clock(stats.AudioSeconds)})", name, "Подготовка аудио");

                        // 5. Паузы.
                        await ctx.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                        job.Detail = "Поиск пауз";
                        t0 = total.Elapsed;
                        var silences = await _silence.DetectAsync(wavPath, s.SilenceThresholdDb, s.MinSilenceSeconds, stats.AudioSeconds, ct).ConfigureAwait(false);
                        stats.SilenceSeconds = (total.Elapsed - t0).TotalSeconds;
                        job.Progress = PctSilenceEnd;

                        checkpoint = new Checkpoint
                        {
                            SourcePath = job.FullPath,
                            SourceSize = job.FileSize,
                            SourceTicks = job.LastWriteTimeUtc.Ticks,
                            ModelPath = ctx.Tools.Model,
                            ChunkLength = s.ChunkLengthSeconds,
                            AudioIndex = stream.AudioIndex,
                            TotalSeconds = stats.AudioSeconds,
                            Silences = silences.Select(r => new[] { r.Start, r.End }).ToList(),
                            Chunks = ChunkPlanner.Plan(stats.AudioSeconds, silences, s.ChunkLengthSeconds)
                        };
                        SaveCheckpoint(tempDir, checkpoint, s.KeepCheckpoints);
                        var silentTotal = silences.Sum(r => r.Length);
                        _log.Debug($"Найдено пауз: {silences.Count} (суммарно {TimeFormat.Clock(silentTotal)}); фрагментов: {checkpoint.Chunks.Count}", name, "Подготовка аудио");
                    }

                    var silenceRanges = checkpoint.Silences.Select(a => new TimeRange(a[0], a[1])).ToList();
                    double silentShare = stats.AudioSeconds > 0 ? silenceRanges.Sum(r => r.Length) / stats.AudioSeconds : 0;

                    // 6. Распознавание.
                    job.Status = JobStatus.Recognizing;
                    var chunkResults = new List<List<TranscriptSegment>>();
                    var recognitionClock = new PausableStopwatch(ctx.Gate);
                    try
                    {
                        if (silentShare >= 0.995 || stats.AudioSeconds < 0.3)
                        {
                            _log.Info("В аудио нет звука выше порога тишины — распознавание не требуется", name, "Распознавание");
                            job.Progress = PctRecognizeEnd;
                        }
                        else
                        {
                            await RecognizeChunksAsync(job, ctx, checkpoint, tempDir, wavPath, chunkResults, recognitionClock, total, ct).ConfigureAwait(false);
                            stats.Recognized = true;
                        }
                    }
                    finally
                    {
                        stats.RecognitionSeconds = recognitionClock.Elapsed.TotalSeconds;
                        recognitionClock.Dispose();
                    }

                    // 7. Обработка текста.
                    await ctx.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    job.Status = JobStatus.ProcessingText;
                    job.Detail = "Пунктуация, орфография, объединение фрагментов";
                    var tText = total.Elapsed;
                    var merged = SegmentMerger.Merge(checkpoint.Chunks, chunkResults);
                    var processed = _text.Process(merged, silenceRanges, TextProcessingOptions.From(s), name);
                    stats.TextSeconds = (total.Elapsed - tText).TotalSeconds;
                    job.Progress = PctTextEnd;
                    _log.Info($"Текст обработан: фраз {processed.Sentences.Count}, добавлено границ предложений {processed.SentenceBreaksAdded}, " +
                              $"исправлено опечаток {processed.SpellingCorrections}, слов вне словаря {processed.UnknownWords}", name, "Обработка текста");

                    // 8. Сохранение.
                    job.Status = JobStatus.Saving;
                    job.Detail = "Сохранение TXT";
                    var tSave = total.Elapsed;
                    if (processed.IsEmpty && s.EmptyResultBehavior == EmptyResultBehavior.DoNotCreate)
                    {
                        job.Detail = "Речь не обнаружена — файл не создан (см. настройки)";
                        _log.Info("Речь не обнаружена, TXT не создан согласно настройкам", name, "Сохранение");
                    }
                    else
                    {
                        var content = _export.Render(processed, s);
                        job.OutputPath = SaveWithFallback(job, decision.TargetPath, content, s);
                        job.Detail = processed.IsEmpty ? "Речь не обнаружена" : $"Фраз: {processed.Sentences.Count}";
                    }
                    stats.SaveSeconds = (total.Elapsed - tSave).TotalSeconds;

                    stats.TotalSeconds = total.Elapsed.TotalSeconds;
                    job.ProcessingTime = total.Elapsed;
                    job.Progress = 100;
                    job.Status = JobStatus.Completed;
                    _log.Info($"Завершено за {TimeFormat.Clock(total.Elapsed)}" + (string.IsNullOrEmpty(job.OutputPath) ? "" : $" → {job.OutputPath}"), name, "Задание", stats.ToString());
                    return stats;
                }
                catch (OperationCanceledException)
                {
                    keepTemp = s.KeepCheckpoints && ctx.KeepCheckpointsOnStop();
                    job.Status = JobStatus.Stopped;
                    job.Detail = keepTemp ? "Остановлено; прогресс сохранён для продолжения" : "Остановлено пользователем";
                    _log.Info("Обработка остановлена" + (keepTemp ? " (контрольная точка сохранена)" : ""), name, "Задание");
                    return stats;
                }
                catch (SaveException ex)
                {
                    Fail(job, ex.Message, ex.InnerException, ex.Details, "Сохранение");
                    return stats;
                }
                catch (ExternalToolException ex)
                {
                    Fail(job, ex.Message, ex.InnerException, ex.Details, StageOf(job.Status));
                    return stats;
                }
                catch (InsufficientSpaceException ex)
                {
                    Fail(job, ex.Message, null, null, "Подготовка аудио");
                    return stats;
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    Fail(job, "Недостаточно места на диске для временных файлов.", ex, null, StageOf(job.Status));
                    return stats;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Fail(job, "Ошибка доступа к файлу: " + ex.Message, ex, null, StageOf(job.Status));
                    return stats;
                }
                catch (Exception ex)
                {
                    Fail(job, "Непредвиденная ошибка: " + ex.Message, ex, null, StageOf(job.Status));
                    return stats;
                }
                finally
                {
                    if (job.ProcessingTime == TimeSpan.Zero) job.ProcessingTime = total.Elapsed;
                    if (!keepTemp) TryDeleteDirectory(tempDir);
                }
            }
        }

        private async Task RecognizeChunksAsync(VideoJob job, ProcessingContext ctx, Checkpoint cp, string tempDir, string wavPath,
            List<List<TranscriptSegment>> results, PausableStopwatch recognitionClock, PausableStopwatch total, CancellationToken ct)
        {
            var s = ctx.Settings;
            var name = job.FileName;
            var customPrompt = BuildCustomPrompt(s);
            int count = cp.Chunks.Count;
            double totalAudio = Math.Max(0.001, cp.TotalSeconds);
            double doneAudio = cp.Chunks.Where(c => cp.Completed.ContainsKey(c.Index)).Sum(c => c.NominalEnd - c.Start);
            _log.Info($"Распознавание: {_engine.EngineName}, модель «{Path.GetFileName(ctx.Tools.Model)}», язык ru, потоков {ctx.ThreadsPerJob}, " +
                      $"beam {s.BeamSize}, GPU {(s.UseGpu ? "разрешён" : "выключен")}, контекст между окнами {(s.CarryContextBetweenWindows ? "да" : "нет")}, " +
                      $"фрагментов {count}", name, "Распознавание");

            foreach (var chunk in cp.Chunks)
            {
                if (cp.Completed.TryGetValue(chunk.Index, out var saved))
                {
                    results.Add(saved);
                    continue;
                }

                await ctx.Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                job.Detail = count > 1 ? $"Фрагмент {chunk.Index + 1} из {count}" : "Распознавание речи";

                string chunkFile;
                bool temporaryChunk = count > 1;
                if (temporaryChunk)
                {
                    chunkFile = $"chunk-{chunk.Index:000}.wav";
                    await _audio.ExtractChunkAsync(wavPath, chunk.Start, chunk.Length, Path.Combine(tempDir, chunkFile), ct).ConfigureAwait(false);
                }
                else
                {
                    chunkFile = "audio.wav";
                }

                var prompt = BuildPrompt(customPrompt, s.UsePreviousTextAsPrompt ? results.LastOrDefault() : null);
                double chunkDone = doneAudio;
                var request = new ChunkTranscriptionRequest
                {
                    WorkingDirectory = tempDir,
                    ChunkFileName = chunkFile,
                    ModelPath = ctx.Tools.Model,
                    Language = s.Language,
                    Prompt = prompt,
                    Threads = ctx.ThreadsPerJob,
                    BeamSize = s.BeamSize,
                    UseGpu = s.UseGpu,
                    CarryContext = s.CarryContextBetweenWindows,
                    Priority = s.WorkerPriority,
                    OffsetSeconds = chunk.Start,
                    ChunkLengthSeconds = chunk.Length,
                    ChunkIndex = chunk.Index
                };

                var progress = new SyncProgress<ChunkProgress>(p =>
                {
                    var nominal = chunk.NominalEnd - chunk.Start;
                    var inChunk = Math.Min(nominal, Math.Max(p.Fraction * chunk.Length, p.ConfirmedSeconds));
                    var recognized = chunkDone + Math.Max(0, inChunk);
                    job.Progress = PctSilenceEnd + (PctRecognizeEnd - PctSilenceEnd) * Math.Min(1, recognized / totalAudio);
                    LiveProgress?.Invoke(job, new LiveJobProgress
                    {
                        AudioSeconds = totalAudio,
                        RecognizedSeconds = recognized - (cp.ResumedAudio),
                        RecognitionElapsed = recognitionClock.Elapsed.TotalSeconds,
                        JobElapsed = total.Elapsed.TotalSeconds
                    });
                });

                List<TranscriptSegment> segments;
                try
                {
                    segments = await _engine.TranscribeChunkAsync(request, progress, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (temporaryChunk) AtomicFile.TryDelete(Path.Combine(tempDir, chunkFile));
                }

                results.Add(segments);
                cp.Completed[chunk.Index] = segments;
                SaveCheckpoint(tempDir, cp, s.KeepCheckpoints);
                doneAudio += chunk.NominalEnd - chunk.Start;
                job.Progress = PctSilenceEnd + (PctRecognizeEnd - PctSilenceEnd) * Math.Min(1, doneAudio / totalAudio);
                _log.Debug($"Фрагмент {chunk.Index + 1}/{count} ({TimeFormat.Clock(chunk.Start)}–{TimeFormat.Clock(chunk.End)}, " +
                           $"{(chunk.CutAtSilence ? "граница по паузе" : "жёсткая граница с перекрытием")}): сегментов {segments.Count}", name, "Распознавание");
            }
            job.Progress = PctRecognizeEnd;
        }

        private static string BuildCustomPrompt(AppSettings s)
        {
            if (!s.UseCustomWordsAsPrompt || s.CustomWords == null || s.CustomWords.Count == 0) return "";
            // Подсказка ограничена половиной контекста модели (~224 токена) — берём первые термины.
            var sb = new StringBuilder();
            foreach (var w in s.CustomWords)
            {
                if (sb.Length + w.Length > 300) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(w);
            }
            return sb.Length > 0 ? "Термины: " + sb + "." : "";
        }

        /// <summary>Подсказка: пользовательские термины + окончание предыдущего фрагмента (как condition_on_previous_text в Whisper).</summary>
        private static string BuildPrompt(string custom, List<TranscriptSegment> previous)
        {
            var tail = "";
            if (previous != null && previous.Count > 0)
            {
                var text = string.Join(" ", previous.Skip(Math.Max(0, previous.Count - 3)).Select(x => x.Text)).Trim();
                if (text.Length > 200)
                {
                    text = text.Substring(text.Length - 200);
                    var space = text.IndexOf(' ');
                    if (space > 0) text = text.Substring(space + 1);
                }
                tail = text;
            }
            return (custom + " " + tail).Trim();
        }

        private string SaveWithFallback(VideoJob job, string target, string content, AppSettings s)
        {
            try
            {
                return _export.Save(job, target, content, s);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                // Результат распознавания не должен теряться: сохраняем резервную копию.
                string recovered = null;
                try
                {
                    Directory.CreateDirectory(_paths.RecoveredDirectory);
                    recovered = Path.Combine(_paths.RecoveredDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss} {PathUtil.SanitizeFileName(Path.GetFileNameWithoutExtension(job.FileName))}.txt");
                    AtomicFile.WriteAllText(recovered, content, new UTF8Encoding(s.WriteUtf8Bom));
                }
                catch
                {
                    recovered = null;
                }
                var msg = $"Не удалось сохранить «{target}»: {ex.Message}";
                if (recovered != null) msg += $". Расшифровка сохранена в резервную копию: {recovered}";
                throw new SaveException(msg, ex, recovered);
            }
        }

        private void Fail(VideoJob job, string message, Exception ex, string details, string stage)
        {
            job.Status = JobStatus.Error;
            job.Error = message;
            job.Detail = "";
            _log.Error(message, ex, job.FileName, stage, details);
        }

        private static string StageOf(JobStatus status) => StatusNames.Get(status);

        private static void CheckReadable(string path)
        {
            if (!File.Exists(path)) throw new IOException($"Файл больше не существует: «{path}»");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
            }
        }

        private static void CheckFreeSpace(string dir, TimeSpan? duration, int chunkSeconds)
        {
            if (!duration.HasValue) return;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;
                // WAV 16 кГц моно 16 бит = 32 000 байт/с; плюс фрагмент и запас 50 МБ.
                long need = (long)((duration.Value.TotalSeconds + chunkSeconds + 30) * 32000 * 1.1) + 50L * 1024 * 1024;
                if (drive.AvailableFreeSpace < need)
                {
                    throw new InsufficientSpaceException(
                        $"Недостаточно места на диске {drive.Name}: для временных файлов нужно около {need / 1048576} МБ, свободно {drive.AvailableFreeSpace / 1048576} МБ.");
                }
            }
            catch (InsufficientSpaceException)
            {
                throw;
            }
            catch
            {
                // Сетевые диски и т. п. — проверка невозможна, ошибку записи обработаем позже.
            }
        }

        private static bool IsDiskFull(IOException ex)
        {
            const int ERROR_HANDLE_DISK_FULL = 0x27, ERROR_DISK_FULL = 0x70;
            int code = ex.HResult & 0xFFFF;
            return code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL
                   || ex.Message.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Длительность WAV PCM 16 кГц моно 16 бит по размеру данных.</summary>
        public static double WavDurationSeconds(string path)
        {
            var len = new FileInfo(path).Length;
            return Math.Max(0, (len - 44) / 32000.0);
        }

        private static void CleanDirectory(string dir)
        {
            foreach (var f in Directory.GetFiles(dir)) AtomicFile.TryDelete(f);
        }

        public static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Файл может быть ещё занят завершающимся процессом — удалим при следующем запуске.
            }
        }

        // ---------- Контрольные точки ----------

        private sealed class Checkpoint
        {
            public string SourcePath { get; set; }
            public long SourceSize { get; set; }
            public long SourceTicks { get; set; }
            public string ModelPath { get; set; }
            public int ChunkLength { get; set; }
            public int AudioIndex { get; set; }
            public double TotalSeconds { get; set; }
            public List<double[]> Silences { get; set; } = new List<double[]>();
            public List<AudioChunk> Chunks { get; set; } = new List<AudioChunk>();
            public Dictionary<int, List<TranscriptSegment>> Completed { get; set; } = new Dictionary<int, List<TranscriptSegment>>();

            /// <summary>Сколько аудио было распознано до перезапуска (для честной оценки скорости).</summary>
            [JsonIgnore]
            public double ResumedAudio { get; set; }
        }

        private Checkpoint LoadCheckpoint(string dir, VideoJob job, AppSettings s, int audioIndex, string model)
        {
            var path = Path.Combine(dir, "checkpoint.json");
            if (!File.Exists(path)) return null;
            try
            {
                var cp = JsonConvert.DeserializeObject<Checkpoint>(File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Settings);
                bool valid = cp != null
                             && PathUtil.PathsEqual(cp.SourcePath, job.FullPath)
                             && cp.SourceSize == job.FileSize
                             && cp.SourceTicks == job.LastWriteTimeUtc.Ticks
                             && cp.ChunkLength == s.ChunkLengthSeconds
                             && cp.AudioIndex == audioIndex
                             && cp.Chunks.Count > 0
                             && PathUtil.PathsEqual(cp.ModelPath, model);
                if (!valid)
                {
                    _log.Debug("Контрольная точка не подходит (изменились файл или настройки) — обработка начнётся заново", job.FileName, "Восстановление");
                    return null;
                }
                cp.ResumedAudio = cp.Chunks.Where(c => cp.Completed.ContainsKey(c.Index)).Sum(c => c.NominalEnd - c.Start);
                return cp;
            }
            catch (Exception ex)
            {
                _log.Warning("Контрольная точка повреждена — обработка начнётся заново", job.FileName, "Восстановление", ex.Message);
                return null;
            }
        }

        private static void SaveCheckpoint(string dir, Checkpoint cp, bool enabled)
        {
            if (!enabled) return;
            try
            {
                AtomicFile.WriteAllText(Path.Combine(dir, "checkpoint.json"), JsonConvert.SerializeObject(cp, Formatting.None), new UTF8Encoding(false));
            }
            catch
            {
                // Контрольная точка — дополнительная защита; её отсутствие не мешает обработке.
            }
        }
    }

    public sealed class InsufficientSpaceException : Exception
    {
        public InsufficientSpaceException(string message) : base(message)
        {
        }
    }

    public sealed class SaveException : Exception
    {
        public SaveException(string message, Exception inner, string recoveredPath) : base(message, inner)
        {
            RecoveredPath = recoveredPath;
        }

        public string RecoveredPath { get; }
        public string Details => RecoveredPath == null ? "" : "Резервная копия: " + RecoveredPath;
    }

    /// <summary>IProgress без захвата контекста синхронизации: обновления применяются сразу в потоке вызова.</summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler?.Invoke(value);
    }
}
