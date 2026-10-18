package ru.khodakovsky.wltester.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Что именно проверялось. */
@Serializable
enum class Category(val ru: String) {
    RU("РФ напрямую"),
    FOREIGN("Зарубежные напрямую"),
    DNS("DNS"),
    SERVER("Сервер VPN"),
    VPN("Через VPN"),
    DOWNLOAD("Загрузка"),
    INFO("Инфо"),
}

/** На каком этапе соединения проверка завершилась. */
@Serializable
enum class Stage(val ru: String) { DNS("DNS"), TCP("TCP"), TLS("TLS"), HTTP("HTTP"), OTHER("—") }

@Serializable
enum class ProbeStatus(val ru: String, val stage: Stage, val reachable: Boolean) {
    OK("OK", Stage.OTHER, true),
    CERT_ERROR("TLS ок, сертификат не прошёл проверку", Stage.TLS, true),
    DNS_FAIL("DNS: имя не разрешилось", Stage.DNS, false),
    TCP_TIMEOUT("TCP: таймаут соединения", Stage.TCP, false),
    TCP_REFUSED("TCP: соединение отклонено", Stage.TCP, false),
    TCP_RESET("TCP: сброс соединения", Stage.TCP, false),
    TCP_UNREACHABLE("TCP: сеть/хост недостижимы", Stage.TCP, false),
    TCP_ERROR("TCP: ошибка", Stage.TCP, false),
    TLS_TIMEOUT("TLS: таймаут рукопожатия", Stage.TLS, false),
    TLS_RESET("TLS: сброс/обрыв при рукопожатии", Stage.TLS, false),
    TLS_ERROR("TLS: ошибка рукопожатия", Stage.TLS, false),
    HTTP_TIMEOUT("HTTP: нет ответа (таймаут)", Stage.HTTP, false),
    HTTP_RESET("HTTP: обрыв соединения", Stage.HTTP, false),
    HTTP_ERROR("HTTP: ошибка", Stage.HTTP, false),
    STALLED("Загрузка зависла (данные перестали идти)", Stage.HTTP, false),
    PROXY_ERROR("Ошибка локального прокси xray", Stage.TCP, false),
    SKIPPED("Пропущено", Stage.OTHER, false),
    ERROR("Ошибка", Stage.OTHER, false),
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
enum class CycleKind(val ru: String) { FULL("Полная"), QUICK("Быстрая"), SKIPPED("Пропуск") }

@Serializable
enum class Trigger(val ru: String) {
    WINDOW("По расписанию"),
    WATCH("Слежение за VPN"),
    ALERT("VPN пропал — подробно"),
    MANUAL("Вручную"),
}

@Serializable
enum class Verdict(val ru: String) {
    NORMAL("Норма"),
    WHITELIST("Белые списки"),
    PARTIAL("Частичные ограничения"),
    NO_INTERNET("Нет интернета"),
    UNKNOWN("Нет данных"),
}

@Serializable
data class NetworkSnapshot(
    /** CELLULAR, WIFI, ETHERNET, NONE, OTHER */
    val transport: String = "NONE",
    val operatorName: String? = null,
    val mccMnc: String? = null,
    val simOperator: String? = null,
    val networkType: String? = null,
    val signalDbm: Int? = null,
    val signalLevel: Int? = null,
    val roaming: Boolean? = null,
    /** В системе поднят какой-то VPN (например, NekoBox). */
    val vpnInSystem: Boolean = false,
    /** Трафик самого тестера идёт через VPN — результаты прямых проверок недостоверны. */
    val ourTrafficViaVpn: Boolean = false,
    val wifiConnected: Boolean = false,
) {
    val isCellular: Boolean get() = transport == "CELLULAR"

    fun shortRu(): String = buildString {
        append(
            when (transport) {
                "CELLULAR" -> "Моб. сеть"
                "WIFI" -> "Wi-Fi"
                "ETHERNET" -> "Ethernet"
                "NONE" -> "Нет сети"
                else -> transport
            }
        )
        operatorName?.takeIf { it.isNotBlank() }?.let { append(" · ").append(it) }
        networkType?.let { append(" · ").append(it) }
        signalDbm?.let { append(" · ").append(it).append(" dBm") }
    }
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
    VERDICT_CHANGED("Смена режима сети"),
    VPN_DOWN("VPN недоступен"),
    VPN_UP("VPN восстановлен"),
    NETWORK_CHANGED("Смена сети"),
    PHASE_CHANGED("Режим мониторинга"),
    SERVICE("Служба"),
    SKIPPED("Пропуск проверки"),
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
