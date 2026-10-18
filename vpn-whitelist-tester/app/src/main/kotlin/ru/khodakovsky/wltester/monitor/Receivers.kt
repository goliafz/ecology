package ru.khodakovsky.wltester.monitor

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.data.LogStore

class AlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        try {
            MonitorService.start(context, MonitorService.ACTION_RUN)
        } catch (e: Exception) {
            // Например, ForegroundServiceStartNotAllowedException — запишем и попробуем на следующем будильнике.
            LogStore.get(context).event(EventType.ERROR, "Система не дала запустить проверку", e.toString())
            Scheduler.schedule(context)
        }
    }

    companion object {
        const val ACTION = "ru.khodakovsky.wltester.ALARM"
    }
}

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Scheduler.schedule(context)
    }
}
