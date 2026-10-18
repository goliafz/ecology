package ru.khodakovsky.wltester.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Что именно проверялось. */
@Serializable
enum class Category(val ru: String) {
    RU("Российские сайты"),
    FOREIGN("Зарубежные сайты"),
    DNS("Поиск адресов сайтов (DNS)"),
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
                "CELLULAR" -> "Мобильный интернет"
                "WIFI" -> "Wi-Fi"
                "ETHERNET" -> "Ethernet"
                "NONE" -> "Нет интернета"
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
    VERDICT_CHANGED("Интернет"),
    VPN_DOWN("VPN пропал"),
    VPN_UP("VPN вернулся"),
    NETWORK_CHANGED("Сеть"),
    PHASE_CHANGED("Режим проверок"),
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
