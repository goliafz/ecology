package ru.khodakovsky.wltester.platform

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.telephony.CellIdentityNr
import android.telephony.CellInfoGsm
import android.telephony.CellInfoLte
import android.telephony.CellInfoNr
import android.telephony.CellInfoWcdma
import android.telephony.CellSignalStrengthLte
import android.telephony.CellSignalStrengthNr
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

        val r = readRadio()
        val mccMnc = runCatching { tm.networkOperator }.getOrNull()?.takeIf { it.isNotBlank() }
        return NetworkSnapshot(
            transport = transport,
            operatorName = operatorName(runCatching { tm.networkOperatorName }.getOrNull(), mccMnc),
            mccMnc = mccMnc,
            simOperator = runCatching { tm.simOperatorName }.getOrNull()?.takeIf { it.isNotBlank() },
            networkType = networkType(),
            signalDbm = r.dbm,
            signalLevel = r.level,
            rsrq = r.rsrq,
            sinr = r.sinr,
            rssi = r.rssi,
            timingAdvance = r.ta,
            cellId = r.cellId,
            pci = r.pci,
            tac = r.tac,
            earfcn = r.earfcn,
            band = r.band,
            roaming = runCatching { tm.isNetworkRoaming }.getOrNull(),
            vpnInSystem = vpnInSystem,
            ourTrafficViaVpn = viaVpn,
            wifiConnected = wifi,
        )
    }

    private class Radio {
        var dbm: Int? = null
        var level: Int? = null
        var rsrq: Int? = null
        var sinr: Double? = null
        var rssi: Int? = null
        var ta: Int? = null
        var cellId: Long? = null
        var pci: Int? = null
        var tac: Int? = null
        var earfcn: Int? = null
        var band: String? = null
    }

    /** Android отдаёт Int.MAX_VALUE, когда значения нет. */
    private fun valid(v: Int, range: IntRange): Int? = v.takeIf { it != Int.MAX_VALUE && it in range }

    /** Уровень, качество и шум сигнала, а при доступе к местоположению ещё и номер вышки. */
    private fun readRadio(): Radio {
        val r = Radio()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            runCatching {
                tm.signalStrength?.let { s ->
                    r.level = s.level
                    val css = s.cellSignalStrengths
                    val lte = css.filterIsInstance<CellSignalStrengthLte>().firstOrNull()
                    val nr = css.filterIsInstance<CellSignalStrengthNr>().firstOrNull()
                    when {
                        lte != null -> fillLte(r, lte)
                        nr != null -> fillNr(r, nr)
                        else -> r.dbm = css.firstOrNull()?.dbm?.let { valid(it, -150..-20) }
                    }
                }
            }
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { tm.signalStrength?.let { r.level = it.level } }
        }
        if (hasLocation()) runCatching { fillCell(r) }
        return r
    }

    private fun fillLte(r: Radio, lte: CellSignalStrengthLte) {
        r.dbm = r.dbm ?: valid(lte.rsrp, -140..-40)
        r.rsrq = r.rsrq ?: valid(lte.rsrq, -34..10)
        // Одни прошивки отдают дБ, другие десятые доли дБ.
        r.sinr = r.sinr ?: valid(lte.rssnr, -300..300)?.let { if (it < -25 || it > 35) it / 10.0 else it.toDouble() }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) r.rssi = r.rssi ?: valid(lte.rssi, -120..-20)
        r.ta = r.ta ?: valid(lte.timingAdvance, 0..1282)
    }

    private fun fillNr(r: Radio, nr: CellSignalStrengthNr) {
        r.dbm = r.dbm ?: valid(nr.ssRsrp, -156..-31)
        r.rsrq = r.rsrq ?: valid(nr.ssRsrq, -43..20)
        r.sinr = r.sinr ?: valid(nr.ssSinr, -23..40)?.toDouble()
    }

    @SuppressLint("MissingPermission")
    private fun fillCell(r: Radio) {
        val cell = tm.allCellInfo.orEmpty().firstOrNull { it.isRegistered } ?: return
        when (cell) {
            is CellInfoLte -> {
                val id = cell.cellIdentity
                r.cellId = valid(id.ci, 0..268435455)?.toLong()
                r.pci = valid(id.pci, 0..503)
                r.tac = valid(id.tac, 0..65535)
                r.earfcn = valid(id.earfcn, 0..262143)
                r.band = (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) id.bands.firstOrNull()?.let { "B$it" } else null)
                    ?: r.earfcn?.let { lteBand(it) }
                fillLte(r, cell.cellSignalStrength)
            }
            is CellInfoWcdma -> {
                r.cellId = valid(cell.cellIdentity.cid, 0..268435455)?.toLong()
                r.tac = valid(cell.cellIdentity.lac, 0..65535)
                r.dbm = r.dbm ?: valid(cell.cellSignalStrength.dbm, -150..-20)
            }
            is CellInfoGsm -> {
                r.cellId = valid(cell.cellIdentity.cid, 0..65535)?.toLong()
                r.tac = valid(cell.cellIdentity.lac, 0..65535)
                r.dbm = r.dbm ?: valid(cell.cellSignalStrength.dbm, -150..-20)
            }
            else -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && cell is CellInfoNr) {
                val id = cell.cellIdentity as CellIdentityNr
                r.cellId = id.nci.takeIf { it != Long.MAX_VALUE && it >= 0 }
                r.pci = valid(id.pci, 0..1007)
                r.tac = valid(id.tac, 0..16777215)
                r.earfcn = valid(id.nrarfcn, 0..3279165)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) r.band = id.bands.firstOrNull()?.let { "n$it" }
                fillNr(r, cell.cellSignalStrength as CellSignalStrengthNr)
            }
        }
    }

    private fun hasLocation(): Boolean =
        ContextCompat.checkSelfPermission(ctx, Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    /** Диапазон LTE по номеру канала: основные диапазоны в России. */
    private fun lteBand(earfcn: Int): String? = when (earfcn) {
        in 0..599 -> "B1"
        in 1200..1949 -> "B3"
        in 2750..3449 -> "B7"
        in 6150..6449 -> "B20"
        in 9870..9919 -> "B31"
        in 37750..38249 -> "B38"
        in 38650..39649 -> "B40"
        else -> null
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

    override fun ping(ip: String, count: Int): PingParser.Stats? {
        val bin = listOf("/system/bin/ping", "/system/xbin/ping").firstOrNull { java.io.File(it).exists() } ?: return null
        return try {
            val p = ProcessBuilder(bin, "-c", count.toString(), "-W", "2", "-i", "0.5", ip).redirectErrorStream(true).start()
            val out = StringBuilder()
            val reader = Thread { runCatching { out.append(p.inputStream.bufferedReader().readText()) } }
            reader.start()
            if (!p.waitFor(count / 2L + 8, TimeUnit.SECONDS)) p.destroy()
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
