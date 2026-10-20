using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using MasterVks.Audio;
using MasterVks.Core;
using MasterVks.Video;

namespace MasterVks.Recording
{
    /// <summary>
    /// Один запуск ffmpeg = один файл MKV. Обычно на всю запись он один; если ffmpeg
    /// вдруг упадёт, сессия тут же начнёт следующий фрагмент, а при остановке склеит их.
    /// </summary>
    internal sealed class RecordingSegment : IVideoTarget
    {
        private readonly object _queueLock = new object();
        private readonly BlockingCollection<FrameBuffer> _videoQueue = new BlockingCollection<FrameBuffer>();
        private readonly BlockingCollection<byte[]> _audioQueue = new BlockingCollection<byte[]>();
        private readonly LinkedList<string> _stderrTail = new LinkedList<string>();
        private readonly int _frameBytes;

        private NamedPipeServerStream _videoPipe;
        private NamedPipeServerStream _audioPipe;
        private Process _process;
        private Thread _videoWriter;
        private Thread _audioWriter;
        private FileStream _flushHandle;

        private long _videoQueued;
        private long _audioSamples;
        private long _videoWritten;
        private long _lastProgressTicks;
        private long _progressFrame = -1;
        private volatile bool _acceptVideo = true;
        private volatile bool _acceptAudio = true;
        private long _stopTicks = long.MaxValue;
        private volatile string _failure;
        private double _speed = 1;

        public readonly int Index;
        public readonly string FilePath;
        private readonly ActivityGate _gate = new ActivityGate();

        public ActivityGate Gate
        {
            get { return _gate; }
        }
        public readonly long CreatedTicks;
        public readonly ManualResetEvent AudioCompleted = new ManualResetEvent(false);

        public RecordingSegment(int index, string filePath, int frameBytes)
        {
            Index = index;
            FilePath = filePath;
            _frameBytes = frameBytes;
            CreatedTicks = Clock.Now;
            _lastProgressTicks = CreatedTicks;
        }

        public bool AcceptsVideo
        {
            get { return _acceptVideo; }
        }

        public long VideoFramesQueued
        {
            get { return Interlocked.Read(ref _videoQueued); }
        }

        public long VideoFramesWritten
        {
            get { return Interlocked.Read(ref _videoWritten); }
        }

        public long LastProgressTicks
        {
            get { return Interlocked.Read(ref _lastProgressTicks); }
        }

        public double Speed
        {
            get { return Volatile.Read(ref _speed); }
        }

        /// <summary>Причина, по которой фрагмент оборвался (null — всё в порядке).</summary>
        public string Failure
        {
            get { return _failure; }
        }

        public bool ProcessExited
        {
            get
            {
                try
                {
                    return _process == null || _process.HasExited;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        public string StderrTail
        {
            get
            {
                lock (_stderrTail) return string.Join(Environment.NewLine, _stderrTail);
            }
        }

        // ===================== Запуск =====================

        public void Start(string ffmpegPath, int width, int height, int fps, VideoQuality quality, string title)
        {
            string id = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Index + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string videoName = "MasterVKS-" + id + "-video";
            string audioName = "MasterVKS-" + id + "-audio";

            _videoPipe = new NamedPipeServerStream(videoName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 8 * 1024 * 1024);
            _audioPipe = new NamedPipeServerStream(audioName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1024 * 1024);

            List<string> args = RecordingArgs.Build(@"\\.\pipe\" + videoName, @"\\.\pipe\" + audioName, width, height, fps, quality, FilePath, title);
            var psi = new ProcessStartInfo(ffmpegPath, CommandLine.Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(FilePath)
            };
            Log.Info("Запуск ffmpeg (часть " + Index + "): " + psi.Arguments);

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnProgressLine;
            _process.ErrorDataReceived += OnErrorLine;
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            try
            {
                _process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch (Exception)
            {
            }
            PowerHelper.DisableThrottling(_process.Handle);

            _videoWriter = new Thread(VideoWriterLoop) { IsBackground = true, Name = "Передача видео в ffmpeg", Priority = ThreadPriority.AboveNormal };
            _audioWriter = new Thread(AudioWriterLoop) { IsBackground = true, Name = "Передача звука в ffmpeg", Priority = ThreadPriority.AboveNormal };
            _videoWriter.Start();
            _audioWriter.Start();
        }

        // ===================== Видео =====================

        public bool EnqueueVideo(FrameBuffer frame)
        {
            lock (_queueLock)
            {
                if (!_acceptVideo || _videoQueue.IsAddingCompleted) return false;
                _videoQueue.Add(frame);
                Interlocked.Increment(ref _videoQueued);
                return true;
            }
        }

        private void VideoWriterLoop()
        {
            try
            {
                WaitForConnection(_videoPipe, "видео");
                foreach (FrameBuffer frame in _videoQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_failure == null) _videoPipe.Write(frame.Data, 0, _frameBytes);
                        Interlocked.Increment(ref _videoWritten);
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
                _videoPipe.Flush();
            }
            catch (Exception ex)
            {
                Fail("канал видео: " + ex.Message, ex);
                DrainVideo();
            }
            finally
            {
                SafeDispose(_videoPipe);
            }
        }

        private void DrainVideo()
        {
            lock (_queueLock)
            {
                _acceptVideo = false;
                if (!_videoQueue.IsAddingCompleted) _videoQueue.CompleteAdding();
            }
            FrameBuffer frame;
            while (_videoQueue.TryTake(out frame)) frame.Release();
        }

        // ===================== Звук =====================

        /// <summary>
        /// Принимает блок звука от ядра и оставляет из него только ту часть, что
        /// попадает во время записи (с учётом пауз). Вызывается из потока сведения.
        /// </summary>
        /// <summary>Блок звука с простым сведением общей дорожки (без обработки).</summary>
        public void OnAudioBlock(AudioBlock block, bool mixMic1, bool mixMic2, bool mixMic3, bool mixSystem)
        {
            var mixL = new float[block.Frames];
            var mixR = new float[block.Frames];
            RoomMixer.SimpleMix(block, mixMic1, mixMic2, mixMic3, mixSystem, mixL, mixR);
            OnAudioBlock(block, mixL, mixR);
        }

        /// <summary>Блок звука с уже сведённой общей дорожкой (<see cref="RoomMixer"/>).</summary>
        public void OnAudioBlock(AudioBlock block, float[] mixL, float[] mixR)
        {
            if (!_acceptAudio) return;

            long start = block.SourceTicks;
            double ticksPerFrame = Clock.Frequency / (double)AudioEngine.Rate;
            long end = start + (long)(block.Frames * ticksPerFrame);
            long stopAt = Interlocked.Read(ref _stopTicks);

            int from = 0, count = block.Frames;
            bool[] mask = null;
            if (!Gate.AnyOpenBetween(start, end))
            {
                count = 0;
            }
            else if (!Gate.FullyOpenBetween(start, end))
            {
                mask = new bool[block.Frames];
                count = 0;
                for (int i = 0; i < block.Frames; i++)
                {
                    mask[i] = Gate.IsOpenAt(start + (long)(i * ticksPerFrame));
                    if (mask[i]) count++;
                }
            }

            if (count > 0)
            {
                var samples = new float[count * RecordingArgs.AudioChannels];
                int o = 0;
                for (int i = from; i < block.Frames; i++)
                {
                    if (mask != null && !mask[i]) continue;
                    samples[o++] = mixL[i];
                    samples[o++] = mixR[i];
                    samples[o++] = block.Mic1[i];
                    samples[o++] = block.Mic2[i];
                    samples[o++] = block.Mic3[i];
                    samples[o++] = block.Sys[i * 2];
                    samples[o++] = block.Sys[i * 2 + 1];
                }
                var bytes = new byte[samples.Length * 4];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                try
                {
                    _audioQueue.Add(bytes);
                    Interlocked.Add(ref _audioSamples, count);
                }
                catch (InvalidOperationException)
                {
                    // очередь уже закрыта — фрагмент завершается
                }
            }

            if (end >= stopAt) CompleteAudio();
        }

        public void CompleteAudio()
        {
            if (!_acceptAudio) return;
            _acceptAudio = false;
            try
            {
                _audioQueue.CompleteAdding();
            }
            catch (Exception)
            {
            }
            AudioCompleted.Set();
        }

        private void AudioWriterLoop()
        {
            try
            {
                WaitForConnection(_audioPipe, "звука");
                foreach (byte[] chunk in _audioQueue.GetConsumingEnumerable())
                {
                    if (_failure == null) _audioPipe.Write(chunk, 0, chunk.Length);
                }
                _audioPipe.Flush();
            }
            catch (Exception ex)
            {
                Fail("канал звука: " + ex.Message, ex);
                _acceptAudio = false;
                try
                {
                    _audioQueue.CompleteAdding();
                }
                catch (Exception)
                {
                }
                byte[] dummy;
                while (_audioQueue.TryTake(out dummy))
                {
                }
            }
            finally
            {
                SafeDispose(_audioPipe);
            }
        }

        // ===================== Общие =====================

        private void WaitForConnection(NamedPipeServerStream pipe, string what)
        {
            IAsyncResult ar = pipe.BeginWaitForConnection(null, null);
            if (!ar.AsyncWaitHandle.WaitOne(30000))
                throw new TimeoutException("ffmpeg не подключился к каналу " + what + " за 30 секунд");
            pipe.EndWaitForConnection(ar);
        }

        private void OnProgressLine(object sender, DataReceivedEventArgs e)
        {
            string line = e.Data;
            if (string.IsNullOrEmpty(line)) return;
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            string key = line.Substring(0, eq);
            string value = line.Substring(eq + 1).Trim();
            if (key == "frame")
            {
                long frame;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame) && frame != _progressFrame)
                {
                    _progressFrame = frame;
                    Interlocked.Exchange(ref _lastProgressTicks, Clock.Now);
                }
            }
            else if (key == "speed")
            {
                double speed;
                if (double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
                    Volatile.Write(ref _speed, speed);
            }
        }

        private void OnErrorLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Log.Ffmpeg("[часть " + Index + "] " + e.Data);
            lock (_stderrTail)
            {
                _stderrTail.AddLast(e.Data);
                while (_stderrTail.Count > 20) _stderrTail.RemoveFirst();
            }
        }

        public void Fail(string reason, Exception ex = null)
        {
            if (_failure != null) return;
            _failure = reason;
            Log.Warn("Фрагмент " + Index + " оборвался: " + reason, ex);
        }

        /// <summary>
        /// Раз в пару секунд просим Windows сбросить файл на диск. Если случится синий
        /// экран или ноутбук выключится, на диске останется почти всё записанное.
        /// </summary>
        public void FlushToDisk()
        {
            try
            {
                if (_flushHandle == null)
                {
                    if (!File.Exists(FilePath)) return;
                    _flushHandle = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }
                _flushHandle.Flush(true);
            }
            catch (Exception)
            {
                SafeDispose(_flushHandle);
                _flushHandle = null;
            }
        }

        public long FileSize
        {
            get
            {
                try
                {
                    var info = new FileInfo(FilePath);
                    return info.Exists ? info.Length : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Аккуратное завершение: дописываем остатки кадров и звука, закрываем каналы
        /// (для ffmpeg это «конец данных»), ждём, пока он допишет файл.
        /// </summary>
        public void Finish(long stopTicks, bool fast)
        {
            Gate.Close(stopTicks);
            Interlocked.Exchange(ref _stopTicks, stopTicks);

            lock (_queueLock)
            {
                _acceptVideo = false;
                if (!_videoQueue.IsAddingCompleted) _videoQueue.CompleteAdding();
            }

            // Звук отстаёт от картинки на величину буфера — ждём, пока дойдёт до момента остановки.
            if (!AudioCompleted.WaitOne(fast ? 500 : 3000)) CompleteAudio();

            int writerTimeout = fast ? 3000 : 30000;
            bool videoDone = _videoWriter == null || _videoWriter.Join(writerTimeout);
            bool audioDone = _audioWriter == null || _audioWriter.Join(writerTimeout);
            if (!videoDone || !audioDone)
            {
                Log.Warn("ffmpeg не принимает данные, завершаю его принудительно");
                Kill();
                if (_videoWriter != null) _videoWriter.Join(5000);
                if (_audioWriter != null) _audioWriter.Join(5000);
            }

            WaitForExit(fast ? 5000 : 60000);
            SafeDispose(_flushHandle);
            _flushHandle = null;
            SafeDispose(_process);
        }

        /// <summary>Вызывается, когда ffmpeg упал сам: просто освобождаем ресурсы.</summary>
        public void Abandon()
        {
            lock (_queueLock)
            {
                _acceptVideo = false;
                if (!_videoQueue.IsAddingCompleted) _videoQueue.CompleteAdding();
            }
            CompleteAudio();
            Kill();
            SafeDispose(_videoPipe);
            SafeDispose(_audioPipe);
            if (_videoWriter != null) _videoWriter.Join(5000);
            if (_audioWriter != null) _audioWriter.Join(5000);
            DrainVideo();
            SafeDispose(_flushHandle);
            _flushHandle = null;
            SafeDispose(_process);
        }

        private void WaitForExit(int timeoutMs)
        {
            if (_process == null) return;
            try
            {
                if (_process.WaitForExit(timeoutMs))
                {
                    _process.WaitForExit();
                    Log.Info("ffmpeg (часть " + Index + ") завершился, код " + _process.ExitCode);
                    return;
                }
                Log.Warn("ffmpeg не завершился за " + timeoutMs / 1000 + " с, прошу остановиться");
                try
                {
                    _process.StandardInput.Write('q');
                    _process.StandardInput.Flush();
                }
                catch (Exception)
                {
                }
                if (!_process.WaitForExit(10000)) Kill();
            }
            catch (Exception ex)
            {
                Log.Warn("Ошибка при ожидании ffmpeg", ex);
            }
        }

        public void Kill()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void SafeDispose(IDisposable d)
        {
            try
            {
                if (d != null) d.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
