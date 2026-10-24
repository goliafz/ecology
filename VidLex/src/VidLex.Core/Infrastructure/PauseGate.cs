using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VidLex.Core.Infrastructure
{
    /// <summary>
    /// «Шлагбаум» паузы. Задания проверяют его в контрольных точках (перед новым этапом,
    /// фрагментом или видео) и ждут, пока пауза не будет снята.
    /// </summary>
    public sealed class PauseGate
    {
        private readonly object _sync = new object();
        private TaskCompletionSource<bool> _resumed = CompletedSource();

        public bool IsPaused { get; private set; }

        public event Action<bool> PausedChanged;

        public void Pause()
        {
            lock (_sync)
            {
                if (IsPaused) return;
                IsPaused = true;
                _resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            PausedChanged?.Invoke(true);
        }

        public void Resume()
        {
            TaskCompletionSource<bool> toRelease;
            lock (_sync)
            {
                if (!IsPaused) return;
                IsPaused = false;
                toRelease = _resumed;
            }
            toRelease.TrySetResult(true);
            PausedChanged?.Invoke(false);
        }

        /// <summary>Возвращает true, если пришлось ждать.</summary>
        public async Task<bool> WaitIfPausedAsync(CancellationToken ct)
        {
            Task wait;
            lock (_sync)
            {
                if (!IsPaused) return false;
                wait = _resumed.Task;
            }
            var cancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancel.TrySetResult(true)))
            {
                await Task.WhenAny(wait, cancel.Task).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return true;
        }

        private static TaskCompletionSource<bool> CompletedSource()
        {
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetResult(true);
            return tcs;
        }
    }

    /// <summary>Секундомер, который останавливается на время паузы очереди.</summary>
    public sealed class PausableStopwatch : IDisposable
    {
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly PauseGate _gate;

        public PausableStopwatch(PauseGate gate)
        {
            _gate = gate;
            if (_gate != null) _gate.PausedChanged += OnPausedChanged;
            if (_gate == null || !_gate.IsPaused) _sw.Start();
        }

        public TimeSpan Elapsed => _sw.Elapsed;

        private void OnPausedChanged(bool paused)
        {
            if (paused) _sw.Stop();
            else _sw.Start();
        }

        public void Dispose()
        {
            _sw.Stop();
            if (_gate != null) _gate.PausedChanged -= OnPausedChanged;
        }
    }
}
