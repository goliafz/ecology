package ru.khodakovsky.wltester.core

import java.net.URI

/** Цель HTTPS-проверки: "host", "host:port" или "https://host/path". */
data class Target(val host: String, val port: Int = 443, val path: String = "/", val label: String = host) {
    companion object {
        fun parse(entry: String): Target? {
            val s = entry.substringBefore('#').trim()
            if (s.isEmpty()) return null
            return try {
                if (s.contains("://")) {
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
        # Российские хостинги/облака — пригодится для идеи с промежуточным сервером
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

    /** Эти сайты проверяются только через VPN-туннель. */
    val VPN_SITES = """
        www.youtube.com
        telegram.org
        web.telegram.org
        www.whatsapp.com
        www.instagram.com
        google.com
        chatgpt.com
    """.trimIndent()

    const val HEALTH_URL = "https://www.google.com/generate_204"
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
