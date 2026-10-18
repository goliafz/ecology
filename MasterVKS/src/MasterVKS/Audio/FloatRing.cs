using System;

namespace MasterVks.Audio
{
    /// <summary>
    /// Кольцевой буфер звука (кадры по N каналов). Не потокобезопасен — блокировка снаружи.
    /// При переполнении выбрасываются самые старые данные.
    /// </summary>
    internal sealed class FloatRing
    {
        private readonly float[] _buffer;
        private readonly int _channels;
        private int _readPos;
        private int _count;

        public FloatRing(int capacityFrames, int channels)
        {
            _channels = channels;
            _buffer = new float[capacityFrames * channels];
        }

        public int Channels
        {
            get { return _channels; }
        }

        public int FramesAvailable
        {
            get { return _count / _channels; }
        }

        public int CapacityFrames
        {
            get { return _buffer.Length / _channels; }
        }

        /// <summary>Возвращает число выброшенных старых кадров (если не хватило места).</summary>
        public int Write(float[] source, int frames)
        {
            int samples = frames * _channels;
            int dropped = 0;
            int offset = 0;
            if (samples > _buffer.Length)
            {
                offset = samples - _buffer.Length;
                dropped += offset / _channels;
                samples = _buffer.Length;
            }
            int overflow = _count + samples - _buffer.Length;
            if (overflow > 0)
            {
                _readPos = (_readPos + overflow) % _buffer.Length;
                _count -= overflow;
                dropped += overflow / _channels;
            }
            int writePos = (_readPos + _count) % _buffer.Length;
            int first = Math.Min(samples, _buffer.Length - writePos);
            Array.Copy(source, offset, _buffer, writePos, first);
            if (samples > first) Array.Copy(source, offset + first, _buffer, 0, samples - first);
            _count += samples;
            return dropped;
        }

        public int Read(float[] destination, int destinationFrameOffset, int frames)
        {
            int take = Math.Min(frames, FramesAvailable);
            int samples = take * _channels;
            int dst = destinationFrameOffset * _channels;
            int first = Math.Min(samples, _buffer.Length - _readPos);
            Array.Copy(_buffer, _readPos, destination, dst, first);
            if (samples > first) Array.Copy(_buffer, 0, destination, dst + first, samples - first);
            _readPos = (_readPos + samples) % _buffer.Length;
            _count -= samples;
            return take;
        }

        public void Skip(int frames)
        {
            int samples = Math.Min(frames, FramesAvailable) * _channels;
            _readPos = (_readPos + samples) % _buffer.Length;
            _count -= samples;
        }

        public void Clear()
        {
            _readPos = 0;
            _count = 0;
        }
    }
}
