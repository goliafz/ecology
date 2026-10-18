package ru.khodakovsky.wltester

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager

class WlApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CH_MONITOR, "Мониторинг", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Постоянное уведомление, пока идёт мониторинг"
                setShowBadge(false)
            }
        )
        nm.createNotificationChannel(
            NotificationChannel(CH_EVENTS, "События", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Включение/отключение белых списков, пропадание VPN, Wi-Fi"
            }
        )
    }

    companion object {
        const val CH_MONITOR = "monitor"
        const val CH_EVENTS = "events"
    }
}
