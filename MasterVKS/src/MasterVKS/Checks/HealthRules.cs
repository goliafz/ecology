using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MasterVks.Core;

namespace MasterVks.Checks
{
    /// <summary>Вердикт по одному пункту здоровья компьютера.</summary>
    internal sealed class HealthVerdict
    {
        public CheckStatus Status;
        public string Summary;
        public string Details;
        /// <summary>Можно исправить кнопкой (например, защитить от перезагрузки).</summary>
        public bool CanFix;

        public HealthVerdict(CheckStatus status, string summary, string details, bool canFix = false)
        {
            Status = status;
            Summary = summary;
            Details = details ?? "";
            CanFix = canFix;
        }
    }

    internal sealed class DiskInfo
    {
        public string Name;
        /// <summary>«Healthy», «Warning», «Unhealthy» или пусто, если узнать не удалось.</summary>
        public string Health;
        public string Media;
    }

    /// <summary>
    /// Правила оценки здоровья компьютера. Здесь только решения по уже снятым
    /// показаниям, без обращений к Windows, поэтому правила проверяются автотестами.
    /// </summary>
    internal static class HealthRules
    {
        public static HealthVerdict Power(bool? onAc, int batteryPercent, bool hasBattery, bool batterySaver, bool powerSaverPlan)
        {
            string battery = hasBattery && batteryPercent >= 0 && batteryPercent <= 100 ? "батарея " + batteryPercent + " %" : null;
            if (onAc == false)
            {
                bool low = batteryPercent >= 0 && batteryPercent < 30;
                return new HealthVerdict(low ? CheckStatus.Error : CheckStatus.Warning,
                    "Работает от батареи" + (battery != null ? ", " + battery : "") + ": подключите зарядку",
                    "Без зарядки ноутбук может выключиться посреди ВКС, а Windows при низком заряде снижает скорость процессора.");
            }
            if (batterySaver)
                return new HealthVerdict(CheckStatus.Warning, "Включён режим экономии заряда",
                    "Windows замедляет процессор и фоновые программы. Выключите экономию в центре уведомлений или в «Параметры → Система → Питание».");
            if (powerSaverPlan)
                return new HealthVerdict(CheckStatus.Warning, "Выбрана схема питания «Экономия энергии»",
                    "Процессор работает медленнее, запись может не успевать. Выберите «Сбалансированная» или «Высокая производительность» в «Панель управления → Электропитание».");
            if (onAc == true)
                return new HealthVerdict(CheckStatus.Ok, "От сети" + (battery != null ? ", " + battery : ""),
                    "Если зарядка отойдёт во время записи, программа сразу предупредит.");
            return new HealthVerdict(CheckStatus.Ok, "Состояние питания Windows не сообщает", "Похоже, это настольный компьютер без батареи.");
        }

        /// <summary>Попадает ли час в «часы активности» Windows (когда она не перезагружается сама).</summary>
        public static bool InActiveHours(int start, int end, int hour)
        {
            if (start < 0 || end < 0 || start == end) return false;
            if (start < end) return hour >= start && hour < end;
            return hour >= start || hour < end;
        }

        public static HealthVerdict Updates(bool rebootPending, bool noAutoRebootPolicy, int activeStart, int activeEnd, int hourNow, TimeSpan uptime)
        {
            string hours = activeStart >= 0 && activeEnd >= 0 && activeStart != activeEnd
                ? "Часы активности Windows: с " + activeStart + ":00 до " + activeEnd + ":00."
                : "Часы активности Windows не заданы.";
            string uptimeText = uptime.TotalDays >= 1
                ? " Компьютер работает без перезагрузки " + (int)uptime.TotalDays + " " + Fmt.Plural((long)uptime.TotalDays, "день", "дня", "дней") + "."
                : "";

            if (rebootPending)
                return new HealthVerdict(CheckStatus.Warning, "Windows ждёт перезагрузки после обновлений",
                    "Перезагрузите компьютер заранее, до начала ВКС. Иначе Windows может перезагрузить его сама в неподходящий момент. " + hours + uptimeText,
                    !noAutoRebootPolicy);

            if (noAutoRebootPolicy)
                return new HealthVerdict(CheckStatus.Ok, "Защищён от самопроизвольной перезагрузки",
                    "Windows не будет перезагружаться сама, пока вы вошли в систему. Обновления ставятся, перезагрузку вы делаете сами. " + hours + uptimeText);

            if (InActiveHours(activeStart, activeEnd, hourNow))
                return new HealthVerdict(CheckStatus.Ok, "Сейчас часы активности: Windows сама не перезагрузится",
                    hours + " Вне этих часов Windows может перезагрузиться для обновлений. Нажмите, чтобы защитить компьютер полностью (один раз понадобится пароль администратора)." + uptimeText,
                    true);

            return new HealthVerdict(CheckStatus.Warning, "Windows может перезагрузиться для обновлений",
                hours + " Сейчас " + hourNow + " ч, это вне часов активности. Нажмите, чтобы защитить компьютер от самопроизвольной перезагрузки (один раз понадобится пароль администратора)." + uptimeText,
                true);
        }

        public static HealthVerdict Processor(double loadPercent, double availableMb, double totalMb, double? temperatureC)
        {
            var problems = new List<string>();
            CheckStatus status = CheckStatus.Ok;
            if (loadPercent > 85)
            {
                problems.Add("процессор уже загружен на " + (int)loadPercent + " %, а запись добавит нагрузки: закройте лишние программы");
                status = CheckStatus.Warning;
            }
            if (availableMb >= 0 && availableMb < 1024)
            {
                problems.Add("свободно мало памяти (" + FormatMb(availableMb) + "): закройте лишние программы и вкладки браузера");
                status = CheckStatus.Warning;
            }
            if (temperatureC.HasValue && temperatureC.Value >= 95)
            {
                problems.Add("датчик показывает " + (int)temperatureC.Value + " °C: проверьте, не закрыты ли вентиляционные отверстия");
                status = CheckStatus.Warning;
            }

            string facts = "Нагрузка " + (int)Math.Round(loadPercent) + " %" +
                           (availableMb >= 0 ? " · свободно " + FormatMb(availableMb) + (totalMb > 0 ? " из " + FormatMb(totalMb) : "") : "") +
                           (temperatureC.HasValue ? " · датчик " + (int)temperatureC.Value + " °C" : "");
            if (status == CheckStatus.Ok)
                return new HealthVerdict(CheckStatus.Ok, facts,
                    "Во время записи программа следит за нагрузкой, памятью и тем, не сбросил ли процессор частоту из-за перегрева, и предупредит, если что-то пойдёт не так.");
            return new HealthVerdict(status, problems.Count == 1 ? Capitalize(problems[0].Split(':')[0]) : "Есть замечания",
                Capitalize(string.Join("; ", problems)) + ". " + facts + ".");
        }

        public static HealthVerdict Disks(IList<DiskInfo> disks, double writeMbPerSecond)
        {
            var bad = disks.Where(d => string.Equals(d.Health, "Unhealthy", StringComparison.OrdinalIgnoreCase)).ToList();
            var warn = disks.Where(d => string.Equals(d.Health, "Warning", StringComparison.OrdinalIgnoreCase)).ToList();
            string list = string.Join("; ", disks.Select(d => d.Name + (string.IsNullOrEmpty(d.Media) ? "" : " (" + d.Media + ")") + ": " + HealthWord(d.Health)));
            string speed = writeMbPerSecond > 0 ? "Скорость записи в папку " + ((int)writeMbPerSecond) + " МБ/с." : "";

            if (bad.Count > 0)
                return new HealthVerdict(CheckStatus.Error, "Диск неисправен: " + bad[0].Name,
                    "Windows сообщает, что диск может скоро отказать. Скопируйте важные записи на другой носитель и покажите компьютер специалисту. " + list + ". " + speed);
            if (warn.Count > 0)
                return new HealthVerdict(CheckStatus.Warning, "Диск предупреждает о проблемах: " + warn[0].Name,
                    "Windows видит признаки износа диска. Записи пока пишутся, но стоит регулярно копировать их в другое место. " + list + ". " + speed);
            if (writeMbPerSecond > 0 && writeMbPerSecond < 20)
                return new HealthVerdict(CheckStatus.Warning, "Диск пишет медленно: " + (int)writeMbPerSecond + " МБ/с",
                    "Для записи хватит, но такая скорость бывает у изношенного или очень загруженного диска. " + list);
            if (disks.Count == 0)
                return new HealthVerdict(CheckStatus.Ok, writeMbPerSecond > 0 ? "Запись в папку работает, " + (int)writeMbPerSecond + " МБ/с" : "Запись в папку работает",
                    "Состояние дисков Windows не сообщила. " + speed);
            return new HealthVerdict(CheckStatus.Ok, "Диски исправны" + (writeMbPerSecond > 0 ? ", запись " + (int)writeMbPerSecond + " МБ/с" : ""), list + ". " + speed);
        }

        public static HealthVerdict Stability(int unexpectedShutdowns, int blueScreens, int hardwareErrors, DateTime? lastProblem, bool readable)
        {
            if (!readable)
                return new HealthVerdict(CheckStatus.Ok, "Журнал Windows прочитать не удалось", "Это не мешает записи.");
            if (unexpectedShutdowns == 0 && blueScreens == 0 && hardwareErrors == 0)
                return new HealthVerdict(CheckStatus.Ok, "За 30 дней сбоев не было",
                    "В журнале Windows нет аварийных выключений, синих экранов и аппаратных ошибок.");

            var parts = new List<string>();
            if (blueScreens > 0) parts.Add(blueScreens + " " + Fmt.Plural(blueScreens, "синий экран", "синих экрана", "синих экранов"));
            if (unexpectedShutdowns > 0) parts.Add(unexpectedShutdowns + " " + Fmt.Plural(unexpectedShutdowns, "аварийное выключение", "аварийных выключения", "аварийных выключений"));
            if (hardwareErrors > 0) parts.Add(hardwareErrors + " " + Fmt.Plural(hardwareErrors, "аппаратная ошибка", "аппаратные ошибки", "аппаратных ошибок"));
            string when = lastProblem.HasValue ? " Последний раз " + lastProblem.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + "." : "";

            bool serious = blueScreens > 0 || hardwareErrors > 0 || unexpectedShutdowns >= 2;
            string advice = serious
                ? " Компьютер уже сбоил. Запись переживёт такой сбой (MKV и восстановление), но для важных ВКС держите включённым диктофон на телефоне и покажите компьютер специалисту."
                : " Одно выключение бывает, если компьютер выключили кнопкой. Если вы этого не делали, держите включённым диктофон на телефоне.";
            return new HealthVerdict(serious ? CheckStatus.Warning : CheckStatus.Ok, "За 30 дней: " + string.Join(", ", parts), "По журналу Windows." + when + advice);
        }

        private static string HealthWord(string health)
        {
            if (string.Equals(health, "Healthy", StringComparison.OrdinalIgnoreCase)) return "исправен";
            if (string.Equals(health, "Warning", StringComparison.OrdinalIgnoreCase)) return "есть предупреждения";
            if (string.Equals(health, "Unhealthy", StringComparison.OrdinalIgnoreCase)) return "неисправен";
            return "состояние неизвестно";
        }

        public static string FormatMb(double mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " ГБ" : ((int)mb) + " МБ";
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.GetCultureInfo("ru-RU")) + s.Substring(1);
        }
    }
}
