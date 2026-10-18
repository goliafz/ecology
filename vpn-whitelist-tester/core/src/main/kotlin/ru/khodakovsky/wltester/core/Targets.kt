package ru.khodakovsky.wltester.core

import java.net.URI

/**
 * Цель проверки: "host", "host:port", "https://host/path", "tcp://host:port" (только подключение)
 * или "mtproto://ip:port" (сервер Telegram: отправляем запрос протокола Telegram и ждём ответа).
 * Комментарий в конце строки ("mtproto://1.2.3.4:443 # Telegram, сервер 2") становится подписью в журнале.
 */
data class Target(
    val host: String,
    val port: Int = 443,
    val path: String = "/",
    val label: String = host,
    val tcpOnly: Boolean = false,
    val mtproto: Boolean = false,
) {
    companion object {
        fun parse(entry: String): Target? {
            val s = entry.substringBefore('#').trim()
            if (s.isEmpty()) return null
            val comment = entry.substringAfter('#', "").trim()
            val t = try {
                val scheme = s.substringBefore("://", "").lowercase()
                if (scheme == "tcp" || scheme == "mtproto") {
                    val hp = s.substringAfter("://").substringBefore('/')
                    val host = hp.substringBeforeLast(':')
                    val port = hp.substringAfterLast(':', "").toIntOrNull() ?: return null
                    if (host.isBlank()) null
                    else Target(host, port, "/", "$host:$port", tcpOnly = scheme == "tcp", mtproto = scheme == "mtproto")
                } else if (s.contains("://")) {
                    val u = URI(s)
                    val host = u.host ?: return null
                    val port = if (u.port > 0) u.port else 443
                    val path = (u.rawPath?.ifEmpty { "/" } ?: "/") + (u.rawQuery?.let { "?$it" } ?: "")
                    Target(host, port, path, s)
                } else {
                    val host = s.substringBefore('/').substringBefore(':')
                    val port = s.substringBefore('/').substringAfter(':', "443").toIntOrNull() ?: 443
                    if (host.isBlank()) null else Target(host, port, "/", if (port == 443) host else "$host:$port")
                }
            } catch (e: Exception) {
                null
            }
            return if (t != null && comment.isNotEmpty()) t.copy(label = "$comment (${t.label})") else t
        }

        fun parseList(text: String): List<Target> =
            text.lineSequence().mapNotNull { parse(it) }.distinctBy { it.label }.toList()
    }
}

object Defaults {

    val RU_SITES = """
        yandex.ru
        ya.ru
        vk.ru
        vk.com
        ok.ru
        gosuslugi.ru
        ozon.ru
        mail.ru
        sberbank.ru
        tbank.ru
        avito.ru
        wildberries.ru
        rutube.ru
        dzen.ru
        max.ru
        mos.ru
        nalog.gov.ru
        2gis.ru
        kinopoisk.ru
        rbc.ru
        # Российские хостинги: если они открываются при белых списках, у них можно взять сервер-посредник
        firstbyte.ru
        selectel.ru
        timeweb.cloud
        cloud.yandex.ru
    """.trimIndent()

    val FOREIGN_SITES = """
        google.ru
        google.com
        wikipedia.org
        github.com
        cloudflare.com
        1.1.1.1
        apple.com
        microsoft.com
        amazon.com
    """.trimIndent()

    val HOSTING = setOf("firstbyte.ru", "selectel.ru", "timeweb.cloud", "cloud.yandex.ru", "cloud.vk.com", "reg.ru")

    /** До этого сайта меряем пинг, разброс и потери: показывает качество самой мобильной связи. */
    const val QUALITY_PING_HOST = "ya.ru"

    /**
     * Проверяются только через VPN: без него у вас они не работают.
     * Для Telegram, кроме сайта, проверяются его серверы: само приложение ходит на них, а не на сайт.
     */
    val VPN_SITES = """
        www.youtube.com
        telegram.org # Telegram, сайт
        web.telegram.org # Telegram, веб-версия
        mtproto://149.154.175.50:443 # Telegram, сервер 1
        mtproto://149.154.167.51:443 # Telegram, сервер 2
        mtproto://149.154.175.100:443 # Telegram, сервер 3
        mtproto://149.154.167.91:443 # Telegram, сервер 4
        mtproto://91.108.56.130:443 # Telegram, сервер 5
        www.whatsapp.com # WhatsApp, сайт
        web.whatsapp.com # WhatsApp, веб-версия
        www.instagram.com
        google.com
        chatgpt.com
    """.trimIndent()

    /** Список из версий до 1.0.6: если пользователь его не менял, заменяем на новый. */
    val VPN_SITES_V1 = """
        www.youtube.com
        telegram.org
        web.telegram.org
        www.whatsapp.com
        www.instagram.com
        google.com
        chatgpt.com
    """.trimIndent()

    /**
     * «Туннель пропускает трафик» — достаточно ответа хотя бы одного адреса.
     * Google не первый: через некоторые связки Vision/версий ядра именно сайты Google ломаются отдельно от остального.
     */
    val HEALTH_URLS = listOf(
        "https://cp.cloudflare.com/generate_204",
        "https://www.google.com/generate_204",
        "https://www.apple.com/library/test/success.html",
    )

    /** Этим именем проверяем, готова ли сеть (DNS отвечает), перед началом цикла. */
    const val WARMUP_HOST = "ya.ru"
    const val DOWNLOAD_RU_URL = "https://yastatic.net/jquery/3.3.1/jquery.min.js"
    const val DOWNLOAD_FOREIGN_URL = "https://speed.cloudflare.com/__down?bytes=262144"

    val IP_DIRECT_URLS = listOf("https://yandex.ru/internet/api/v0/ip", "https://api.ipify.org/")
    val IP_VPN_URLS = listOf("https://api.ipify.org/", "https://ifconfig.me/ip")

    /** UDP-DNS: Google, Cloudflare, Яндекс. */
    val DNS_SERVERS = listOf("8.8.8.8", "1.1.1.1", "77.88.8.8")
    const val DNS_TEST_NAME = "google.com"

    /** Дополнительные SNI, которыми «простукиваются» порты VPN-сервера (кроме SNI из профилей). */
    val EXTRA_SERVER_SNI = listOf("ya.ru")
}
