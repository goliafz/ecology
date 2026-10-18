using System;
using System.Collections.Generic;
using System.Threading;
using MasterVks.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MasterVks.Audio
{
    internal struct ToneStep
    {
        public double Frequency;
        public int DurationMs;
        public float Amplitude;

        public ToneStep(double frequency, int durationMs, float amplitude)
        {
            Frequency = frequency;
            DurationMs = durationMs;
            Amplitude = amplitude;
        }
    }

    /// <summary>Генератор проверочного сигнала с плавным началом и концом (без щелчков).</summary>
    internal sealed class ToneProvider : ISampleProvider
    {
        private const int Rate = 48000;
        private const int FadeFrames = Rate / 100;
        private readonly List<ToneStep> _steps;
        private int _step;
        private int _frameInStep;
        private double _phase;

        public ToneProvider(IEnumerable<ToneStep> steps)
        {
            _steps = new List<ToneStep>(steps);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        }

        public WaveFormat WaveFormat { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int written = 0;
            while (written < frames && _step < _steps.Count)
            {
                ToneStep step = _steps[_step];
                int stepFrames = step.DurationMs * Rate / 1000;
                if (_frameInStep >= stepFrames)
                {
                    _step++;
                    _frameInStep = 0;
                    continue;
                }
                float envelope = 1f;
                if (_frameInStep < FadeFrames) envelope = _frameInStep / (float)FadeFrames;
                int left = stepFrames - _frameInStep;
                if (left < FadeFrames) envelope = Math.Min(envelope, left / (float)FadeFrames);

                float value = 0f;
                if (step.Frequency > 0)
                {
                    value = (float)Math.Sin(_phase) * step.Amplitude * envelope;
                    _phase += 2 * Math.PI * step.Frequency / Rate;
                    if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                }
                buffer[offset + written * 2] = value;
                buffer[offset + written * 2 + 1] = value;
                written++;
                _frameInStep++;
            }
            return written * 2;
        }
    }

    internal static class TonePlayer
    {
        /// <summary>
        /// Проигрывает сигнал на выбранном устройстве вывода и ждёт окончания.
        /// Вызывать из фонового (MTA) потока. Возвращает null при успехе или текст ошибки.
        /// </summary>
        public static string Play(string renderDeviceId, IList<ToneStep> steps, out long startedTicks)
        {
            startedTicks = Clock.Now;
            int totalMs = 0;
            foreach (ToneStep s in steps) totalMs += s.DurationMs;

            MMDeviceEnumerator enumerator = null;
            WasapiOut output = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                MMDevice device = null;
                if (!string.IsNullOrEmpty(renderDeviceId))
                {
                    try
                    {
                        device = enumerator.GetDevice(renderDeviceId);
                        if (device.State != DeviceState.Active) device = null;
                    }
                    catch (Exception)
                    {
                        device = null;
                    }
                }
                if (device == null)
                {
                    if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                        return "в Windows нет устройства вывода звука";
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                using (var finished = new ManualResetEvent(false))
                {
                    output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                    output.PlaybackStopped += (sender, args) => finished.Set();
                    output.Init(new SampleToWaveProviderLocal(new ToneProvider(steps)));
                    startedTicks = Clock.Now;
                    output.Play();
                    if (!finished.WaitOne(totalMs + 3000)) Log.Warn("Проверочный сигнал не закончился вовремя");
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось проиграть проверочный сигнал", ex);
                return AudioSource.Humanize(ex);
            }
            finally
            {
                try
                {
                    if (output != null)
                    {
                        output.Stop();
                        output.Dispose();
                    }
                }
                catch (Exception)
                {
                }
                if (enumerator != null) enumerator.Dispose();
            }
        }

        /// <summary>ISampleProvider → IWaveProvider (float), без зависимостей от вспомогательных классов NAudio.</summary>
        private sealed class SampleToWaveProviderLocal : IWaveProvider
        {
            private readonly ISampleProvider _source;
            private float[] _buffer = new float[0];

            public SampleToWaveProviderLocal(ISampleProvider source)
            {
                _source = source;
            }

            public WaveFormat WaveFormat
            {
                get { return _source.WaveFormat; }
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int samples = count / 4;
                if (_buffer.Length < samples) _buffer = new float[samples];
                int read = _source.Read(_buffer, 0, samples);
                Buffer.BlockCopy(_buffer, 0, buffer, offset, read * 4);
                return read * 4;
            }
        }
    }
}
