using System;

namespace MasterVks.Audio
{
    /// <summary>
    /// Сведение общей дорожки для переговорной, где голоса собеседников звучат
    /// из динамиков (телевизора), а несколько микрофонов слышат комнату.
    ///
    /// Без обработки в общей дорожке каждый голос собеседника звучит дважды:
    /// сразу (звук компьютера) и с опозданием, когда микрофоны услышат его из
    /// телевизора. Телевизор по HDMI запаздывает на 50–200 мс, и это слышно как эхо.
    /// А голос человека в комнате приходит в несколько микрофонов в разное время
    /// и звучит гулко.
    ///
    /// Поэтому «умное» сведение:
    /// 1. Из каждого микрофона вычитается то, что он слышит из динамиков
    ///    (подавление эха по спектру; задержка телевизора находится сама).
    /// 2. Микрофоны смешиваются по Дугану: громче тот, к которому ближе
    ///    говорящий, остальные приглушаются. Гулкость и шум комнаты уходят.
    ///
    /// Обрабатывается только общая дорожка: отдельные дорожки микрофонов и
    /// звука компьютера остаются как есть, это страховка на любой случай.
    /// Общая дорожка запаздывает на <see cref="Latency"/> сэмплов (21 мс), звук
    /// компьютера в ней задерживается так же, чтобы всё совпадало.
    /// </summary>
    internal sealed class RoomMixer
    {
        public const int FrameSize = 1024;
        public const int Hop = FrameSize / 2;
        public const int Bins = FrameSize / 2 + 1;
        /// <summary>Задержка общей дорожки при умном сведении, сэмплов.</summary>
        public const int Latency = FrameSize;

        private readonly bool _smart;
        private readonly bool[] _useMic;
        private readonly bool _useSystem;
        private readonly EchoSuppressor[] _suppressors = new EchoSuppressor[3];
        private readonly FarEndHistory _farEnd = new FarEndHistory();
        private readonly Fft _fft = new Fft(FrameSize);
        private readonly float[] _window = Fft.SqrtHann(FrameSize);

        private readonly float[] _refFrame = new float[FrameSize];
        private int _filled;
        private readonly SampleQueue _sysDelayL = new SampleQueue(Latency);
        private readonly SampleQueue _sysDelayR = new SampleQueue(Latency);

        private readonly float[] _levels = new float[3];
        private readonly float[] _gains = { 1f, 1f, 1f };

        public RoomMixer(bool smart, bool mic1, bool mic2, bool mic3, bool system)
        {
            _smart = smart;
            _useMic = new[] { mic1, mic2, mic3 };
            _useSystem = system;
            for (int k = 0; k < 3; k++)
                if (_useMic[k]) _suppressors[k] = new EchoSuppressor(_fft, _window, _farEnd);
            int used = (mic1 ? 1 : 0) + (mic2 ? 1 : 0) + (mic3 ? 1 : 0);
            for (int k = 0; k < 3; k++) _gains[k] = used > 0 ? 1f / used : 0f;
        }

        public bool Smart
        {
            get { return _smart; }
        }

        /// <summary>Задержка телевизора, которую нашло подавление эха для микрофона (мс), или −1, пока не нашло.</summary>
        public int EchoDelayMs(int mic)
        {
            EchoSuppressor s = _suppressors[mic];
            return s == null || s.DelayFrames < 0 ? -1 : (int)Math.Round(s.DelayFrames * Hop * 1000.0 / AudioEngine.Rate);
        }

        /// <summary>Насколько подавлено эхо в микрофоне за последнее время, дБ (0 — не подавлялось).</summary>
        public double EchoReductionDb(int mic)
        {
            EchoSuppressor s = _suppressors[mic];
            return s == null ? 0 : s.ReductionDb;
        }

        /// <summary>Общая дорожка для блока: левый и правый каналы по числу кадров блока.</summary>
        public void Process(AudioBlock block, float[] outL, float[] outR)
        {
            if (!_smart)
            {
                SimpleMix(block, _useMic[0], _useMic[1], _useMic[2], _useSystem, outL, outR);
                return;
            }

            int n = block.Frames;
            float[][] mics = { block.Mic1, block.Mic2, block.Mic3 };
            for (int i = 0; i < n; i++)
            {
                // Опорный сигнал для подавления эха: то, что ушло в динамики.
                _refFrame[Hop + _filled] = 0.5f * (block.Sys[i * 2] + block.Sys[i * 2 + 1]);
                for (int k = 0; k < 3; k++)
                    if (_suppressors[k] != null) _suppressors[k].Input[Hop + _filled] = mics[k][i];
                _filled++;
                if (_filled == Hop)
                {
                    _farEnd.Push(_fft, _window, _refFrame);
                    for (int k = 0; k < 3; k++)
                        if (_suppressors[k] != null) _suppressors[k].ProcessFrame();
                    Array.Copy(_refFrame, Hop, _refFrame, 0, Hop);
                    _filled = 0;
                }
            }

            // Уровни микрофонов после подавления эха: по ним решаем, кто сейчас говорит.
            var processed = new float[3][];
            float total = 1e-6f;
            for (int k = 0; k < 3; k++)
            {
                if (_suppressors[k] == null) continue;
                processed[k] = _suppressors[k].Take(n);
                double sum = 0;
                for (int i = 0; i < n; i++) sum += processed[k][i] * processed[k][i];
                float rms = (float)Math.Sqrt(sum / n);
                // Быстро замечаем, что человек заговорил, и медленно (за 0,3 с) отпускаем.
                _levels[k] = rms > _levels[k] ? 0.5f * rms + 0.5f * _levels[k] : 0.967f * _levels[k] + 0.033f * rms;
                total += _levels[k];
            }

            // Смесь по Дугану: усиления в сумме дают единицу, поэтому шум комнаты не растёт с числом микрофонов.
            var target = new float[3];
            int active = 0;
            for (int k = 0; k < 3; k++) if (_suppressors[k] != null) active++;
            for (int k = 0; k < 3; k++)
            {
                if (_suppressors[k] == null) continue;
                target[k] = total > 1e-5f ? _levels[k] / total : 1f / active;
            }

            for (int i = 0; i < n; i++)
            {
                float t = (i + 1) / (float)n;
                float mic = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (processed[k] == null) continue;
                    float g = _gains[k] + (target[k] - _gains[k]) * t;
                    mic += processed[k][i] * g;
                }
                float sl = _sysDelayL.Shift(block.Sys[i * 2]);
                float sr = _sysDelayR.Shift(block.Sys[i * 2 + 1]);
                outL[i] = SoftLimit(mic + (_useSystem ? sl : 0f));
                outR[i] = SoftLimit(mic + (_useSystem ? sr : 0f));
            }
            for (int k = 0; k < 3; k++) _gains[k] = target[k];
        }

        /// <summary>Простое сведение: сумма микрофонов (приглушённая) и звук компьютера, без задержки.</summary>
        public static void SimpleMix(AudioBlock block, bool mixMic1, bool mixMic2, bool mixMic3, bool mixSystem, float[] outL, float[] outR)
        {
            // Несколько микрофонов слышат одного человека: приглушаем сумму, чтобы общая дорожка не хрипела.
            int micsInMix = (mixMic1 ? 1 : 0) + (mixMic2 ? 1 : 0) + (mixMic3 ? 1 : 0);
            float micGain = micsInMix > 1 ? (float)(1.0 / Math.Sqrt(micsInMix)) : 1f;
            for (int i = 0; i < block.Frames; i++)
            {
                float mic = ((mixMic1 ? block.Mic1[i] : 0f) + (mixMic2 ? block.Mic2[i] : 0f) + (mixMic3 ? block.Mic3[i] : 0f)) * micGain;
                outL[i] = SoftLimit(mic + (mixSystem ? block.Sys[i * 2] : 0f));
                outR[i] = SoftLimit(mic + (mixSystem ? block.Sys[i * 2 + 1] : 0f));
            }
        }

        public static float SoftLimit(float x)
        {
            const float knee = 0.85f;
            float a = x < 0 ? -x : x;
            if (a <= knee) return x;
            float limited = knee + (1f - knee) * (float)Math.Tanh((a - knee) / (1f - knee));
            return x < 0 ? -limited : limited;
        }
    }

    /// <summary>Спектры звука компьютера (того, что звучит из динамиков) за последние ~0,7 с.</summary>
    internal sealed class FarEndHistory
    {
        public const int Frames = 64;
        private readonly float[][] _mag = new float[Frames][];
        private readonly float[] _energy = new float[Frames];
        private readonly float[] _re = new float[RoomMixer.FrameSize];
        private readonly float[] _im = new float[RoomMixer.FrameSize];
        private int _head = -1;

        /// <summary>Номер последнего кадра (растёт на единицу каждые 512 сэмплов).</summary>
        public long Count { get; private set; }

        public FarEndHistory()
        {
            for (int i = 0; i < Frames; i++) _mag[i] = new float[RoomMixer.Bins];
        }

        public void Push(Fft fft, float[] window, float[] frame)
        {
            for (int i = 0; i < RoomMixer.FrameSize; i++)
            {
                _re[i] = frame[i] * window[i];
                _im[i] = 0;
            }
            fft.Forward(_re, _im);
            _head = (_head + 1) % Frames;
            float[] m = _mag[_head];
            double e = 0;
            for (int f = 0; f < RoomMixer.Bins; f++)
            {
                m[f] = (float)Math.Sqrt(_re[f] * _re[f] + _im[f] * _im[f]);
                if (f >= EchoSuppressor.BandLow && f <= EchoSuppressor.BandHigh) e += m[f] * m[f];
            }
            _energy[_head] = (float)e;
            Count++;
        }

        /// <summary>Спектр кадра, бывшего <paramref name="ago"/> кадров назад (0 — текущий).</summary>
        public float[] Magnitude(int ago)
        {
            return _mag[((_head - ago) % Frames + Frames) % Frames];
        }

        public float Energy(int ago)
        {
            return _energy[((_head - ago) % Frames + Frames) % Frames];
        }
    }

    /// <summary>
    /// Подавление эха в одном микрофоне. Для каждой частоты оценивает, сколько
    /// звука из динамиков слышит микрофон (с учётом задержки телевизора и
    /// отзвука комнаты), и приглушает эту часть. Голос человека в комнате,
    /// пока собеседники молчат, проходит без изменений.
    /// </summary>
    internal sealed class EchoSuppressor
    {
        /// <summary>Полоса для поиска задержки: примерно 200 Гц – 8 кГц.</summary>
        public const int BandLow = 4;
        public const int BandHigh = 170;

        /// <summary>Сколько кадров после прямого звука ещё слышен отзвук комнаты (~170 мс).</summary>
        private const int Tail = 16;
        /// <summary>Пока задержка не найдена, учитываем всё окно до ~600 мс.</summary>
        private const int SearchFrames = 56;
        private const float TailDecay = 0.8f;
        private const float Overestimate = 1.6f;
        private const float Floor = 0.1f;
        private const float ActiveRef = 0.02f;

        private readonly Fft _fft;
        private readonly float[] _window;
        private readonly FarEndHistory _far;
        private readonly float[] _re = new float[RoomMixer.FrameSize];
        private readonly float[] _im = new float[RoomMixer.FrameSize];
        private readonly float[] _overlap = new float[RoomMixer.Hop];
        private readonly float[] _coupling = new float[RoomMixer.Bins];
        private readonly float[] _gain = new float[RoomMixer.Bins];
        private readonly float[] _echo = new float[RoomMixer.Bins];
        private readonly SampleQueue _out = new SampleQueue(RoomMixer.Hop);
        private readonly DelayFinder _delay = new DelayFinder();

        /// <summary>Входной кадр: первая половина — прошлые сэмплы, вторая заполняется снаружи.</summary>
        public readonly float[] Input = new float[RoomMixer.FrameSize];

        private double _inEnergy, _outEnergy;

        public EchoSuppressor(Fft fft, float[] window, FarEndHistory far)
        {
            _fft = fft;
            _window = window;
            _far = far;
            for (int f = 0; f < RoomMixer.Bins; f++)
            {
                _coupling[f] = 0.1f;
                _gain[f] = 1f;
            }
        }

        public int DelayFrames
        {
            get { return _delay.Delay; }
        }

        /// <summary>Подавление эха за последние секунды, дБ.</summary>
        public double ReductionDb
        {
            get { return _inEnergy > 1e-9 ? 10 * Math.Log10(_inEnergy / Math.Max(_outEnergy, 1e-12)) : 0; }
        }

        public void ProcessFrame()
        {
            for (int i = 0; i < RoomMixer.FrameSize; i++)
            {
                _re[i] = Input[i] * _window[i];
                _im[i] = 0;
            }
            _fft.Forward(_re, _im);

            double micEnergy = 0;
            for (int f = BandLow; f <= BandHigh; f++) micEnergy += _re[f] * _re[f] + _im[f] * _im[f];
            _delay.Add(_far.Energy(0), (float)micEnergy);

            // Где в прошлом искать звук из динамиков, который сейчас слышит микрофон.
            int d = _delay.Delay;
            int from = d < 0 ? 0 : Math.Max(0, d - 1);
            int to = d < 0 ? SearchFrames : Math.Min(FarEndHistory.Frames - 1, d + Tail);
            Array.Clear(_echo, 0, _echo.Length);
            float w = 1f;
            for (int j = from; j <= to; j++)
            {
                float[] m = _far.Magnitude(j);
                for (int f = 0; f < RoomMixer.Bins; f++)
                {
                    float v = m[f] * w;
                    if (v > _echo[f]) _echo[f] = v;
                }
                if (d >= 0 && j >= d) w *= TailDecay;
            }

            double frameIn = 0, frameOut = 0;
            for (int f = 0; f < RoomMixer.Bins; f++)
            {
                float y = (float)Math.Sqrt(_re[f] * _re[f] + _im[f] * _im[f]);
                float s = _echo[f];
                if (s > ActiveRef)
                {
                    // Сколько звука из динамиков доходит до микрофона на этой частоте. Следим за
                    // нижней частью отношений (60 %): голос в комнате делает отношение больше, а эхо нет.
                    float r = y / s;
                    if (r > _coupling[f]) _coupling[f] = Math.Min(_coupling[f] * 1.03f, 4f);
                    else _coupling[f] = Math.Max(_coupling[f] * 0.98f, 1e-3f);
                }
                float e = Overestimate * _coupling[f] * s;
                float g;
                if (y < 1e-9f) g = e > 0 ? Floor : 1f;
                else
                {
                    float q = e / y;
                    g = 1f - q * q;
                    if (g < Floor) g = Floor;
                }
                // Сглаживание во времени убирает «булькающие» призвуки.
                g = 0.6f * g + 0.4f * _gain[f];
                _gain[f] = g;
                if (f >= BandLow && f <= BandHigh && s > ActiveRef)
                {
                    frameIn += y * y;
                    frameOut += y * y * g * g;
                }
            }

            for (int f = 0; f < RoomMixer.Bins; f++)
            {
                _re[f] *= _gain[f];
                _im[f] *= _gain[f];
            }
            // Вещественный сигнал: вторая половина спектра зеркальна первой.
            for (int f = 1; f < RoomMixer.FrameSize / 2; f++)
            {
                _re[RoomMixer.FrameSize - f] = _re[f];
                _im[RoomMixer.FrameSize - f] = -_im[f];
            }
            _fft.Inverse(_re, _im);

            var output = new float[RoomMixer.Hop];
            for (int i = 0; i < RoomMixer.Hop; i++)
            {
                output[i] = _overlap[i] + _re[i] * _window[i];
                _overlap[i] = _re[RoomMixer.Hop + i] * _window[RoomMixer.Hop + i];
            }
            _out.Push(output);
            Array.Copy(Input, RoomMixer.Hop, Input, 0, RoomMixer.Hop);

            _inEnergy = 0.995 * _inEnergy + frameIn;
            _outEnergy = 0.995 * _outEnergy + frameOut;
        }

        public float[] Take(int count)
        {
            return _out.Take(count);
        }
    }

    /// <summary>
    /// Находит, на сколько кадров звук из динамиков приходит в микрофон позже,
    /// чем уходит в них: сравнивает, когда звук становится громче, в обоих потоках.
    /// </summary>
    internal sealed class DelayFinder
    {
        public const int History = 600;
        public const int MaxLag = 56;
        private const int Every = 47;

        private readonly float[] _far = new float[History];
        private readonly float[] _mic = new float[History];
        private int _pos, _count, _sinceCheck;
        private int _candidate = -1;
        private float _lastFar, _lastMic;

        /// <summary>Найденная задержка в кадрах или −1.</summary>
        public int Delay { get; private set; }

        public DelayFinder()
        {
            Delay = -1;
        }

        public void Add(float farEnergy, float micEnergy)
        {
            // Нарастание громкости (в логарифме): так совпадения звука видны чётче, чем по самой громкости.
            float lf = (float)Math.Log10(farEnergy + 1e-3);
            float lm = (float)Math.Log10(micEnergy + 1e-3);
            _far[_pos] = farEnergy > 1e-2 ? Math.Max(0, Math.Min(3, lf - _lastFar)) : 0;
            _mic[_pos] = Math.Max(0, Math.Min(3, lm - _lastMic));
            _lastFar = lf;
            _lastMic = lm;
            _pos = (_pos + 1) % History;
            if (_count < History) _count++;
            if (++_sinceCheck >= Every && _count >= 200)
            {
                _sinceCheck = 0;
                Estimate();
            }
        }

        private void Estimate()
        {
            int n = _count;
            int start = (_pos - n + History) % History;
            Func<float[], int, float> at = (a, i) => a[(start + i) % History];

            int farActive = 0;
            for (int i = 0; i < n; i++) if (at(_far, i) > 0.05f) farActive++;
            if (farActive < n / 10) return;

            double best = -1, second = -1;
            int bestLag = -1;
            var scores = new double[MaxLag + 1];
            for (int lag = 0; lag <= MaxLag; lag++)
            {
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                int m = n - lag;
                for (int i = 0; i < m; i++)
                {
                    double x = at(_far, i);
                    double y = at(_mic, i + lag);
                    sx += x;
                    sy += y;
                    sxx += x * x;
                    syy += y * y;
                    sxy += x * y;
                }
                double vx = sxx - sx * sx / m, vy = syy - sy * sy / m;
                double c = vx > 1e-9 && vy > 1e-9 ? (sxy - sx * sy / m) / Math.Sqrt(vx * vy) : 0;
                scores[lag] = c;
                if (c > best)
                {
                    best = c;
                    bestLag = lag;
                }
            }
            for (int lag = 0; lag <= MaxLag; lag++)
                if (Math.Abs(lag - bestLag) > 2 && scores[lag] > second) second = scores[lag];

            // Уверенный и заметный пик; принимаем, если он повторился два раза подряд.
            if (best < 0.2 || best - second < 0.08) return;
            if (_candidate >= 0 && Math.Abs(_candidate - bestLag) <= 1) Delay = bestLag;
            _candidate = bestLag;
        }
    }

    /// <summary>Очередь сэмплов: кладём кадрами, забираем блоками.</summary>
    internal sealed class SampleQueue
    {
        private float[] _data = new float[4096];
        private int _start, _count;

        /// <param name="prefill">Сколько нулей лежит в очереди сначала (задержка).</param>
        public SampleQueue(int prefill)
        {
            _count = prefill;
        }

        public void Push(float[] samples)
        {
            if (_count + samples.Length > _data.Length)
            {
                var bigger = new float[Math.Max(_data.Length * 2, _count + samples.Length)];
                for (int i = 0; i < _count; i++) bigger[i] = _data[(_start + i) % _data.Length];
                _data = bigger;
                _start = 0;
            }
            for (int i = 0; i < samples.Length; i++) _data[(_start + _count + i) % _data.Length] = samples[i];
            _count += samples.Length;
        }

        public float[] Take(int n)
        {
            var result = new float[n];
            int have = Math.Min(n, _count);
            for (int i = 0; i < have; i++) result[i] = _data[(_start + i) % _data.Length];
            _start = (_start + have) % _data.Length;
            _count -= have;
            return result;
        }

        /// <summary>Линия задержки: кладём сэмпл, достаём тот, что лежит дольше всех.</summary>
        public float Shift(float sample)
        {
            _data[(_start + _count) % _data.Length] = sample;
            float result = _data[_start];
            _start = (_start + 1) % _data.Length;
            return result;
        }
    }

    /// <summary>Быстрое преобразование Фурье (радикс 2) для кадров по 1024 сэмпла.</summary>
    internal sealed class Fft
    {
        private readonly int _n;
        private readonly int[] _rev;
        private readonly float[] _cos;
        private readonly float[] _sin;

        public Fft(int n)
        {
            _n = n;
            int bits = 0;
            while ((1 << bits) < n) bits++;
            _rev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _rev[i] = r;
            }
            _cos = new float[n / 2];
            _sin = new float[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n);
                _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n);
            }
        }

        /// <summary>Окно «корень из Ханна»: при перекрытии в половину кадра анализ и синтез вместе дают единицу.</summary>
        public static float[] SqrtHann(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            return w;
        }

        public void Forward(float[] re, float[] im)
        {
            Transform(re, im, false);
        }

        /// <summary>Обратное преобразование, уже поделённое на размер.</summary>
        public void Inverse(float[] re, float[] im)
        {
            Transform(re, im, true);
            float k = 1f / _n;
            for (int i = 0; i < _n; i++)
            {
                re[i] *= k;
                im[i] *= k;
            }
        }

        private void Transform(float[] re, float[] im, bool inverse)
        {
            for (int i = 0; i < _n; i++)
            {
                int j = _rev[i];
                if (j > i)
                {
                    float t = re[i];
                    re[i] = re[j];
                    re[j] = t;
                    t = im[i];
                    im[i] = im[j];
                    im[j] = t;
                }
            }
            for (int size = 2; size <= _n; size <<= 1)
            {
                int half = size >> 1, step = _n / size;
                for (int start = 0; start < _n; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float c = _cos[k * step];
                        float s = inverse ? -_sin[k * step] : _sin[k * step];
                        int a = start + k, b = a + half;
                        float tr = re[b] * c - im[b] * s;
                        float ti = re[b] * s + im[b] * c;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
