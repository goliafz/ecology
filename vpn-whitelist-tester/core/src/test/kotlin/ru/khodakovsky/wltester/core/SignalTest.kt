package ru.khodakovsky.wltester.core

import kotlinx.coroutines.runBlocking
import ru.khodakovsky.wltester.core.analysis.Analyzer
import ru.khodakovsky.wltester.core.analysis.Csv
import ru.khodakovsky.wltester.core.analysis.EventDetector
import ru.khodakovsky.wltester.core.analysis.MonitorMemory
import ru.khodakovsky.wltester.core.analysis.ReportBuilder
import ru.khodakovsky.wltester.core.engine.CycleEngine
import ru.khodakovsky.wltester.core.engine.EngineConfig
import ru.khodakovsky.wltester.core.engine.EnginePlatform
import ru.khodakovsky.wltester.core.probe.PingParser
import java.net.InetAddress
import java.net.ServerSocket
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class SignalTest {

    @Test
    fun tcpTargetsAndInlineLabels() {
        assertTrue(Target.parse("tcp://example.org:5222")!!.tcpOnly)
        val t = Target.parse("mtproto://149.154.167.51:443 # Telegram, сервер 2")!!
        assertTrue(t.mtproto)
        assertEquals("149.154.167.51", t.host)
        assertEquals(443, t.port)
        assertEquals("Telegram, сервер 2 (149.154.167.51:443)", t.label)
        assertEquals("Telegram", Analyzer.serviceOf(t.label))
        assertEquals("WhatsApp", Analyzer.serviceOf("web.whatsapp.com"))
        val all = Target.parseList(Defaults.VPN_SITES)
        assertEquals(5, all.count { it.mtproto })
        assertTrue(all.any { Analyzer.serviceOf(it.label) == "WhatsApp" })
    }

    @Test
    fun pingParserReadsJitterAndLoss() {
        val s = PingParser.parse(
            "10 packets transmitted, 8 received, 20% packet loss, time 4510ms\nrtt min/avg/max/mdev = 40.1/55.7/120.3/18.2 ms"
        )!!
        assertEquals(20, s.lossPct)
        assertEquals(55.7, s.avgMs)
        assertEquals(18.2, s.mdevMs)
    }

    @Test
    fun signalGradeUsesWorstOfLevelAndNoise() {
        assertEquals(SignalGrade.GOOD, NetworkSnapshot(signalDbm = -95).signalGrade())
        assertEquals(SignalGrade.BAD, NetworkSnapshot(signalDbm = -85, sinr = -3.0).signalGrade())
        val n = NetworkSnapshot(signalDbm = -100, sinr = 8.0, cellId = 123456L * 256 + 2, networkType = "4G LTE", timingAdvance = 13)
        assertEquals(123456L, n.enb)
        assertEquals(2, n.sector)
        assertEquals(1014, n.towerDistanceM)
        assertEquals("хороший: -100 dBm, сигнал/шум 8 дБ, вышка 123456, сектор 2", n.signalText())
    }

    @Test
    fun messengerSummaryPrefersAppServers() {
        val checks = listOf(
            CheckResult(Category.VPN, "Telegram, сайт (telegram.org)", status = ProbeStatus.OK, sni = "telegram.org"),
            CheckResult(Category.VPN, "Telegram, сервер 2 (149.154.167.51:443)", status = ProbeStatus.TCP_TIMEOUT),
            CheckResult(Category.VPN, "WhatsApp, сайт (www.whatsapp.com)", status = ProbeStatus.OK, sni = "www.whatsapp.com"),
            CheckResult(Category.VPN, CycleEngine.HEALTH_LABEL, status = ProbeStatus.OK),
            CheckResult(Category.VPN, "www.youtube.com", status = ProbeStatus.TCP_TIMEOUT, sni = "www.youtube.com"),
        )
        assertEquals(mapOf("Telegram" to false, "WhatsApp" to true), Analyzer.messengers(checks))
    }

    private fun cycle(t: Long, tgOk: Boolean, dbm: Int, sinr: Double, loss: Int): CycleRecord {
        val checks = listOf(
            CheckResult(Category.RU, "ya.ru", status = ProbeStatus.OK),
            CheckResult(Category.FOREIGN, "google.com", status = ProbeStatus.OK),
            CheckResult(Category.VPN, "Telegram, сайт (telegram.org)", status = ProbeStatus.OK, sni = "telegram.org"),
            CheckResult(Category.VPN, "Telegram, сервер 2 (149.154.167.51:443)", status = if (tgOk) ProbeStatus.OK else ProbeStatus.TCP_TIMEOUT),
            CheckResult(Category.VPN, "WhatsApp, сайт (www.whatsapp.com)", status = ProbeStatus.OK, sni = "www.whatsapp.com"),
            CheckResult(Category.VPN, CycleEngine.HEALTH_LABEL, route = "Основной", status = ProbeStatus.OK),
            CheckResult(Category.QUALITY, "Пинг до ya.ru", status = ProbeStatus.OK, ttfbMs = 50, info = "в среднем 50 мс, разброс 5 мс, потерь $loss% (0 из 10)"),
        )
        return CycleRecord(
            id = "c$t", startedAt = t, finishedAt = t + 1000, kind = CycleKind.FULL, trigger = Trigger.MANUAL,
            network = NetworkSnapshot(transport = "CELLULAR", networkType = "4G LTE", signalDbm = dbm, sinr = sinr, cellId = 1000L * 256 + 1),
            checks = checks, summary = Analyzer.summarize(checks),
        )
    }

    @Test
    fun reportBlamesTelegramNotSignal() {
        val base = 1_700_000_000_000L
        val cycles = (0 until 6).map { i -> cycle(base + i * 300_000L, tgOk = i < 2, dbm = -92, sinr = 12.0, loss = 0) }
        var mem = MonitorMemory()
        val events = mutableListOf<Event>()
        cycles.forEach { c -> EventDetector.detect(mem, c) { it.toString() }.let { (e, m) -> events += e; mem = m } }
        assertTrue(events.any { it.type == EventType.MESSENGER && it.title == "Telegram через VPN перестал работать" }, events.toString())

        val report = ReportBuilder().build(cycles, events)
        assertTrue(report.contains("не доходят именно данные Telegram"), report)
        assertTrue(report.contains("Сайт Telegram через VPN открывался, а серверы самого приложения нет"), report)
        assertTrue(report.contains("вышка 1000, сектор 1"), report)
        assertTrue(Csv().cycles(cycles).contains("Telegram=fail WhatsApp=ok"))
    }

    @Test
    fun reportBlamesSignalWhenItIsBad() {
        val base = 1_700_000_000_000L
        val cycles = (0 until 6).map { i ->
            val ok = i < 2
            cycle(base + i * 300_000L, tgOk = ok, dbm = if (ok) -92 else -118, sinr = if (ok) 12.0 else -2.0, loss = if (ok) 0 else 40)
        }
        val report = ReportBuilder().build(cycles, emptyList())
        assertTrue(report.contains("вероятная причина в связи"), report)
    }

    @Test
    fun engineMeasuresQuality() = runBlocking {
        val platform = object : EnginePlatform {
            override fun resolve(host: String) = listOf(InetAddress.getByName("127.0.0.1"))
            override fun network() = NetworkSnapshot(transport = "CELLULAR")
            override fun ping(ip: String, count: Int) = PingParser.Stats(count, count - 1, 42.0, 3.0)
        }
        val cfg = EngineConfig(
            ruTargets = emptyList(), foreignTargets = emptyList(), vpnTargets = emptyList(),
            downloadEnabled = false, dnsUdp = false, ping = false, warmupHost = "", connectTimeoutMs = 1500, readTimeoutMs = 1500,
        )
        val c = CycleEngine(platform, null).run(CycleKind.FULL, Trigger.MANUAL, cfg)
        val q = c.checks.first { it.category == Category.QUALITY }
        assertEquals(ProbeStatus.OK, q.status)
        assertTrue(q.info!!.contains("потерь 10%"), q.info)
    }

    /** Поддельный сервер Telegram: отвечает resPQ с тем же nonce, как настоящий. */
    private fun fakeTelegram(answer: Boolean): ServerSocket {
        val ss = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
        thread(isDaemon = true) {
            while (true) {
                val c = runCatching { ss.accept() }.getOrNull() ?: break
                thread(isDaemon = true) {
                    runCatching {
                        val i = java.io.DataInputStream(c.getInputStream())
                        check(i.readUnsignedByte() == 0xef)
                        val req = ByteArray(i.readUnsignedByte() * 4).also { i.readFully(it) }
                        if (!answer) { Thread.sleep(3000); return@runCatching }
                        val nonce = req.copyOfRange(24, 40)
                        val reply = java.nio.ByteBuffer.allocate(40).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                            .putLong(0).putLong(1).putInt(20).putInt(0x05162463).put(nonce).array()
                        c.getOutputStream().apply { write(byteArrayOf(10)); write(reply); flush() }
                    }
                    runCatching { c.close() }
                }
            }
        }
        return ss
    }

    @Test
    fun mtprotoProbeNeedsRealAnswer() {
        val good = fakeTelegram(answer = true)
        val silent = fakeTelegram(answer = false)
        val probe = ru.khodakovsky.wltester.core.probe.HttpsProbe(ru.khodakovsky.wltester.core.probe.JvmPlatform, 1500, 1500)
        val ok = probe.run(
            ru.khodakovsky.wltester.core.probe.HttpsProbe.Request(
                Category.VPN, Target.parse("mtproto://127.0.0.1:${good.localPort} # Telegram, сервер 1")!!,
                connectIp = InetAddress.getByName("127.0.0.1"), tls = false, http = false, mtproto = true,
            )
        ).result
        assertEquals(ProbeStatus.OK, ok.status, ok.error)
        assertEquals("сервер Telegram ответил", ok.info)
        val bad = probe.run(
            ru.khodakovsky.wltester.core.probe.HttpsProbe.Request(
                Category.VPN, Target.parse("mtproto://127.0.0.1:${silent.localPort}")!!,
                connectIp = InetAddress.getByName("127.0.0.1"), tls = false, http = false, mtproto = true,
            )
        ).result
        assertEquals(ProbeStatus.HTTP_TIMEOUT, bad.status)
        good.close(); silent.close()
    }
}
