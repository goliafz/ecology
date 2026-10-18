using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MasterVks.Core;
using MasterVks.Recording;

namespace MasterVks.Checks
{
    /// <summary>Одно снятие показаний здоровья компьютера.</summary>
    internal sealed class HealthSample
    {
        public bool? OnAc;
        public int BatteryPercent = -1;
        public bool BatterySaver;
        public double CpuPercent;
        /// <summary>Частота процессора относительно номинальной, % (меньше 100 — процессор притормозил).</summary>
        public double? PerformancePercent;
        public double AvailableMb = -1;
        public double? TemperatureC;
        public bool RebootPending;
    }

    /// <summary>
    /// Решает, о чём предупредить, по потоку показаний. Предупреждает один раз
    /// при появлении проблемы и один раз, когда она ушла: без спама.
    /// Логика отдельно от Windows, поэтому проверяется автотестами.
    /// </summary>
    internal sealed class HealthWatch
    {
        private const double SustainSeconds = 60;

        private bool? _lastAc;
        private bool _lowBatteryWarned, _saverWarned, _cpuWarned, _throttleWarned, _hotWarned, _memoryWarned, _rebootWarned;
        private double _cpuHighSince = -1, _cpuLowSince = -1, _throttleSince = -1, _unthrottleSince = -1;

        public bool OnBattery { get; private set; }

        /// <summary>Есть ли сейчас проблема, из-за которой стоит показать жёлтый значок.</summary>
        public bool HasProblem
        {
            get { return OnBattery || _saverWarned || _cpuWarned || _throttleWarned || _hotWarned || _memoryWarned; }
        }

        public List<Notice> Feed(HealthSample s, double nowSeconds)
        {
            var notices = new List<Notice>();

            // Питание
            if (s.OnAc.HasValue)
            {
                OnBattery = s.OnAc == false;
                if (s.OnAc == false && _lastAc != false)
                    notices.Add(new Notice("Ноутбук работает от батареи" + (s.BatteryPercent >= 0 ? " (" + s.BatteryPercent + " %)" : "") +
                                           ". Проверьте, не отошла ли зарядка: без неё ноутбук может выключиться посреди ВКС.", NoticeLevel.Warning));
                if (s.OnAc == true && _lastAc == false)
                {
                    notices.Add(new Notice("Зарядка снова подключена.", NoticeLevel.Success));
                    _lowBatteryWarned = false;
                }
                _lastAc = s.OnAc;
            }
            if (s.OnAc == false && s.BatteryPercent >= 0 && s.BatteryPercent < 30 && !_lowBatteryWarned)
            {
                _lowBatteryWarned = true;
                notices.Add(new Notice("Заряд батареи " + s.BatteryPercent + " %. Срочно подключите зарядку, иначе ноутбук выключится. Запись при этом сохранится.", NoticeLevel.Error));
            }
            if (s.BatterySaver && !_saverWarned)
            {
                _saverWarned = true;
                notices.Add(new Notice("Windows включила экономию заряда: процессор работает медленнее. Выключите её в центре уведомлений.", NoticeLevel.Warning));
            }
            else if (!s.BatterySaver)
            {
                _saverWarned = false;
            }

            // Процессор
            if (s.CpuPercent >= 90)
            {
                _cpuLowSince = -1;
                if (_cpuHighSince < 0) _cpuHighSince = nowSeconds;
                if (!_cpuWarned && nowSeconds - _cpuHighSince >= SustainSeconds)
                {
                    _cpuWarned = true;
                    notices.Add(new Notice("Процессор уже минуту загружен почти полностью (" + (int)s.CpuPercent + " %). Закройте лишние программы, иначе ВКС и запись могут начать тормозить.", NoticeLevel.Warning));
                }
            }
            else
            {
                _cpuHighSince = -1;
                if (s.CpuPercent < 70)
                {
                    if (_cpuLowSince < 0) _cpuLowSince = nowSeconds;
                    if (_cpuWarned && nowSeconds - _cpuLowSince >= SustainSeconds)
                    {
                        _cpuWarned = false;
                        notices.Add(new Notice("Нагрузка на процессор вернулась в норму.", NoticeLevel.Success));
                    }
                }
            }

            // Сброс частоты (перегрев или экономия энергии)
            if (s.PerformancePercent.HasValue)
            {
                bool throttled = s.PerformancePercent.Value < 55 && s.CpuPercent > 50;
                if (throttled)
                {
                    _unthrottleSince = -1;
                    if (_throttleSince < 0) _throttleSince = nowSeconds;
                    if (!_throttleWarned && nowSeconds - _throttleSince >= SustainSeconds)
                    {
                        _throttleWarned = true;
                        notices.Add(new Notice("Процессор под нагрузкой сбросил частоту до " + (int)s.PerformancePercent.Value +
                                               " %. Похоже на перегрев: проверьте, не закрыты ли вентиляционные отверстия, а ноутбук поставьте на твёрдую поверхность.", NoticeLevel.Warning));
                    }
                }
                else
                {
                    _throttleSince = -1;
                    if (s.PerformancePercent.Value > 75 || s.CpuPercent < 30)
                    {
                        if (_unthrottleSince < 0) _unthrottleSince = nowSeconds;
                        if (_throttleWarned && nowSeconds - _unthrottleSince >= SustainSeconds)
                        {
                            _throttleWarned = false;
                            notices.Add(new Notice("Частота процессора восстановилась.", NoticeLevel.Success));
                        }
                    }
                }
            }

            // Температура (если датчик есть)
            if (s.TemperatureC.HasValue)
            {
                if (s.TemperatureC.Value >= 95 && !_hotWarned)
                {
                    _hotWarned = true;
                    notices.Add(new Notice("Датчик температуры показывает " + (int)s.TemperatureC.Value + " °C. Проверьте вентиляцию компьютера.", NoticeLevel.Warning));
                }
                else if (s.TemperatureC.Value < 85)
                {
                    _hotWarned = false;
                }
            }

            // Память
            if (s.AvailableMb >= 0)
            {
                if (s.AvailableMb < 700 && !_memoryWarned)
                {
                    _memoryWarned = true;
                    notices.Add(new Notice("Свободной памяти осталось " + HealthRules.FormatMb(s.AvailableMb) + ". Закройте лишние программы и вкладки браузера.", NoticeLevel.Warning));
                }
                else if (s.AvailableMb > 1200)
                {
                    _memoryWarned = false;
                }
            }

            // Обновления
            if (s.RebootPending && !_rebootWarned)
            {
                _rebootWarned = true;
                notices.Add(new Notice("Windows установила обновления и ждёт перезагрузки. Перезагрузите компьютер после ВКС, а не во время.", NoticeLevel.Warning));
            }
            return notices;
        }
    }

    /// <summary>Фоновый наблюдатель: раз в 5 секунд снимает показания и копит предупреждения для окна.</summary>
    internal sealed class HealthMonitor : IDisposable
    {
        private readonly HealthWatch _watch = new HealthWatch();
        private readonly ConcurrentQueue<Notice> _notices = new ConcurrentQueue<Notice>();
        private readonly CpuSampler _cpu = new CpuSampler();
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _hasProblem;
        private PerformanceCounter _performance;
        private bool _performanceFailed;

        public bool HasProblem
        {
            get { return _hasProblem; }
        }

        public bool TryTakeNotice(out Notice notice)
        {
            return _notices.TryDequeue(out notice);
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Здоровье компьютера", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            var watch = Stopwatch.StartNew();
            int tick = 0;
            bool rebootPending = false;
            _cpu.Sample();
            while (!_stop)
            {
                for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);
                if (_stop) break;
                try
                {
                    HealthProbe.PowerState power = HealthProbe.ReadPower();
                    double available, total;
                    HealthProbe.Memory(out available, out total);
                    if (tick % 12 == 0) rebootPending = HealthProbe.IsRebootPending();
                    var sample = new HealthSample
                    {
                        OnAc = power.OnAc,
                        BatteryPercent = power.BatteryPercent,
                        BatterySaver = power.BatterySaver,
                        CpuPercent = _cpu.Sample(),
                        PerformancePercent = ReadPerformance(),
                        AvailableMb = available,
                        TemperatureC = tick % 6 == 0 ? HealthProbe.Temperature() : null,
                        RebootPending = rebootPending
                    };
                    foreach (Notice n in _watch.Feed(sample, watch.Elapsed.TotalSeconds))
                    {
                        if (n.Level >= NoticeLevel.Warning) Log.Warn("Здоровье: " + n.Text);
                        else Log.Info("Здоровье: " + n.Text);
                        _notices.Enqueue(n);
                    }
                    _hasProblem = _watch.HasProblem;
                }
                catch (Exception ex)
                {
                    Log.Warn("Сбой наблюдения за здоровьем компьютера", ex);
                }
                tick++;
            }
        }

        private double? ReadPerformance()
        {
            if (_performanceFailed) return null;
            try
            {
                if (_performance == null)
                {
                    _performance = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    _performance.NextValue();
                    return null;
                }
                return _performance.NextValue();
            }
            catch (Exception)
            {
                _performanceFailed = true;
                return null;
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread != null) _thread.Join(2000);
            if (_performance != null) _performance.Dispose();
        }
    }
}
