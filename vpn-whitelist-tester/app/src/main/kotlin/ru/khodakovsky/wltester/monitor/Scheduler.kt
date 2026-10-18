package ru.khodakovsky.wltester.monitor

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import ru.khodakovsky.wltester.data.AppSettings
import ru.khodakovsky.wltester.data.MonitorState
import ru.khodakovsky.wltester.data.Phase
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.ui.MainActivity
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId

/**
 * Будильники AlarmManager — единственный надёжный способ просыпаться ночью в Doze.
 * Каждый будильник запускает [MonitorService] на один цикл.
 */
object Scheduler {

    private const val MIN_DELAY_MS = 5_000L

    fun inWindow(s: AppSettings, now: Long = System.currentTimeMillis()): Boolean {
        if (s.windowStart == s.windowEnd) return true
        val t = LocalDateTime.ofInstant(Instant.ofEpochMilli(now), ZoneId.systemDefault())
        val m = t.hour * 60 + t.minute
        return if (s.windowStart < s.windowEnd) m >= s.windowStart && m < s.windowEnd
        else m >= s.windowStart || m < s.windowEnd
    }

    /** Мониторинг сейчас должен работать (служба на переднем плане). */
    fun isActiveNow(s: AppSettings, now: Long = System.currentTimeMillis()): Boolean =
        s.monitoringEnabled && (s.scheduleMode == ScheduleMode.ALWAYS || inWindow(s, now))

    fun nextWindowStart(s: AppSettings, now: Long): Long {
        val zone = ZoneId.systemDefault()
        val t = LocalDateTime.ofInstant(Instant.ofEpochMilli(now), zone)
        var c = t.toLocalDate().atTime(s.windowStart / 60, s.windowStart % 60)
        if (!c.isAfter(t)) c = c.plusDays(1)
        return c.atZone(zone).toInstant().toEpochMilli()
    }

    fun intervalMs(s: AppSettings, st: MonitorState): Long = when (s.scheduleMode) {
        ScheduleMode.WINDOW -> s.fullIntervalMin * 60_000L
        ScheduleMode.ALWAYS -> when {
            st.phase == Phase.ALERT -> s.alertIntervalMin.coerceIn(1, 720) * 60_000L
            st.failStreak > 0 -> 60_000L // перепроверить через минуту, прежде чем бить тревогу
            inWindow(s) -> s.nightWatchMin.coerceIn(1, 720) * 60_000L // ночью белые списки вероятнее, смотрим чаще
            else -> s.watchIntervalMin * 60_000L
        }
    }

    fun computeNext(s: AppSettings, st: MonitorState, now: Long = System.currentTimeMillis()): Long? {
        if (!s.monitoringEnabled) return null
        val candidate = maxOf(st.lastRunAt + intervalMs(s, st), now + MIN_DELAY_MS)
        return when (s.scheduleMode) {
            ScheduleMode.ALWAYS -> candidate
            ScheduleMode.WINDOW -> when {
                !inWindow(s, now) -> nextWindowStart(s, now)
                inWindow(s, candidate) -> candidate
                else -> nextWindowStart(s, candidate)
            }
        }
    }

    /** Пересчитать и поставить следующий будильник. Возвращает время или null. */
    fun schedule(ctx: Context): Long? {
        val store = SettingsStore.get(ctx)
        val s = store.get()
        val am = ctx.getSystemService(AlarmManager::class.java)
        val pi = alarmIntent(ctx)
        val next = computeNext(s, store.state())
        if (next == null) {
            am.cancel(pi)
            store.updateState { it.copy(nextRunAt = 0) }
            return null
        }
        val canExact = Build.VERSION.SDK_INT < Build.VERSION_CODES.S || am.canScheduleExactAlarms()
        try {
            when {
                canExact && s.reliableAlarms -> {
                    val show = PendingIntent.getActivity(
                        ctx, 1, Intent(ctx, MainActivity::class.java),
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                    )
                    am.setAlarmClock(AlarmManager.AlarmClockInfo(next, show), pi)
                }
                canExact -> am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next, pi)
                else -> am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next, pi)
            }
        } catch (e: SecurityException) {
            am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next, pi)
        }
        store.updateState { it.copy(nextRunAt = next) }
        return next
    }

    fun canScheduleExact(ctx: Context): Boolean {
        val am = ctx.getSystemService(AlarmManager::class.java)
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.S || am.canScheduleExactAlarms()
    }

    private fun alarmIntent(ctx: Context): PendingIntent = PendingIntent.getBroadcast(
        ctx, 0, Intent(ctx, AlarmReceiver::class.java).setAction(AlarmReceiver.ACTION),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )
}
