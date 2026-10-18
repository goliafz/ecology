package ru.khodakovsky.wltester.platform

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import ru.khodakovsky.wltester.core.NetworkSnapshot
import ru.khodakovsky.wltester.core.engine.EnginePlatform
import ru.khodakovsky.wltester.core.probe.PingParser
import java.util.concurrent.TimeUnit

class AndroidPlatform(private val ctx: Context) : EnginePlatform {

    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private val tm = ctx.getSystemService(TelephonyManager::class.java)

    override fun network(): NetworkSnapshot {
        val active = cm.activeNetwork
        val caps = active?.let { cm.getNetworkCapabilities(it) }
        val viaVpn = caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true

        @Suppress("DEPRECATION")
        val all = runCatching { cm.allNetworks.mapNotNull { cm.getNetworkCapabilities(it) } }.getOrDefault(emptyList())
        val vpnInSystem = all.any { it.hasTransport(NetworkCapabilities.TRANSPORT_VPN) }
        val wifi = all.any { it.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) }

        val physical = if (viaVpn) {
            // Если трафик идёт через VPN — смотрим, какая физическая сеть под ним.
            all.firstOrNull { !it.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) }
        } else caps
        val transport = when {
            physical == null -> "NONE"
            physical.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "WIFI"
            physical.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "CELLULAR"
            physical.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ETHERNET"
            else -> "OTHER"
        }

        var signalDbm: Int? = null
        var signalLevel: Int? = null
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            runCatching {
                tm.signalStrength?.let { s ->
                    signalLevel = s.level
                    signalDbm = s.cellSignalStrengths.firstOrNull()?.dbm?.takeIf { it in -150..-20 }
                }
            }
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { tm.signalStrength?.let { signalLevel = it.level } }
        }

        val mccMnc = runCatching { tm.networkOperator }.getOrNull()?.takeIf { it.isNotBlank() }
        return NetworkSnapshot(
            transport = transport,
            operatorName = operatorName(runCatching { tm.networkOperatorName }.getOrNull(), mccMnc),
            mccMnc = mccMnc,
            simOperator = runCatching { tm.simOperatorName }.getOrNull()?.takeIf { it.isNotBlank() },
            networkType = networkType(),
            signalDbm = signalDbm,
            signalLevel = signalLevel,
            roaming = runCatching { tm.isNetworkRoaming }.getOrNull(),
            vpnInSystem = vpnInSystem,
            ourTrafficViaVpn = viaVpn,
            wifiConnected = wifi,
        )
    }

    /** Некоторые прошивки отдают сокращение вроде «б.» — тогда берём имя по коду сети. */
    private fun operatorName(raw: String?, mccMnc: String?): String? {
        val name = raw?.trim().orEmpty()
        val known = OPERATORS[mccMnc]
        return when {
            name.length >= 3 -> name
            known != null -> known
            else -> name.ifEmpty { null }
        }
    }

    @SuppressLint("MissingPermission")
    private fun networkType(): String? {
        if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) return null
        val t = runCatching { tm.dataNetworkType }.getOrNull() ?: return null
        return when (t) {
            TelephonyManager.NETWORK_TYPE_NR -> "5G"
            TelephonyManager.NETWORK_TYPE_LTE -> "4G LTE"
            19 -> "4G LTE-CA"
            TelephonyManager.NETWORK_TYPE_HSPAP, TelephonyManager.NETWORK_TYPE_HSPA, TelephonyManager.NETWORK_TYPE_HSDPA,
            TelephonyManager.NETWORK_TYPE_HSUPA, TelephonyManager.NETWORK_TYPE_UMTS, TelephonyManager.NETWORK_TYPE_EVDO_0,
            TelephonyManager.NETWORK_TYPE_EVDO_A, TelephonyManager.NETWORK_TYPE_EVDO_B, TelephonyManager.NETWORK_TYPE_EHRPD,
            TelephonyManager.NETWORK_TYPE_TD_SCDMA -> "3G"
            TelephonyManager.NETWORK_TYPE_EDGE, TelephonyManager.NETWORK_TYPE_GPRS, TelephonyManager.NETWORK_TYPE_CDMA,
            TelephonyManager.NETWORK_TYPE_1xRTT, TelephonyManager.NETWORK_TYPE_IDEN, TelephonyManager.NETWORK_TYPE_GSM -> "2G"
            TelephonyManager.NETWORK_TYPE_IWLAN -> "IWLAN"
            TelephonyManager.NETWORK_TYPE_UNKNOWN -> null
            else -> "тип $t"
        }
    }

    override fun ping(ip: String): PingParser.Stats? {
        val bin = listOf("/system/bin/ping", "/system/xbin/ping").firstOrNull { java.io.File(it).exists() } ?: return null
        return try {
            val p = ProcessBuilder(bin, "-c", "3", "-W", "2", "-i", "0.5", ip).redirectErrorStream(true).start()
            val out = StringBuilder()
            val reader = Thread { runCatching { out.append(p.inputStream.bufferedReader().readText()) } }
            reader.start()
            if (!p.waitFor(10, TimeUnit.SECONDS)) p.destroy()
            reader.join(1000)
            PingParser.parse(out.toString())
        } catch (e: Exception) {
            null
        }
    }

    companion object {
        private val OPERATORS = mapOf(
            "25001" to "МТС",
            "25002" to "МегаФон",
            "25099" to "Билайн",
            "25020" to "T2",
            "25011" to "Yota",
            "25035" to "Мотив",
        )
    }
}
