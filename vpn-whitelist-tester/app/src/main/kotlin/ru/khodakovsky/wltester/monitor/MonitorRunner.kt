package ru.khodakovsky.wltester.monitor

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.core.Trigger
import ru.khodakovsky.wltester.core.Verdict
import ru.khodakovsky.wltester.core.analysis.EventDetector
import ru.khodakovsky.wltester.core.engine.CycleEngine
import ru.khodakovsky.wltester.data.AppSettings
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.Phase
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.platform.AndroidPlatform
import ru.khodakovsky.wltester.platform.XrayTunnelFactory
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** Логика одного запуска: какой цикл выполнить, переходы «слежение ⇄ тревога», события и уведомления. */
object MonitorRunner {

    enum class Reason { ALARM, MANUAL }

    private val mutex = Mutex()
    private val _progress = MutableStateFlow<String?>(null)
    /** null — проверка не идёт, иначе текст для UI. */
    val progress: StateFlow<String?> = _progress.asStateFlow()

    private val timeFmt = DateTimeFormatter.ofPattern("dd.MM HH:mm:ss")
    fun fmt(ms: Long): String = timeFmt.format(Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()))
    fun hhmm(min: Int) = "%02d:%02d".format(min / 60, min % 60)

    /** @return false, если другая проверка уже идёт. */
    suspend fun run(ctx: Context, reason: Reason): Boolean {
        if (!mutex.tryLock()) return false
        try {
            _progress.value = "Подготовка…"
            doRun(ctx.applicationContext, reason)
        } finally {
            _progress.value = null
            mutex.unlock()
        }
        return true
    }

    private suspend fun doRun(ctx: Context, reason: Reason) {
        val store = SettingsStore.get(ctx)
        val log = LogStore.get(ctx)
        val s = store.get()

        if (reason == Reason.MANUAL) {
            val c = cycle(ctx, s, CycleKind.FULL, Trigger.MANUAL)
            // Ручная проверка тоже засчитывается: если VPN вернулся, выходим из режима тревоги.
            if (s.scheduleMode == ScheduleMode.ALWAYS && store.state().phase == Phase.ALERT) trackRecovery(ctx, s, c)
            return
        }
        if (!Scheduler.isActiveNow(s)) return

        val st0 = store.state()
        if (s.scheduleMode == ScheduleMode.WINDOW && !st0.windowActive) {
            log.event(EventType.SERVICE, "Начались проверки по расписанию, с ${hhmm(s.windowStart)} до ${hhmm(s.windowEnd)}", "Подробная проверка раз в ${s.fullIntervalMin} мин")
            store.updateState { it.copy(windowActive = true) }
            log.cleanup(s.retentionDays)
        }
        store.updateState { it.copy(lastRunAt = System.currentTimeMillis()) }

        when (s.scheduleMode) {
            ScheduleMode.WINDOW -> cycle(ctx, s, CycleKind.FULL, Trigger.WINDOW)
            ScheduleMode.ALWAYS -> if (st0.phase == Phase.ALERT) {
                trackRecovery(ctx, s, cycle(ctx, s, CycleKind.FULL, Trigger.ALERT))
            } else {
                val c = cycle(ctx, s, CycleKind.QUICK, Trigger.WATCH)
                if (c.kind != CycleKind.SKIPPED) {
                    if (isFailing(c)) {
                        val fs = store.updateState { it.copy(failStreak = it.failStreak + 1) }.failStreak
                        // Пока шла проверка, мониторинг могли выключить — тогда не поднимаем тревогу.
                        if (fs >= s.failThreshold && store.get().monitoringEnabled) {
                            store.updateState { it.copy(phase = Phase.ALERT, failStreak = 0, okStreak = 0) }
                            log.event(
                                EventType.PHASE_CHANGED, "VPN не ответил $fs раза подряд, проверяю чаще",
                                "Подробная проверка раз в ${s.fullIntervalMin} мин, пока VPN не вернётся",
                            )
                            trackRecovery(ctx, s, cycle(ctx, s, CycleKind.FULL, Trigger.ALERT))
                        }
                    } else if (store.state().failStreak != 0) {
                        store.updateState { it.copy(failStreak = 0) }
                    }
                }
            }
        }
    }

    /** В режиме тревоги: после [RECOVERY_OK] успешных проверок подряд возвращаемся к слежению. */
    private fun trackRecovery(ctx: Context, s: AppSettings, c: CycleRecord) {
        if (c.kind == CycleKind.SKIPPED) return
        val store = SettingsStore.get(ctx)
        val failing = isFailing(c)
        val st = store.updateState { if (failing) it.copy(okStreak = 0) else it.copy(okStreak = it.okStreak + 1) }
        if (!failing && st.okStreak >= RECOVERY_OK) {
            store.updateState { it.copy(phase = Phase.WATCH, okStreak = 0, failStreak = 0) }
            LogStore.get(ctx).event(EventType.PHASE_CHANGED, "VPN снова работает, проверяю как обычно", "Проверка VPN раз в ${s.watchIntervalMin} мин")
        }
    }

    const val RECOVERY_OK = 2

    /** «VPN пропал»: туннель не работает ни в одном профиле (или, без профилей, — сеть ограничена). */
    private fun isFailing(c: CycleRecord): Boolean =
        if (c.summary.vpnTested) !c.summary.vpnAny
        else c.summary.verdict == Verdict.WHITELIST || c.summary.verdict == Verdict.NO_INTERNET

    private suspend fun cycle(ctx: Context, s: AppSettings, kind: CycleKind, trigger: Trigger): CycleRecord {
        val store = SettingsStore.get(ctx)
        val log = LogStore.get(ctx)
        if (s.requireCellular) ensureCellular(ctx, s)
        _progress.value = kind.ru.replaceFirstChar { it.uppercase() } + "…"

        val xray = XrayTunnelFactory(ctx)
        val engine = CycleEngine(AndroidPlatform(ctx), if (xray.available()) xray else null)
        val rec = engine.run(kind, trigger, s.engineConfig())
        log.appendCycle(rec)

        val st = store.state()
        val (events, mem) = EventDetector.detect(st.memory, rec, ::fmt)
        val all = events.toMutableList()
        if (rec.kind == CycleKind.SKIPPED && !st.lastWasSkipped) {
            all += Event(rec.startedAt, EventType.SKIPPED, "Проверки на паузе: нет мобильного интернета", rec.notes.firstOrNull())
            if (rec.network.transport == "WIFI") Notifier.wifiWarning(ctx)
        }
        store.updateState { it.copy(memory = mem, lastWasSkipped = rec.kind == CycleKind.SKIPPED) }
        log.appendEvents(all)

        if (s.notifyEvents) {
            events.filter {
                (it.type == EventType.VERDICT_CHANGED && !it.title.startsWith("Начальное")) ||
                    it.type == EventType.VPN_DOWN || it.type == EventType.VPN_UP
            }.forEach { Notifier.event(ctx, it) }
        }
        return rec
    }

    /** На Android 9 и ниже можно выключить Wi-Fi самим; на новых — только попросить пользователя. */
    private suspend fun ensureCellular(ctx: Context, s: AppSettings) {
        if (!s.autoWifiOff || Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) return
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        val caps = cm.activeNetwork?.let { cm.getNetworkCapabilities(it) }
        if (caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) != true) return
        @Suppress("DEPRECATION")
        val ok = runCatching { ctx.applicationContext.getSystemService(WifiManager::class.java).setWifiEnabled(false) }.getOrDefault(false)
        if (!ok) return
        LogStore.get(ctx).event(EventType.NETWORK_CHANGED, "Приложение выключило Wi-Fi, чтобы проверить мобильный интернет")
        repeat(30) {
            delay(500)
            val c = cm.activeNetwork?.let { cm.getNetworkCapabilities(it) }
            if (c?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) == true) return
        }
    }
}
