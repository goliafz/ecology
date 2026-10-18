package ru.khodakovsky.wltester

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager

class WlApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CH_MONITOR, "Автопроверки", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Постоянное уведомление, пока идут автопроверки"
                setShowBadge(false)
            }
        )
        nm.createNotificationChannel(
            NotificationChannel(CH_EVENTS, "События", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Белые списки включили или отключили, VPN пропал или вернулся, нужно выключить Wi-Fi"
            }
        )
    }

    companion object {
        const val CH_MONITOR = "monitor"
        const val CH_EVENTS = "events"
    }
}
