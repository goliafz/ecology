using System;
using System.Collections.Generic;

namespace LetopisVks.Core
{
    /// <summary>
    /// «Ворота» записи: помнят, когда запись шла, а когда стояла на паузе.
    /// Видео и звук спрашивают у них одно и то же, поэтому остаются синхронными.
    /// </summary>
    internal sealed class ActivityGate
    {
        private readonly object _lock = new object();
        private readonly List<long> _opens = new List<long>();
        private readonly List<long> _closes = new List<long>();

        public bool IsOpen
        {
            get { lock (_lock) return _opens.Count > _closes.Count; }
        }

        public bool EverOpened
        {
            get { lock (_lock) return _opens.Count > 0; }
        }

        public void Open(long ticks)
        {
            lock (_lock)
            {
                if (_opens.Count > _closes.Count) return;
                if (_closes.Count > 0 && ticks < _closes[_closes.Count - 1]) ticks = _closes[_closes.Count - 1];
                _opens.Add(ticks);
            }
        }

        public void Close(long ticks)
        {
            lock (_lock)
            {
                if (_opens.Count <= _closes.Count) return;
                _closes.Add(Math.Max(ticks, _opens[_opens.Count - 1]));
            }
        }

        /// <summary>Сколько секунд запись была активна к моменту <paramref name="ticks"/>.</summary>
        public double ActiveSeconds(long ticks)
        {
            lock (_lock)
            {
                long total = 0;
                for (int i = 0; i < _opens.Count; i++)
                {
                    long start = _opens[i];
                    long end = i < _closes.Count ? _closes[i] : long.MaxValue;
                    if (ticks <= start) break;
                    total += Math.Min(end, ticks) - start;
                }
                return Clock.ToSeconds(total);
            }
        }

        public bool IsOpenAt(long ticks)
        {
            lock (_lock)
            {
                for (int i = _opens.Count - 1; i >= 0; i--)
                {
                    if (ticks < _opens[i]) continue;
                    long end = i < _closes.Count ? _closes[i] : long.MaxValue;
                    return ticks < end;
                }
                return false;
            }
        }

        /// <summary>Есть ли внутри промежутка [from, to) хоть один момент записи.</summary>
        public bool AnyOpenBetween(long from, long to)
        {
            lock (_lock)
            {
                for (int i = 0; i < _opens.Count; i++)
                {
                    long start = _opens[i];
                    long end = i < _closes.Count ? _closes[i] : long.MaxValue;
                    if (start < to && end > from) return true;
                }
                return false;
            }
        }

        /// <summary>Целиком ли промежуток [from, to) попадает в один отрезок записи.</summary>
        public bool FullyOpenBetween(long from, long to)
        {
            lock (_lock)
            {
                for (int i = 0; i < _opens.Count; i++)
                {
                    long start = _opens[i];
                    long end = i < _closes.Count ? _closes[i] : long.MaxValue;
                    if (start <= from && end >= to) return true;
                }
                return false;
            }
        }
    }
}
