using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VidLex.Core.Models
{
    /// <summary>
    /// Задание на обработку одного видео. Свойства меняются из фоновых потоков;
    /// WPF корректно маршалит PropertyChanged скалярных свойств в поток интерфейса.
    /// </summary>
    public sealed class VideoJob : INotifyPropertyChanged
    {
        private JobStatus _status = JobStatus.Waiting;
        private double _progress;
        private TimeSpan? _duration;
        private TimeSpan _processingTime;
        private string _error = "";
        private string _detail = "";
        private string _outputPath = "";
        private bool _progressIsEstimate;

        public VideoJob(string fullPath, string rootFolder)
        {
            FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
            RootFolder = rootFolder ?? "";
            FileName = System.IO.Path.GetFileName(fullPath);
            RelativePath = Infrastructure.PathUtil.GetRelativePath(RootFolder, fullPath);
            Id = Infrastructure.PathUtil.StableHash(fullPath);
        }

        /// <summary>Стабильный идентификатор, зависящий только от полного пути.</summary>
        public string Id { get; }
        public string FullPath { get; }
        public string RootFolder { get; }
        public string FileName { get; }
        public string RelativePath { get; }

        /// <summary>Размер и дата изменения на момент сканирования — для проверки контрольных точек.</summary>
        public long FileSize { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }

        public JobStatus Status
        {
            get => _status;
            set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsFinished)); } }
        }

        public string StatusText => StatusNames.Get(_status);

        public bool IsFinished => _status == JobStatus.Completed || _status == JobStatus.Skipped || _status == JobStatus.Error;

        /// <summary>Прогресс текущего видео, 0…100.</summary>
        public double Progress
        {
            get => _progress;
            set => Set(ref _progress, Math.Max(0, Math.Min(100, value)));
        }

        /// <summary>true, если процент — оценка, а не данные движка.</summary>
        public bool ProgressIsEstimate
        {
            get => _progressIsEstimate;
            set => Set(ref _progressIsEstimate, value);
        }

        public TimeSpan? Duration
        {
            get => _duration;
            set { if (Set(ref _duration, value)) OnPropertyChanged(nameof(DurationText)); }
        }

        public string DurationText => _duration.HasValue ? Infrastructure.TimeFormat.Clock(_duration.Value) : "—";

        /// <summary>Чистое время обработки (без пауз).</summary>
        public TimeSpan ProcessingTime
        {
            get => _processingTime;
            set { if (Set(ref _processingTime, value)) OnPropertyChanged(nameof(ProcessingTimeText)); }
        }

        public string ProcessingTimeText => _processingTime > TimeSpan.Zero ? Infrastructure.TimeFormat.Clock(_processingTime) : "—";

        public string Error
        {
            get => _error;
            set => Set(ref _error, value ?? "");
        }

        /// <summary>Пояснение к текущему этапу (например, «фрагмент 2 из 5»).</summary>
        public string Detail
        {
            get => _detail;
            set => Set(ref _detail, value ?? "");
        }

        public string OutputPath
        {
            get => _outputPath;
            set => Set(ref _outputPath, value ?? "");
        }

        /// <summary>Сброс к состоянию «Ожидает» перед повторной обработкой.</summary>
        public void Reset()
        {
            Status = JobStatus.Waiting;
            Progress = 0;
            ProgressIsEstimate = false;
            ProcessingTime = TimeSpan.Zero;
            Error = "";
            Detail = "";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() => RelativePath;
    }

    public static class StatusNames
    {
        public static string Get(JobStatus status)
        {
            switch (status)
            {
                case JobStatus.Waiting: return "Ожидает";
                case JobStatus.PreparingAudio: return "Подготовка аудио";
                case JobStatus.Recognizing: return "Распознавание";
                case JobStatus.ProcessingText: return "Обработка текста";
                case JobStatus.Saving: return "Сохранение";
                case JobStatus.Completed: return "Завершено";
                case JobStatus.Skipped: return "Пропущено";
                case JobStatus.Error: return "Ошибка";
                case JobStatus.Stopped: return "Остановлено";
                default: return status.ToString();
            }
        }
    }
}
