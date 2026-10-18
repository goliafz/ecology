package ru.khodakovsky.wltester.monitor

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.provider.Settings
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import ru.khodakovsky.wltester.R
import ru.khodakovsky.wltester.WlApp
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.Phase
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.ui.MainActivity
import java.util.concurrent.atomic.AtomicInteger

/**
 * Служба переднего плана: держит процесс живым, пока мониторинг активен, и выполняет циклы по будильникам.
 * Вне окна расписания служба останавливается, а будильник ставится на начало окна.
 */
class MonitorService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val jobs = AtomicInteger(0)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        isRunning = true
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        goForeground(Notifier.monitor(this, Notifier.statusText(this, running = true)))
        when (intent?.action) {
            ACTION_DISABLE -> {
                SettingsStore.get(this).update { it.copy(monitoringEnabled = false) }
                LogStore.get(this).event(EventType.SERVICE, "Автопроверки выключены из уведомления")
                Scheduler.schedule(this)
                stopNow()
            }
            ACTION_STOP -> if (jobs.get() == 0) stopNow()
            ACTION_RUN_MANUAL -> launch(MonitorRunner.Reason.MANUAL)
            else -> launch(MonitorRunner.Reason.ALARM)
        }
        return START_NOT_STICKY
    }

    private fun launch(reason: MonitorRunner.Reason) {
        jobs.incrementAndGet()
        scope.launch {
            val pm = getSystemService(PowerManager::class.java)
            val wl = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "wltester:cycle").apply {
                setReferenceCounted(false)
                acquire(10 * 60 * 1000L)
            }
            try {
                MonitorRunner.run(applicationContext, reason)
            } catch (e: Throwable) {
                LogStore.get(applicationContext).event(EventType.ERROR, "Проверка завершилась с ошибкой", e.toString().take(500))
            } finally {
                Scheduler.schedule(applicationContext)
                if (wl.isHeld) wl.release()
                if (jobs.decrementAndGet() == 0) afterRun()
            }
        }
    }

    private fun afterRun() {
        val store = SettingsStore.get(this)
        val s = store.get()
        if (Scheduler.isActiveNow(s)) {
            Notifier.update(this, Notifier.monitor(this, Notifier.statusText(this, running = false)))
        } else {
            if (store.state().windowActive) {
                store.updateState { it.copy(windowActive = false) }
                if (s.monitoringEnabled) {
                    LogStore.get(this).event(EventType.SERVICE, "Проверки по расписанию закончились", "Следующие начнутся ${MonitorRunner.fmt(store.state().nextRunAt)}")
                }
            }
            stopNow()
        }
    }

    private fun goForeground(n: Notification) {
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(Notifier.ID_MONITOR, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(Notifier.ID_MONITOR, n)
        }
    }

    private fun stopNow() {
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        isRunning = false
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_RUN = "ru.khodakovsky.wltester.RUN"
        const val ACTION_RUN_MANUAL = "ru.khodakovsky.wltester.RUN_MANUAL"
        const val ACTION_STOP = "ru.khodakovsky.wltester.STOP"
        const val ACTION_DISABLE = "ru.khodakovsky.wltester.DISABLE"

        @Volatile
        var isRunning = false
            private set

        fun start(ctx: Context, action: String) {
            ContextCompat.startForegroundService(ctx, Intent(ctx, MonitorService::class.java).setAction(action))
        }
    }
}

object Notifier {
    const val ID_MONITOR = 1
    private const val ID_WIFI = 2

    fun statusText(ctx: Context, running: Boolean): String {
        val store = SettingsStore.get(ctx)
        val s = store.get()
        val st = store.state()
        val mode = when (s.scheduleMode) {
            ScheduleMode.WINDOW -> "По расписанию с ${MonitorRunner.hhmm(s.windowStart)} до ${MonitorRunner.hhmm(s.windowEnd)}, раз в ${s.fullIntervalMin} мин"
            ScheduleMode.ALWAYS -> if (st.phase == Phase.ALERT) "VPN пропал, подробно раз в ${s.alertIntervalMin} мин"
            else "Слежу за VPN раз в ${if (Scheduler.inWindow(s)) s.nightWatchMin else s.watchIntervalMin} мин"
        }
        val last = LogStore.get(ctx).lastCycle()?.let { c ->
            val vpn = if (c.summary.vpnTested) (if (c.summary.vpnAny) ", VPN работает" else ", VPN не работает") else ""
            ". Сейчас: ${c.summary.verdict.ru.lowercase()}$vpn"
        } ?: ""
        val next = if (running) "идёт проверка" else st.nextRunAt.takeIf { it > 0 }?.let { "следующая в ${MonitorRunner.fmt(it).substringAfter(' ').take(5)}" } ?: ""
        return "$mode, $next$last"
    }

    fun monitor(ctx: Context, text: String): Notification {
        val open = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val run = PendingIntent.getService(
            ctx, 10, Intent(ctx, MonitorService::class.java).setAction(MonitorService.ACTION_RUN_MANUAL), PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            ctx, 11, Intent(ctx, MonitorService::class.java).setAction(MonitorService.ACTION_DISABLE), PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(ctx, WlApp.CH_MONITOR)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("WL Tester проверяет интернет")
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(0, "Проверить сейчас", run)
            .addAction(0, "Выключить проверки", stop)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    fun update(ctx: Context, n: Notification) = post(ctx, ID_MONITOR, n)

    fun event(ctx: Context, e: Event) {
        val open = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = NotificationCompat.Builder(ctx, WlApp.CH_EVENTS)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(e.title)
            .setContentText(e.details ?: MonitorRunner.fmt(e.ts))
            .setStyle(NotificationCompat.BigTextStyle().bigText("${MonitorRunner.fmt(e.ts)}\n${e.details ?: ""}"))
            .setAutoCancel(true)
            .setContentIntent(open)
            .build()
        post(ctx, 100 + (e.ts / 1000 % 100000).toInt(), n)
    }

    fun wifiWarning(ctx: Context) {
        val panel = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) Intent(Settings.Panel.ACTION_WIFI) else Intent(Settings.ACTION_WIFI_SETTINGS)
        val pi = PendingIntent.getActivity(ctx, 3, panel.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE)
        val n = NotificationCompat.Builder(ctx, WlApp.CH_EVENTS)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Выключите Wi-Fi")
            .setContentText("Проверять нужно мобильный интернет, а сейчас подключён Wi-Fi.")
            .setAutoCancel(true)
            .setContentIntent(pi)
            .build()
        post(ctx, ID_WIFI, n)
    }

    private fun post(ctx: Context, id: Int, n: Notification) {
        val nm = NotificationManagerCompat.from(ctx)
        if (!nm.areNotificationsEnabled()) return
        try {
            nm.notify(id, n)
        } catch (e: SecurityException) {
            ctx.getSystemService(NotificationManager::class.java)?.cancel(id)
        }
    }
}
