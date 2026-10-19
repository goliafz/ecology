using System;
using System.Collections.Generic;
using System.Threading;
using MasterVks.Core;
using NAudio.CoreAudioApi;

namespace MasterVks.Audio
{
    /// <summary>Блок звука на 10 мс: по массиву на каждый источник.</summary>
    internal sealed class AudioBlock
    {
        /// <summary>Момент (по <see cref="Clock"/>), к которому относится первый сэмпл блока.</summary>
        public long SourceTicks;
        public int Frames;
        public float[] Mic1;
        public float[] Mic2;
        public float[] Mic3;
        /// <summary>Звук компьютера, стерео через один (L, R, L, R…).</summary>
        public float[] Sys;
        public int Mic1Real;
        public int Mic2Real;
        public int Mic3Real;
        public int SysReal;
    }

    /// <summary>
    /// Звуковое ядро. Работает всё время, пока открыта программа: индикаторы
    /// уровня живые, проверка и запись берут звук отсюда. Каждые 10 мс по
    /// единым часам выдаётся ровно 480 сэмплов на каждый источник — даже если
    /// микрофон отвалился (тогда это тишина). Поэтому длительность звука
    /// в записи всегда совпадает с длительностью видео.
    /// </summary>
    internal sealed class AudioEngine : IDisposable
    {
        public const int Rate = CaptureConverter.TargetRate;
        public const int BlockFrames = Rate / 100;

        public readonly AudioSource Mic1 = new AudioSource("Микрофон 1", false);
        public readonly AudioSource Mic2 = new AudioSource("Микрофон 2", false);
        public readonly AudioSource Mic3 = new AudioSource("Микрофон 3", false);
        public readonly AudioSource System = new AudioSource("Звук собеседников", true);

        private readonly object _subscribersLock = new object();
        private readonly List<Action<AudioBlock>> _subscribers = new List<Action<AudioBlock>>();
        private Thread _mixer;
        private Thread _supervisor;
        private volatile bool _stop;
        private volatile int _offsetMs;
        private long _lastMixerError;

        public int OffsetMs
        {
            get { return _offsetMs; }
            set { _offsetMs = value; }
        }

        public IEnumerable<AudioSource> Sources
        {
            get
            {
                yield return Mic1;
                yield return Mic2;
                yield return Mic3;
                yield return System;
            }
        }

        public void Start()
        {
            _supervisor = new Thread(SupervisorLoop) { IsBackground = true, Name = "Надзор за звуковыми устройствами" };
            _supervisor.SetApartmentState(ApartmentState.MTA);
            _supervisor.Start();

            _mixer = new Thread(MixerLoop) { IsBackground = true, Name = "Сведение звука", Priority = ThreadPriority.Highest };
            _mixer.Start();
        }

        public void Subscribe(Action<AudioBlock> handler)
        {
            lock (_subscribersLock) _subscribers.Add(handler);
        }

        public void Unsubscribe(Action<AudioBlock> handler)
        {
            lock (_subscribersLock) _subscribers.Remove(handler);
        }

        private void SupervisorLoop()
        {
            while (!_stop)
            {
                foreach (AudioSource source in Sources)
                {
                    try
                    {
                        source.Maintain();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(source.Title + ": сбой надзора за устройством", ex);
                    }
                }
                Thread.Sleep(250);
            }
            foreach (AudioSource source in Sources) source.Dispose();
        }

        private void MixerLoop()
        {
            long start = Clock.Now;
            long emitted = 0;
            while (!_stop)
            {
                try
                {
                    long due = (long)((Clock.Now - start) * (double)Rate / Clock.Frequency);
                    int guard = 0;
                    while (emitted + BlockFrames <= due && guard++ < 1000 && !_stop)
                    {
                        long blockTicks = start + (long)(emitted * (double)Clock.Frequency / Rate);
                        ProduceBlock(blockTicks);
                        emitted += BlockFrames;
                    }
                }
                catch (Exception ex)
                {
                    long now = Clock.Now;
                    if (now - _lastMixerError > Clock.Frequency * 10)
                    {
                        _lastMixerError = now;
                        Log.Warn("Сбой в потоке сведения звука", ex);
                    }
                }
                Thread.Sleep(4);
            }
        }

        private void ProduceBlock(long blockTicks)
        {
            var block = new AudioBlock
            {
                Frames = BlockFrames,
                SourceTicks = blockTicks - Clock.FromSeconds(_offsetMs / 1000.0),
                Mic1 = new float[BlockFrames],
                Mic2 = new float[BlockFrames],
                Mic3 = new float[BlockFrames],
                Sys = new float[BlockFrames * 2]
            };
            block.Mic1Real = Mic1.ReadBlock(block.Mic1, BlockFrames);
            block.Mic2Real = Mic2.ReadBlock(block.Mic2, BlockFrames);
            block.Mic3Real = Mic3.ReadBlock(block.Mic3, BlockFrames);
            block.SysReal = System.ReadBlock(block.Sys, BlockFrames);

            Action<AudioBlock>[] handlers;
            lock (_subscribersLock) handlers = _subscribers.ToArray();
            foreach (Action<AudioBlock> handler in handlers)
            {
                try
                {
                    handler(block);
                }
                catch (Exception ex)
                {
                    long now = Clock.Now;
                    if (now - _lastMixerError > Clock.Frequency * 10)
                    {
                        _lastMixerError = now;
                        Log.Warn("Ошибка у получателя звука", ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_mixer != null) _mixer.Join(2000);
            if (_supervisor != null) _supervisor.Join(5000);
        }
    }

    internal sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class AudioDevices
    {
        /// <summary>Список включённых устройств. Вызывать не из потока интерфейса (нужен MTA).</summary>
        public static List<AudioDeviceInfo> List(bool capture)
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(capture ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
                    {
                        try
                        {
                            result.Add(new AudioDeviceInfo { Id = device.ID, Name = device.FriendlyName });
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Не удалось прочитать имя звукового устройства", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось получить список звуковых устройств", ex);
            }
            return result;
        }

        public static string DefaultCaptureId()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return null;
                    return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
