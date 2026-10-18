package ru.khodakovsky.wltester.core

import kotlinx.serialization.Serializable
import java.net.URI
import java.net.URLDecoder

/** Профиль VLESS, разобранный из ссылки vless://… (как в подписке Marzban). */
@Serializable
data class VlessProfile(
    val label: String,
    val uuid: String,
    val host: String,
    val port: Int,
    /** reality | tls | none */
    val security: String = "reality",
    val network: String = "tcp",
    val flow: String = "",
    val sni: String = "",
    val fingerprint: String = "chrome",
    val publicKey: String = "",
    val shortId: String = "",
    val spiderX: String = "",
) {
    /** SNI, который увидит провайдер. */
    val effectiveSni: String get() = sni.ifBlank { host }
}

class VlessParseException(message: String) : Exception(message)

object VlessLinks {

    /** Разбирает список ссылок (по одной в строке, строки с # игнорируются). Имена делаются уникальными. */
    fun parseAll(text: String): Pair<List<VlessProfile>, List<String>> {
        val profiles = mutableListOf<VlessProfile>()
        val errors = mutableListOf<String>()
        text.lineSequence().map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("#") }
            .forEachIndexed { i, line ->
                try {
                    profiles += parse(line)
                } catch (e: Exception) {
                    errors += "Строка ${i + 1}: ${e.message}"
                }
            }
        val seen = mutableMapOf<String, Int>()
        val unique = profiles.map { p ->
            val n = (seen[p.label] ?: 0) + 1
            seen[p.label] = n
            if (n == 1) p else p.copy(label = "${p.label} #$n")
        }
        return unique to errors
    }

    fun parse(link: String): VlessProfile {
        val trimmed = link.trim()
        if (!trimmed.startsWith("vless://", ignoreCase = true)) throw VlessParseException("ссылка должна начинаться с vless://")
        val uri = try {
            URI(trimmed)
        } catch (e: Exception) {
            throw VlessParseException("некорректная ссылка: ${e.message}")
        }
        val uuid = uri.rawUserInfo?.let { decode(it) }?.takeIf { it.isNotBlank() }
            ?: throw VlessParseException("нет UUID")
        val host = uri.host?.removePrefix("[")?.removeSuffix("]") ?: throw VlessParseException("нет адреса сервера")
        val port = uri.port.takeIf { it > 0 } ?: 443
        val q = parseQuery(uri.rawQuery)
        val security = q["security"]?.lowercase()?.ifBlank { null } ?: "none"
        val network = q["type"]?.lowercase()?.ifBlank { null } ?: "tcp"
        if (network != "tcp" && network != "raw") throw VlessParseException("поддерживается только type=tcp (сейчас $network)")
        if (security !in setOf("reality", "tls", "none")) throw VlessParseException("неизвестный security=$security")
        if (security == "reality" && q["pbk"].isNullOrBlank()) throw VlessParseException("для Reality нужен pbk (публичный ключ)")
        val name = uri.fragment?.trim().orEmpty()
        return VlessProfile(
            label = shortLabel(name, host, port),
            uuid = uuid,
            host = host,
            port = port,
            security = security,
            network = "tcp",
            flow = q["flow"].orEmpty(),
            sni = q["sni"].orEmpty(),
            fingerprint = q["fp"]?.ifBlank { null } ?: "chrome",
            publicKey = q["pbk"].orEmpty(),
            shortId = q["sid"].orEmpty(),
            spiderX = q["spx"].orEmpty(),
        )
    }

    /** "🚀 Marz (alex) [Основной]" -> "Основной". */
    fun shortLabel(name: String, host: String, port: Int): String {
        val bracket = Regex("\\[([^\\]]+)]").findAll(name).lastOrNull()?.groupValues?.get(1)?.trim()
        if (!bracket.isNullOrBlank()) return bracket
        val clean = name.filter { it.isLetterOrDigit() || it in " ()-_.:" }.trim()
        return clean.ifBlank { "$host:$port" }
    }

    private fun parseQuery(raw: String?): Map<String, String> {
        if (raw.isNullOrBlank()) return emptyMap()
        return raw.split('&').filter { it.isNotEmpty() }.associate { part ->
            val idx = part.indexOf('=')
            if (idx < 0) decode(part) to "" else decode(part.substring(0, idx)) to decode(part.substring(idx + 1))
        }
    }

    private fun decode(s: String): String = URLDecoder.decode(s.replace("+", "%2B"), "UTF-8")
}
