using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace LetopisVks.Audio
{
    /// <summary>
    /// Переводит звук устройства (любой формат Windows) в float 48 кГц
    /// с нужным числом каналов: 1 для микрофона, 2 для звука компьютера.
    /// </summary>
    internal sealed class CaptureConverter
    {
        public const int TargetRate = 48000;

        private static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private enum SampleKind
        {
            Float32,
            Pcm16,
            Pcm24,
            Pcm32
        }

        private readonly SampleKind _kind;
        private readonly int _inChannels;
        private readonly int _outChannels;
        private readonly int _bytesPerSample;
        private readonly int _inRate;
        private readonly WdlResampler _resampler;
        private float[] _converted = new float[0];
        private float[] _resampled = new float[0];

        public string Description { get; private set; }

        public CaptureConverter(WaveFormat format, int outChannels)
        {
            if (format == null) throw new ArgumentNullException("format");
            _outChannels = outChannels;
            _inChannels = Math.Max(1, format.Channels);
            _inRate = format.SampleRate;
            _bytesPerSample = format.BitsPerSample / 8;

            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
            var ext = format as WaveFormatExtensible;
            if (ext != null)
            {
                isFloat = ext.SubFormat == FloatSubFormat;
                isPcm = ext.SubFormat == PcmSubFormat;
            }

            if (isFloat && format.BitsPerSample == 32) _kind = SampleKind.Float32;
            else if (isPcm && format.BitsPerSample == 16) _kind = SampleKind.Pcm16;
            else if (isPcm && format.BitsPerSample == 24) _kind = SampleKind.Pcm24;
            else if (isPcm && format.BitsPerSample == 32) _kind = SampleKind.Pcm32;
            else throw new NotSupportedException("Неподдерживаемый формат звука: " + format);

            if (_inRate <= 0) throw new NotSupportedException("Неизвестная частота звука устройства.");
            if (_inRate != TargetRate)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false, 64, 32);
                _resampler.SetFilterParms(0.693f, 0.707f);
                _resampler.SetFeedMode(true);
                _resampler.SetRates(_inRate, TargetRate);
            }

            Description = _inRate + " Гц, " + _inChannels + " кан., " + _kind;
        }

        /// <summary>Возвращает число готовых кадров в <paramref name="output"/> (массив может быть заменён на больший).</summary>
        public int Process(byte[] data, int byteCount, ref float[] output)
        {
            int frameBytes = _bytesPerSample * _inChannels;
            int frames = byteCount / frameBytes;
            if (frames <= 0) return 0;

            EnsureSize(ref _converted, frames * _outChannels);
            ConvertAndDownmix(data, frames, _converted);

            if (_resampler == null)
            {
                EnsureSize(ref output, frames * _outChannels);
                Array.Copy(_converted, output, frames * _outChannels);
                return frames;
            }

            float[] inBuffer;
            int inOffset;
            int need = _resampler.ResamplePrepare(frames, _outChannels, out inBuffer, out inOffset);
            int copyFrames = Math.Min(need, frames);
            Array.Copy(_converted, 0, inBuffer, inOffset, copyFrames * _outChannels);
            int maxOut = (int)((long)frames * TargetRate / _inRate) + 64;
            EnsureSize(ref _resampled, maxOut * _outChannels);
            int produced = _resampler.ResampleOut(_resampled, 0, copyFrames, maxOut, _outChannels);
            EnsureSize(ref output, produced * _outChannels);
            Array.Copy(_resampled, output, produced * _outChannels);
            return produced;
        }

        private void ConvertAndDownmix(byte[] data, int frames, float[] dst)
        {
            int frameBytes = _bytesPerSample * _inChannels;
            for (int f = 0; f < frames; f++)
            {
                int baseIndex = f * frameBytes;
                if (_outChannels == 1)
                {
                    float sum = 0;
                    for (int c = 0; c < _inChannels; c++) sum += ReadSample(data, baseIndex + c * _bytesPerSample);
                    dst[f] = sum / _inChannels;
                }
                else
                {
                    float left = ReadSample(data, baseIndex);
                    float right = _inChannels > 1 ? ReadSample(data, baseIndex + _bytesPerSample) : left;
                    if (_inChannels >= 3)
                    {
                        // Центральный канал (речь в 5.1/7.1) — в обе стороны.
                        float center = ReadSample(data, baseIndex + 2 * _bytesPerSample) * 0.7071f;
                        left += center;
                        right += center;
                    }
                    dst[f * 2] = left;
                    dst[f * 2 + 1] = right;
                }
            }
        }

        private float ReadSample(byte[] data, int index)
        {
            switch (_kind)
            {
                case SampleKind.Float32:
                    float v = BitConverter.ToSingle(data, index);
                    return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
                case SampleKind.Pcm16:
                    return (short)(data[index] | (data[index + 1] << 8)) / 32768f;
                case SampleKind.Pcm24:
                    int s24 = (data[index] << 8) | (data[index + 1] << 16) | (data[index + 2] << 24);
                    return (s24 >> 8) / 8388608f;
                default:
                    return BitConverter.ToInt32(data, index) / 2147483648f;
            }
        }

        private static void EnsureSize(ref float[] array, int size)
        {
            if (array == null || array.Length < size) array = new float[Math.Max(size, 256)];
        }
    }
}
