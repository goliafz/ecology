package ru.khodakovsky.wltester.core

import kotlinx.coroutines.runBlocking
import ru.khodakovsky.wltester.core.analysis.EventDetector
import ru.khodakovsky.wltester.core.analysis.MonitorMemory
import ru.khodakovsky.wltester.core.analysis.ReportBuilder
import ru.khodakovsky.wltester.core.engine.CycleEngine
import ru.khodakovsky.wltester.core.engine.EngineConfig
import ru.khodakovsky.wltester.core.engine.EnginePlatform
import ru.khodakovsky.wltester.core.engine.TunnelFactory
import ru.khodakovsky.wltester.core.engine.TunnelSession
import ru.khodakovsky.wltester.core.probe.HttpsProbe
import ru.khodakovsky.wltester.core.probe.JvmPlatform
import java.io.DataInputStream
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.file.Files
import java.security.KeyStore
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.TrustManagerFactory
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** Локальные TLS-сервер и SOCKS5-прокси, чтобы проверить пробы целиком без интернета. */
class ProbeTest {

    companion object {
        private val ctx: SSLContext by lazy { makeContext() }
        val tlsPort: Int by lazy { startTlsServer() }
        val socksPort: Int by lazy { startSocks() }

        private fun makeContext(): SSLContext {
            val dir = Files.createTempDirectory("wlks").toFile()
            val ks = File(dir, "ks.p12")
            val keytool = File(System.getProperty("java.home"), "bin/keytool").path
            val p = ProcessBuilder(
                keytool, "-genkeypair", "-alias", "t", "-keyalg", "RSA", "-keysize", "2048", "-validity", "2",
                "-dname", "CN=localhost", "-ext", "SAN=dns:localhost,ip:127.0.0.1",
                "-keystore", ks.path, "-storetype", "PKCS12", "-storepass", "secret1", "-keypass", "secret1",
            ).redirectErrorStream(true).start()
            val out = p.inputStream.bufferedReader().readText()
            check(p.waitFor() == 0) { out }
            val store = KeyStore.getInstance("PKCS12").apply { ks.inputStream().use { load(it, "secret1".toCharArray()) } }
            val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, "secret1".toCharArray()) }
            val tmf = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(store) }
            return SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, tmf.trustManagers, null) }
        }

        private fun startTlsServer(): Int {
            val ss = ctx.serverSocketFactory.createServerSocket(0, 50, InetAddress.getByName("127.0.0.1")) as SSLServerSocket
            thread(isDaemon = true) {
                while (true) {
                    val s = ss.accept()
                    thread(isDaemon = true) { runCatching { serve(s) }; runCatching { s.close() } }
                }
            }
            return ss.localPort
        }

        private fun serve(s: Socket) {
            val req = readHead(s.getInputStream())
            val path = req.lineSequence().first().split(' ')[1]
            val out = s.getOutputStream()
            when (path) {
                "/204" -> out.write("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n".toByteArray())
                "/ip" -> out.write("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7".toByteArray())
                "/stall" -> {
                    out.write("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\n".toByteArray())
                    out.write(ByteArray(20 * 1024))
                    out.flush()
                    Thread.sleep(4000)
                }
                "/big" -> {
                    out.write("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\n".toByteArray())
                    out.write(ByteArray(100_000))
                }
                else -> out.write("HTTP/1.1 301 Moved\r\nLocation: /x\r\nContent-Length: 0\r\n\r\n".toByteArray())
            }
            out.flush()
        }

        private fun readHead(i: InputStream): String {
            val sb = StringBuilder()
            while (!sb.endsWith("\r\n\r\n")) {
                val b = i.read()
                if (b < 0) break
                sb.append(b.toChar())
            }
            return sb.toString()
        }

        /** SOCKS5, который все домены отправляет на 127.0.0.1, кроме "blocked.test" — его отклоняет. */
        private fun startSocks(): Int {
            val ss = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
            thread(isDaemon = true) {
                while (true) {
                    val c = ss.accept()
                    thread(isDaemon = true) { runCatching { socks(c) }; runCatching { c.close() } }
                }
            }
            return ss.localPort
        }

        private fun socks(c: Socket) {
            val i = DataInputStream(c.getInputStream())
            val o = c.getOutputStream()
            i.readUnsignedByte(); val n = i.readUnsignedByte(); i.readFully(ByteArray(n))
            o.write(byteArrayOf(5, 0))
            i.readUnsignedByte(); i.readUnsignedByte(); i.readUnsignedByte()
            check(i.readUnsignedByte() == 3)
            val host = String(ByteArray(i.readUnsignedByte()).also { i.readFully(it) })
            val port = i.readUnsignedShort()
            if (host == "blocked.test") {
                o.write(byteArrayOf(5, 5, 0, 1, 0, 0, 0, 0, 0, 0)); return
            }
            val up = Socket("127.0.0.1", port)
            o.write(byteArrayOf(5, 0, 0, 1, 127, 0, 0, 1, 0, 0))
            val t = thread(isDaemon = true) { pipe(up.getInputStream(), o) }
            pipe(i, up.getOutputStream())
            t.join(5000)
            up.close()
        }

        private fun pipe(i: InputStream, o: OutputStream) {
            runCatching { i.copyTo(o) }
            runCatching { o.flush() }
        }
    }

    private val localhost = InetAddress.getByName("127.0.0.1")
    private fun probe(readTimeout: Int = 1500) = HttpsProbe(JvmPlatform, 2000, readTimeout, ctx.socketFactory)

    @Test
    fun httpsOkWithStages() {
        val r = probe().run(HttpsProbe.Request(Category.RU, Target("localhost", tlsPort), connectIp = localhost)).result
        assertEquals(ProbeStatus.OK, r.status, r.error)
        assertEquals(301, r.httpCode)
        assertTrue(r.tcpMs != null && r.tlsMs != null && r.ttfbMs != null)
    }

    @Test
    fun certMismatchStillReachable() {
        val r = probe().run(HttpsProbe.Request(Category.SERVER, Target("localhost", tlsPort), connectIp = localhost, sni = "ya.ru", http = false)).result
        assertEquals(ProbeStatus.CERT_ERROR, r.status)
        assertTrue(r.ok)
    }

    @Test
    fun untrustedCertIsCertError() {
        val r = HttpsProbe(JvmPlatform, 2000, 1500).run(HttpsProbe.Request(Category.RU, Target("localhost", tlsPort), connectIp = localhost)).result
        assertEquals(ProbeStatus.CERT_ERROR, r.status, r.error)
    }

    @Test
    fun refusedPort() {
        val free = ServerSocket(0).use { it.localPort }
        val r = probe().run(HttpsProbe.Request(Category.FOREIGN, Target("localhost", free), connectIp = localhost)).result
        assertEquals(ProbeStatus.TCP_REFUSED, r.status, r.error)
    }

    @Test
    fun downloadStallDetected() {
        val r = probe(1200).run(
            HttpsProbe.Request(Category.DOWNLOAD, Target("localhost", tlsPort, "/stall"), connectIp = localhost, download = true, maxBytes = 300_000)
        ).result
        assertEquals(ProbeStatus.STALLED, r.status, r.error)
        assertEquals(20L * 1024, r.bytes)
    }

    @Test
    fun downloadCompletes() {
        val r = probe().run(
            HttpsProbe.Request(Category.DOWNLOAD, Target("localhost", tlsPort, "/big"), connectIp = localhost, download = true, maxBytes = 300_000)
        ).result
        assertEquals(ProbeStatus.OK, r.status, r.error)
        assertEquals(100_000L, r.bytes)
    }

    @Test
    fun viaSocks() {
        val ok = probe().run(HttpsProbe.Request(Category.VPN, Target("localhost", tlsPort, "/204"), "p", socksPort = socksPort)).result
        assertEquals(ProbeStatus.OK, ok.status, ok.error)
        assertEquals(204, ok.httpCode)
        val bad = probe().run(HttpsProbe.Request(Category.VPN, Target("blocked.test", tlsPort), "p", socksPort = socksPort)).result
        assertEquals(ProbeStatus.PROXY_ERROR, bad.status)
    }

    @Test
    fun healthFallsBackToNextUrlAndCorruptionIsReported() = runBlocking {
        val free = ServerSocket(0).use { it.localPort }
        val platform = object : EnginePlatform {
            override fun resolve(host: String) = listOf(localhost)
            override fun network() = NetworkSnapshot(transport = "CELLULAR")
        }
        val tunnels = object : TunnelFactory {
            override fun start(profiles: List<VlessProfile>, addresses: Map<Int, String>) = object : TunnelSession {
                override val socksPorts = profiles.map { socksPort }
                override fun diagnostics() = null
                override fun close() {}
            }
        }
        val cfg = EngineConfig(
            ruTargets = emptyList(), foreignTargets = emptyList(), vpnTargets = emptyList(),
            profiles = listOf(VlessProfile("P", "u", "localhost", tlsPort, sni = "localhost", publicKey = "k")),
            connectTimeoutMs = 2000, readTimeoutMs = 1500, downloadEnabled = false, dnsUdp = false, ping = false, warmupHost = "",
            healthUrls = listOf("https://blocked.test/x", "https://localhost:$tlsPort/204"),
        )
        val c = CycleEngine(platform, tunnels, sslFactory = ctx.socketFactory).run(CycleKind.QUICK, Trigger.WATCH, cfg)
        assertEquals(mapOf("P" to true), c.summary.vpn)
        val health = c.checks.first { it.target == CycleEngine.HEALTH_LABEL }
        assertTrue(health.info!!.contains("blocked.test"), health.info)

        val corrupt = c.copy(
            kind = CycleKind.FULL,
            checks = c.checks + CheckResult(Category.VPN, "www.youtube.com", "P", ProbeStatus.DATA_CORRUPT),
        )
        val report = ReportBuilder().build(listOf(corrupt), emptyList())
        assertTrue(report.contains("BAD_DECRYPT") && report.contains("www.youtube.com"), report)
    }

    @Test
    fun fullCycleDetectsWhitelistAndReports() = runBlocking {
        val free = ServerSocket(0).use { it.localPort }
        val platform = object : EnginePlatform {
            override fun resolve(host: String) = listOf(localhost)
            override fun network() = NetworkSnapshot(transport = "CELLULAR", operatorName = "Test MTS", networkType = "LTE")
        }
        val tunnels = object : TunnelFactory {
            override fun start(profiles: List<VlessProfile>, addresses: Map<Int, String>) = object : TunnelSession {
                override val socksPorts = profiles.map { socksPort }
                override fun diagnostics() = "test"
                override fun close() {}
            }
        }
        val profile = VlessProfile("Основной", "u", "localhost", tlsPort, sni = "localhost", publicKey = "k")
        val cfg = EngineConfig(
            ruTargets = listOf(Target("localhost", tlsPort, "/", "ru-site")),
            foreignTargets = listOf(Target("localhost", free, "/", "foreign-site")),
            vpnTargets = listOf(Target("localhost", tlsPort, "/", "yt")),
            profiles = listOf(profile),
            connectTimeoutMs = 2000, readTimeoutMs = 1500,
            downloadEnabled = false, dnsUdp = false, ping = false,
            healthUrls = listOf("https://localhost:$tlsPort/204"),
            warmupHost = "",
        )
        var now = 1_700_000_000_000L
        val engine = CycleEngine(platform, tunnels, clock = { now }, sslFactory = ctx.socketFactory)
        val c1 = engine.run(CycleKind.FULL, Trigger.MANUAL, cfg)
        assertEquals(Verdict.WHITELIST, c1.summary.verdict)
        assertEquals(mapOf("Основной" to true), c1.summary.vpn)
        assertTrue(c1.checks.any { it.category == Category.SERVER && it.target.startsWith("TLS ") })

        val skippedCfg = cfg.copy()
        val wifi = CycleEngine(object : EnginePlatform {
            override fun network() = NetworkSnapshot(transport = "WIFI")
        }, null).run(CycleKind.FULL, Trigger.WINDOW, skippedCfg)
        assertEquals(CycleKind.SKIPPED, wifi.kind)

        val (ev, mem) = EventDetector.detect(MonitorMemory(), c1) { it.toString() }
        assertEquals(1, ev.size)
        now += 300_000
        val c2 = engine.run(CycleKind.FULL, Trigger.MANUAL, cfg.copy(foreignTargets = cfg.ruTargets))
        assertEquals(Verdict.NORMAL, c2.summary.verdict)
        val (ev2, _) = EventDetector.detect(mem, c2) { it.toString() }
        assertTrue(ev2.any { it.title.startsWith("Белые списки отключились") }, ev2.toString())

        val roundTrip = WlJson.decodeFromString(CycleRecord.serializer(), WlJson.encodeToString(CycleRecord.serializer(), c1))
        assertEquals(c1, roundTrip)

        val report = ReportBuilder().build(listOf(c1, c2), ev + ev2)
        println(report)
        assertTrue(report.contains("БЕЛЫЕ СПИСКИ"))
    }
}
