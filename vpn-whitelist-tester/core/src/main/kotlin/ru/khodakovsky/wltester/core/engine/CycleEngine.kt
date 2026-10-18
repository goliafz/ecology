package ru.khodakovsky.wltester.core.engine

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Defaults
import ru.khodakovsky.wltester.core.NetworkSnapshot
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.Target
import ru.khodakovsky.wltester.core.Trigger
import ru.khodakovsky.wltester.core.VlessProfile
import ru.khodakovsky.wltester.core.analysis.Analyzer
import ru.khodakovsky.wltester.core.probe.DnsUdp
import ru.khodakovsky.wltester.core.probe.ErrorMapper
import ru.khodakovsky.wltester.core.probe.HttpsProbe
import ru.khodakovsky.wltester.core.probe.NetPlatform
import ru.khodakovsky.wltester.core.probe.PingParser
import java.net.Inet4Address
import java.net.InetAddress

interface EnginePlatform : NetPlatform {
    fun network(): NetworkSnapshot

    /** ICMP-пинг; null, если ping недоступен на устройстве. */
    fun ping(ip: String, count: Int = 3): PingParser.Stats? = null
}

/** Запуск локального xray с SOCKS-входом на каждый профиль. */
interface TunnelFactory {
    fun start(profiles: List<VlessProfile>, addresses: Map<Int, String>): TunnelSession
}

interface TunnelSession : AutoCloseable {
    val socksPorts: List<Int>
    fun diagnostics(): String?
}

data class EngineConfig(
    val ruTargets: List<Target> = Target.parseList(Defaults.RU_SITES),
    val foreignTargets: List<Target> = Target.parseList(Defaults.FOREIGN_SITES),
    val vpnTargets: List<Target> = Target.parseList(Defaults.VPN_SITES),
    /** Пинг, разброс и потери до российского сайта: качество самой мобильной связи. */
    val quality: Boolean = true,
    val qualityHost: String = Defaults.QUALITY_PING_HOST,
    val profiles: List<VlessProfile> = emptyList(),
    val connectTimeoutMs: Int = 6000,
    val readTimeoutMs: Int = 8000,
    val requireCellular: Boolean = true,
    val downloadEnabled: Boolean = true,
    val downloadBytes: Long = 256 * 1024,
    val downloadRuUrl: String = Defaults.DOWNLOAD_RU_URL,
    val downloadForeignUrl: String = Defaults.DOWNLOAD_FOREIGN_URL,
    val sniMatrix: Boolean = true,
    val ping: Boolean = true,
    val dnsUdp: Boolean = true,
    val parallelism: Int = 6,
    val healthUrls: List<String> = Defaults.HEALTH_URLS,
    val warmupHost: String = Defaults.WARMUP_HOST,
    /** Сколько первых сайтов из каждого списка проверять в быстром цикле. */
    val quickSites: Int = 2,
)

class CycleEngine(
    private val platform: EnginePlatform,
    /** null — ядро xray недоступно, туннельные проверки пропускаются. */
    private val tunnels: TunnelFactory?,
    private val clock: () -> Long = System::currentTimeMillis,
    private val sslFactory: javax.net.ssl.SSLSocketFactory? = null,
) {
    suspend fun run(kind: CycleKind, trigger: Trigger, cfg: EngineConfig): CycleRecord = coroutineScope {
        val started = clock()
        val id = "c$started"
        val net = withContext(Dispatchers.IO) { platform.network() }
        val notes = mutableListOf<String>()

        if (cfg.requireCellular && !net.isCellular) {
            val why = if (net.transport == "NONE") {
                "Нет интернета: мобильные данные выключены или нет сигнала. Проверка пропущена."
            } else {
                "Телефон подключён к Wi-Fi, а проверять нужно мобильный интернет. Выключите Wi-Fi."
            }
            return@coroutineScope CycleRecord(
                id = id, startedAt = started, finishedAt = clock(), kind = CycleKind.SKIPPED, trigger = trigger,
                network = net, notes = listOf(why),
            )
        }
        if (net.ourTrafficViaVpn) {
            notes += "Внимание: приложение ходит в интернет через VPN, поэтому проверки без VPN неточные. Уберите WL Tester из списка приложений, которые идут через VPN в NekoBox."
        }

        val probe = HttpsProbe(platform, cfg.connectTimeoutMs, cfg.readTimeoutMs, sslFactory)
        waitForDns(probe, cfg)?.let { notes += it }
        val sem = Semaphore(cfg.parallelism.coerceIn(1, 16))
        suspend fun <T> io(block: () -> T): T = sem.io(block)
        val full = kind == CycleKind.FULL

        // 1. Адреса VPN-серверов (нужны и для прямых проверок порта, и для xray).
        val serverHosts = cfg.profiles.map { it.host }.distinct()
        val resolved = serverHosts.map { host ->
            async { host to io { resolveServer(probe, host) } }
        }.awaitAll().toMap()
        val serverIp: Map<String, InetAddress?> = resolved.mapValues { it.value.second }

        // 2. Прямые проверки.
        val direct = mutableListOf<kotlinx.coroutines.Deferred<List<CheckResult>>>()
        val ru = if (full) cfg.ruTargets else cfg.ruTargets.take(cfg.quickSites)
        val foreign = if (full) cfg.foreignTargets else cfg.foreignTargets.take(cfg.quickSites)
        ru.forEach { t -> direct += async { listOf(io { probe.run(siteRequest(Category.RU, t)).result }) } }
        foreign.forEach { t -> direct += async { listOf(io { probe.run(siteRequest(Category.FOREIGN, t)).result }) } }
        if (full && cfg.quality && cfg.qualityHost.isNotBlank()) {
            direct += async { listOf(io { qualityCheck(probe, cfg.qualityHost) }) }
        }

        var publicIpDirect: String? = null
        if (full) {
            if (cfg.dnsUdp) Defaults.DNS_SERVERS.forEach { s ->
                direct += async { listOf(io { DnsUdp.query(s, Defaults.DNS_TEST_NAME, cfg.connectTimeoutMs) }) }
            }
            direct += async {
                val (check, ip) = io { publicIp(probe, Defaults.IP_DIRECT_URLS, CheckResult.DIRECT, null, "Ваш IP-адрес без VPN") }
                publicIpDirect = ip
                listOf(check)
            }
            if (cfg.downloadEnabled) {
                listOf(cfg.downloadRuUrl to "с российского сайта", cfg.downloadForeignUrl to "с зарубежного сайта").forEach { (url, tag) ->
                    val t = Target.parse(url) ?: return@forEach
                    direct += async {
                        listOf(io { probe.run(downloadRequest(t, "Скачивание $tag", CheckResult.DIRECT, null, cfg)).result })
                    }
                }
            }
        }

        // 3. Сервер VPN напрямую: DNS, TCP к портам, TLS с разными SNI, ping.
        val server = mutableListOf<kotlinx.coroutines.Deferred<List<CheckResult>>>()
        resolved.values.forEach { server += async { listOf(it.first) } }
        val endpoints = cfg.profiles.map { it.host to it.port }.distinct()
        val allSni = (cfg.profiles.map { it.effectiveSni } + Defaults.EXTRA_SERVER_SNI).distinct()
        endpoints.forEach { (host, port) ->
            val ip = serverIp[host]
            server += async {
                listOf(io { serverCheck(probe, host, port, ip, null) })
            }
            if (full) {
                val snis = if (cfg.sniMatrix) allSni else cfg.profiles.filter { it.host == host && it.port == port }.map { it.effectiveSni }.distinct()
                snis.forEach { sni -> server += async { listOf(io { serverCheck(probe, host, port, ip, sni) }) } }
            }
        }
        if (full && cfg.ping) {
            serverIp.forEach { (host, ip) ->
                if (ip != null) server += async { listOf(io { pingCheck(host, ip) }) }
            }
        }

        // 4. Туннель.
        val publicIpVpn = mutableMapOf<String, String>()
        val tunnel = async { tunnelChecks(probe, cfg, full, serverIp, notes, publicIpVpn, sem) }

        val checks = direct.awaitAll().flatten() + server.awaitAll().flatten() + tunnel.await()
        val summary = Analyzer.summarize(checks)
        CycleRecord(
            id = id,
            startedAt = started,
            finishedAt = clock(),
            kind = kind,
            trigger = trigger,
            network = net,
            publicIpDirect = publicIpDirect,
            publicIpVpn = publicIpVpn.toMap(),
            checks = checks,
            summary = summary,
            notes = notes.toList(),
        )
    }

    private suspend fun tunnelChecks(
        probe: HttpsProbe,
        cfg: EngineConfig,
        full: Boolean,
        serverIp: Map<String, InetAddress?>,
        notes: MutableList<String>,
        publicIpVpn: MutableMap<String, String>,
        sem: Semaphore,
    ): List<CheckResult> = coroutineScope {
        suspend fun <T> io(block: () -> T): T = sem.io(block)
        if (cfg.profiles.isEmpty()) return@coroutineScope emptyList()
        val factory = tunnels ?: return@coroutineScope cfg.profiles.map {
            CheckResult(Category.VPN, HEALTH_LABEL, it.label, ProbeStatus.SKIPPED, error = "в этой версии приложения нет ядра для проверки VPN")
        }
        val addresses = cfg.profiles.withIndex().mapNotNull { (i, p) -> serverIp[p.host]?.hostAddress?.let { i to it } }.toMap()
        val session = try {
            withContext(Dispatchers.IO) { factory.start(cfg.profiles, addresses) }
        } catch (e: Exception) {
            notes += "Не получилось запустить проверку VPN: ${e.message}"
            return@coroutineScope cfg.profiles.map {
                CheckResult(Category.VPN, HEALTH_LABEL, it.label, ProbeStatus.ERROR, error = "проверка VPN не запустилась: ${e.message}")
            }
        }
        try {
            val perProfile = cfg.profiles.mapIndexed { i, p ->
                async {
                    val port = session.socksPorts[i]
                    val out = mutableListOf<CheckResult>()
                    out += healthCheck(probe, cfg, p.label, port, sem)
                    if (full) {
                        val sub = mutableListOf<kotlinx.coroutines.Deferred<List<CheckResult>>>()
                        sub += async {
                            io {
                                val (check, ip) = publicIp(probe, Defaults.IP_VPN_URLS, p.label, port, "Ваш IP-адрес через VPN")
                                if (ip != null) synchronized(publicIpVpn) { publicIpVpn[p.label] = ip }
                                listOf(check)
                            }
                        }
                        cfg.vpnTargets.forEach { t ->
                            sub += async { io { listOf(probe.run(siteRequest(Category.VPN, t, p.label, port)).result) } }
                        }
                        if (cfg.downloadEnabled) Target.parse(cfg.downloadForeignUrl)?.let { t ->
                            sub += async { io { listOf(probe.run(downloadRequest(t, "Скачивание через VPN", p.label, port, cfg)).result) } }
                        }
                        out += sub.awaitAll().flatten()
                    }
                    out
                }
            }.awaitAll().flatten()
            if (perProfile.any { it.target == HEALTH_LABEL && !it.ok }) {
                session.diagnostics()?.takeIf { it.isNotBlank() }?.let { notes += "Технический журнал проверки VPN: $it" }
            }
            perProfile
        } finally {
            withContext(Dispatchers.IO) { runCatching { session.close() } }
        }
    }

    private suspend fun healthCheck(
        probe: HttpsProbe,
        cfg: EngineConfig,
        label: String,
        port: Int,
        sem: Semaphore,
    ): CheckResult {
        val failed = mutableListOf<String>()
        var last: CheckResult? = null
        for (url in cfg.healthUrls) {
            val t = Target.parse(url) ?: continue
            val req = HttpsProbe.Request(Category.VPN, t, label, socksPort = port, maxBytes = 4096, label = HEALTH_LABEL)
            val r = sem.io { probe.run(req).result }
            if (r.ok) {
                val info = "ответил ${t.host}" + if (failed.isNotEmpty()) "; не ответили: ${failed.joinToString("; ")}" else ""
                return r.copy(info = info)
            }
            failed += "${t.host} (${r.status.ru})"
            last = r
            delay(300)
        }
        return (last ?: CheckResult(Category.VPN, HEALTH_LABEL, label, ProbeStatus.SKIPPED))
            .copy(info = "не ответил ни один адрес: ${failed.joinToString("; ")}")
    }

    /**
     * Сразу после переключения сети DNS какое-то время не отвечает, и весь цикл выглядит как «ничего не работает».
     * Ждём до ~15 с, пока разрешится контрольное имя.
     */
    private suspend fun waitForDns(probe: HttpsProbe, cfg: EngineConfig): String? {
        if (cfg.warmupHost.isBlank()) return null
        val t0 = clock()
        repeat(4) { attempt ->
            val ok = withContext(Dispatchers.IO) { runCatching { probe.resolveWithTimeout(cfg.warmupHost) }.isSuccess }
            if (ok) {
                return if (attempt == 0) null else "Интернет включился не сразу: ждали ${(clock() - t0) / 1000} с"
            }
            delay(2000)
        }
        return "Интернет не отвечал ${(clock() - t0) / 1000} с перед проверкой (не нашёлся адрес ${cfg.warmupHost})"
    }

    private suspend fun <T> Semaphore.io(block: () -> T): T = withPermit { withContext(Dispatchers.IO) { block() } }

    private fun resolveServer(probe: HttpsProbe, host: String): Pair<CheckResult, InetAddress?> {
        val t0 = System.nanoTime()
        return try {
            val list = probe.resolveWithTimeout(host)
            val ip = list.firstOrNull { it is Inet4Address } ?: list.first()
            CheckResult(
                Category.SERVER, "DNS $host", status = ProbeStatus.OK, ip = ip.hostAddress,
                dnsMs = HttpsProbe.msSince(t0), totalMs = HttpsProbe.msSince(t0),
                info = list.joinToString(", ") { it.hostAddress ?: "?" },
            ) to ip
        } catch (e: Exception) {
            CheckResult(
                Category.SERVER, "DNS $host", status = ProbeStatus.DNS_FAIL,
                totalMs = HttpsProbe.msSince(t0), error = ErrorMapper.describe(e),
            ) to null
        }
    }

    private fun serverCheck(probe: HttpsProbe, host: String, port: Int, ip: InetAddress?, sni: String?): CheckResult {
        val label = if (sni == null) "TCP $host:$port" else "TLS $host:$port SNI=$sni"
        val req = HttpsProbe.Request(
            Category.SERVER, Target(host, port), connectIp = ip, sni = sni,
            tls = sni != null, http = false, label = label,
        )
        return probe.run(req).result
    }

    private fun siteRequest(category: Category, t: Target, route: String = CheckResult.DIRECT, socksPort: Int? = null) =
        if (t.tcpOnly || t.mtproto) {
            HttpsProbe.Request(category, t, route, socksPort = socksPort, tls = false, http = false, mtproto = t.mtproto)
        } else {
            HttpsProbe.Request(category, t, route, socksPort = socksPort, maxBytes = SITE_BYTES)
        }

    private fun qualityCheck(probe: HttpsProbe, host: String): CheckResult {
        val t0 = System.nanoTime()
        val label = "Пинг до $host"
        val ip = try {
            probe.resolveWithTimeout(host).let { list -> list.firstOrNull { it is Inet4Address } ?: list.first() }
        } catch (e: Exception) {
            return CheckResult(Category.QUALITY, label, status = ProbeStatus.DNS_FAIL, totalMs = HttpsProbe.msSince(t0), error = ErrorMapper.describe(e))
        }
        val addr = ip.hostAddress ?: host
        val st = runCatching { platform.ping(addr, QUALITY_PINGS) }.getOrNull()
            ?: return CheckResult(Category.QUALITY, label, status = ProbeStatus.SKIPPED, ip = addr, totalMs = HttpsProbe.msSince(t0), error = "на телефоне нет команды ping")
        val info = buildString {
            st.avgMs?.let { append("в среднем ${it.toLong()} мс") }
            st.mdevMs?.let { append(", разброс ${it.toLong()} мс") }
            append(if (isEmpty()) "" else ", ").append("потерь ${st.lossPct}% (${st.sent - st.received} из ${st.sent})")
        }
        return CheckResult(
            Category.QUALITY, label,
            status = if (st.received > 0) ProbeStatus.OK else ProbeStatus.TCP_TIMEOUT,
            ip = addr, ttfbMs = st.avgMs?.toLong(), totalMs = HttpsProbe.msSince(t0), info = info,
        )
    }

    private fun pingCheck(host: String, ip: InetAddress): CheckResult {
        val t0 = System.nanoTime()
        val addr = ip.hostAddress ?: host
        val stats = runCatching { platform.ping(addr) }.getOrNull()
        val total = HttpsProbe.msSince(t0)
        return when {
            stats == null -> CheckResult(Category.SERVER, "PING $host", status = ProbeStatus.SKIPPED, ip = addr, totalMs = total, error = "на телефоне нет команды ping")
            stats.received > 0 -> CheckResult(
                Category.SERVER, "PING $host", status = ProbeStatus.OK, ip = addr, totalMs = total,
                ttfbMs = stats.avgMs?.toLong(), info = "ответов ${stats.received} из ${stats.sent}, в среднем ${stats.avgMs?.toLong() ?: "?"} мс",
            )
            else -> CheckResult(Category.SERVER, "PING $host", status = ProbeStatus.TCP_TIMEOUT, ip = addr, totalMs = total, info = "ответов 0 из ${stats.sent}")
        }
    }

    private fun publicIp(probe: HttpsProbe, urls: List<String>, route: String, socksPort: Int?, label: String): Pair<CheckResult, String?> {
        var last: CheckResult? = null
        for (u in urls) {
            val t = Target.parse(u) ?: continue
            val o = probe.run(HttpsProbe.Request(Category.INFO, t, route, socksPort = socksPort, maxBytes = 8192, captureBody = 512, label = label))
            val ip = o.body?.let { IP_RE.find(it)?.value }
            if (o.result.ok && ip != null) return o.result.copy(info = ip, target = label) to ip
            last = o.result
        }
        return (last ?: CheckResult(Category.INFO, label, route, ProbeStatus.SKIPPED)) to null
    }

    private fun downloadRequest(t: Target, label: String, route: String, socksPort: Int?, cfg: EngineConfig) =
        HttpsProbe.Request(
            if (route == CheckResult.DIRECT) Category.DOWNLOAD else Category.VPN, t, route, socksPort = socksPort,
            maxBytes = cfg.downloadBytes + 2048, download = true, deadlineMs = 30_000, label = label,
        )

    companion object {
        // Имя не меняем: по нему находятся записи в старых журналах.
        const val HEALTH_LABEL = "VPN-туннель (generate_204)"

        /** Для обычных сайтов достаточно ответа сервера — тело читаем немного, чтобы не тратить трафик. */
        const val SITE_BYTES = 8L * 1024

        /** Пакетов для замера качества связи: по 10 видно и разброс, и потери. */
        const val QUALITY_PINGS = 10
        private val IP_RE = Regex("\\b\\d{1,3}(?:\\.\\d{1,3}){3}\\b|\\b[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{0,4}){2,7}\\b")
    }
}
