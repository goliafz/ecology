package ru.khodakovsky.wltester.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Что именно проверялось. */
@Serializable
enum class Category(val ru: String) {
    RU("Российские сайты"),
    FOREIGN("Зарубежные сайты"),
    DNS("Поиск адресов сайтов (DNS)"),
    QUALITY("Качество связи"),
    SERVER("Ваш VPN-сервер"),
    VPN("Сайты через VPN"),
    DOWNLOAD("Скачивание файла"),
    INFO("Сведения"),
}

/** На каком этапе соединения проверка завершилась. */
@Serializable
enum class Stage(val ru: String) { DNS("DNS"), TCP("TCP"), TLS("TLS"), HTTP("HTTP"), OTHER("-") }

@Serializable
enum class ProbeStatus(val ru: String, val stage: Stage, val reachable: Boolean) {
    OK("открылся", Stage.OTHER, true),
    CERT_ERROR("открылся, но сертификат не подтвердился", Stage.TLS, true),
    DNS_FAIL("не нашёлся адрес сайта (DNS)", Stage.DNS, false),
    TCP_TIMEOUT("сайт не отвечает (TCP, вышло время)", Stage.TCP, false),
    TCP_REFUSED("сайт отказал в подключении (TCP)", Stage.TCP, false),
    TCP_RESET("подключение сразу оборвали (TCP)", Stage.TCP, false),
    TCP_UNREACHABLE("сайт недоступен из этой сети (TCP)", Stage.TCP, false),
    TCP_ERROR("ошибка подключения (TCP)", Stage.TCP, false),
    TLS_TIMEOUT("подключение зависло на шифровании (TLS)", Stage.TLS, false),
    TLS_RESET("подключение оборвали на шифровании (TLS)", Stage.TLS, false),
    TLS_ERROR("ошибка шифрования (TLS)", Stage.TLS, false),
    HTTP_TIMEOUT("сайт не прислал ответ", Stage.HTTP, false),
    HTTP_RESET("ответ сайта оборвался", Stage.HTTP, false),
    HTTP_ERROR("сайт ответил непонятно", Stage.HTTP, false),
    DATA_CORRUPT("данные пришли испорченными (TLS)", Stage.HTTP, false),
    STALLED("скачивание зависло на полпути", Stage.HTTP, false),
    PROXY_ERROR("внутренняя ошибка проверки VPN", Stage.TCP, false),
    SKIPPED("пропущено", Stage.OTHER, false),
    ERROR("ошибка", Stage.OTHER, false),
}

@Serializable
data class CheckResult(
    val category: Category,
    val target: String,
    /** "direct" — напрямую, иначе имя VPN-профиля. */
    val route: String = DIRECT,
    val status: ProbeStatus,
    val ip: String? = null,
    val port: Int? = null,
    val sni: String? = null,
    val dnsMs: Long? = null,
    val tcpMs: Long? = null,
    val tlsMs: Long? = null,
    val ttfbMs: Long? = null,
    val totalMs: Long = 0,
    val httpCode: Int? = null,
    val bytes: Long? = null,
    val speedKbps: Long? = null,
    val certIssuer: String? = null,
    val info: String? = null,
    val error: String? = null,
) {
    val ok: Boolean get() = status.reachable

    companion object {
        const val DIRECT = "direct"
    }
}

@Serializable
enum class CycleKind(val ru: String) { FULL("полная проверка"), QUICK("быстрая проверка VPN"), SKIPPED("проверка пропущена") }

@Serializable
enum class Trigger(val ru: String) {
    WINDOW("по расписанию"),
    WATCH("слежение за VPN"),
    ALERT("VPN пропал, частые проверки"),
    MANUAL("вручную"),
}

@Serializable
enum class Verdict(val ru: String) {
    NORMAL("Обычный интернет"),
    WHITELIST("Белые списки"),
    PARTIAL("Есть сбои"),
    NO_INTERNET("Нет интернета"),
    UNKNOWN("Нет данных"),
}

@Serializable
data class NetworkSnapshot(
    /** CELLULAR, WIFI, ETHERNET, NONE, OTHER */
    val transport: String = "NONE",
    val operatorName: String? = null,
    val mccMnc: String? = null,
    /** Оператор SIM-карты, через которую идёт интернет (может отличаться от оператора сети). */
    val simOperator: String? = null,
    val simMccMnc: String? = null,
    /** Слот SIM с мобильным интернетом: 1 или 2. */
    val dataSimSlot: Int? = null,
    val networkType: String? = null,
    /** Уровень сигнала: для 4G/5G это RSRP, для 2G/3G просто dBm. */
    val signalDbm: Int? = null,
    /** Полоски Android, 0..4. */
    val signalLevel: Int? = null,
    /** Качество сигнала (RSRQ), дБ: ближе к нулю лучше. */
    val rsrq: Int? = null,
    /** Сигнал/шум (SINR/RSSNR), дБ: чем больше, тем лучше. */
    val sinr: Double? = null,
    /** Полная мощность в канале (RSSI), dBm. */
    val rssi: Int? = null,
    /** Timing advance: по нему оценивается расстояние до вышки. */
    val timingAdvance: Int? = null,
    /** Идентификатор соты (для LTE eNB = cellId / 256). Нужен доступ к местоположению. */
    val cellId: Long? = null,
    val pci: Int? = null,
    val tac: Int? = null,
    val earfcn: Int? = null,
    val band: String? = null,
    /** Почему номер вышки не записан (нет разрешения, выключена геолокация и т.п.). */
    val cellNote: String? = null,
    val roaming: Boolean? = null,
    /** В системе поднят какой-то VPN (например, NekoBox). */
    val vpnInSystem: Boolean = false,
    /** Трафик самого тестера идёт через VPN — результаты прямых проверок недостоверны. */
    val ourTrafficViaVpn: Boolean = false,
    val wifiConnected: Boolean = false,
) {
    val isCellular: Boolean get() = transport == "CELLULAR"

    /** Сеть другого оператора, чем SIM: роуминг или общая сеть операторов. */
    val foreignNetwork: Boolean
        get() = mccMnc != null && simMccMnc != null && mccMnc != simMccMnc

    /** «Билайн» или «MTS RUS (SIM: Билайн)», если сеть чужая. */
    fun operatorText(): String? {
        val net = operatorName?.takeIf { it.isNotBlank() } ?: return simOperator
        return if (foreignNetwork && simOperator != null) "$net (SIM: $simOperator)" else net
    }

    /** Примерное расстояние до вышки по timing advance (LTE: ~78 м на единицу). */
    val towerDistanceM: Int? get() = timingAdvance?.takeIf { it in 0..1282 }?.let { it * 78 }

    /** Номер базовой станции для LTE (eNB) и сектор. */
    val enb: Long? get() = cellId?.takeIf { networkType?.contains("LTE") == true }?.let { it / 256 }
    val sector: Int? get() = cellId?.takeIf { networkType?.contains("LTE") == true }?.let { (it % 256).toInt() }

    fun cellLabel(): String? = when {
        enb != null -> "вышка $enb, сектор $sector"
        cellId != null -> "сота $cellId"
        else -> null
    }

    /** «хороший: -95 dBm, сигнал/шум 8 дБ, вышка 123456, сектор 2» */
    fun signalText(): String? {
        val g = signalGrade() ?: return null
        val parts = listOfNotNull(
            signalDbm?.let { "$it dBm" },
            sinr?.let { "сигнал/шум ${it.toInt()} дБ" },
            cellLabel(),
        )
        return g.ru + if (parts.isNotEmpty()) ": " + parts.joinToString(", ") else ""
    }

    /** Оценка сигнала простыми словами: худшая из уровня и шума. */
    fun signalGrade(): SignalGrade? {
        val byLevel = signalDbm?.let {
            when {
                it >= -90 -> SignalGrade.EXCELLENT
                it >= -105 -> SignalGrade.GOOD
                it >= -115 -> SignalGrade.WEAK
                else -> SignalGrade.BAD
            }
        }
        val byNoise = sinr?.let {
            when {
                it >= 13 -> SignalGrade.EXCELLENT
                it >= 5 -> SignalGrade.GOOD
                it >= 0 -> SignalGrade.WEAK
                else -> SignalGrade.BAD
            }
        }
        return listOfNotNull(byLevel, byNoise).maxByOrNull { it.ordinal }
    }

    fun shortRu(): String = buildString {
        append(
            when (transport) {
                "CELLULAR" -> "Мобильный интернет"
                "WIFI" -> "Wi-Fi"
                "ETHERNET" -> "Ethernet"
                "NONE" -> "Нет интернета"
                else -> transport
            }
        )
        operatorText()?.let { append(" · ").append(it) }
        networkType?.let { append(" · ").append(it) }
        signalDbm?.let { append(" · ").append(it).append(" dBm") }
    }
}

enum class SignalGrade(val ru: String) {
    EXCELLENT("отличный"),
    GOOD("хороший"),
    WEAK("слабый"),
    BAD("очень слабый"),
}

@Serializable
data class Summary(
    val verdict: Verdict = Verdict.UNKNOWN,
    val ruOk: Int = 0,
    val ruTotal: Int = 0,
    val foreignOk: Int = 0,
    val foreignTotal: Int = 0,
    /** Профиль VPN -> туннель работает. */
    val vpn: Map<String, Boolean> = emptyMap(),
    val serverTcpOk: Int = 0,
    val serverTcpTotal: Int = 0,
    /** Мессенджер через VPN -> работает (хотя бы через один профиль). */
    val messengers: Map<String, Boolean> = emptyMap(),
) {
    val vpnAny: Boolean get() = vpn.values.any { it }
    val vpnTested: Boolean get() = vpn.isNotEmpty()
}

@Serializable
data class CycleRecord(
    val id: String,
    val startedAt: Long,
    val finishedAt: Long,
    val kind: CycleKind,
    val trigger: Trigger,
    val network: NetworkSnapshot,
    val publicIpDirect: String? = null,
    val publicIpVpn: Map<String, String> = emptyMap(),
    val checks: List<CheckResult> = emptyList(),
    val summary: Summary = Summary(),
    val notes: List<String> = emptyList(),
)

@Serializable
enum class EventType(val ru: String) {
    VERDICT_CHANGED("Интернет"),
    VPN_DOWN("VPN пропал"),
    VPN_UP("VPN вернулся"),
    NETWORK_CHANGED("Сеть"),
    PHASE_CHANGED("Режим проверок"),
    MESSENGER("Мессенджеры"),
    SERVICE("Приложение"),
    SKIPPED("Проверка пропущена"),
    ERROR("Ошибка"),
}

@Serializable
data class Event(
    val ts: Long,
    val type: EventType,
    val title: String,
    val details: String? = null,
)

val WlJson: Json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
    coerceInputValues = true
}
