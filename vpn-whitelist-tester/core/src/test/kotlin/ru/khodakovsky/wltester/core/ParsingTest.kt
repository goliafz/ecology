package ru.khodakovsky.wltester.core

import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import ru.khodakovsky.wltester.core.analysis.Analyzer
import ru.khodakovsky.wltester.core.probe.DnsUdp
import ru.khodakovsky.wltester.core.probe.PingParser
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ParsingTest {

    // Тестовые значения, не настоящие.
    private val link = "vless://11111111-2222-3333-4444-555555555555@vpn.example.org:9443?security=reality&type=tcp" +
        "&headerType=&flow=xtls-rprx-vision&path=&host=&sni=ya.ru&fp=firefox&pbk=AbCdEf123_-x&sid=0123abcd" +
        "#%F0%9F%9A%80%20Marz%20%28user%29%20%5B%D0%91%D0%B5%D0%BB%D1%8B%D0%B5%20%D1%81%D0%BF%D0%B8%D1%81%D0%BA%D0%B8%5D"

    @Test
    fun parsesMarzbanRealityLink() {
        val p = VlessLinks.parse(link)
        assertEquals("Белые списки", p.label)
        assertEquals("11111111-2222-3333-4444-555555555555", p.uuid)
        assertEquals("vpn.example.org", p.host)
        assertEquals(9443, p.port)
        assertEquals("reality", p.security)
        assertEquals("xtls-rprx-vision", p.flow)
        assertEquals("ya.ru", p.effectiveSni)
        assertEquals("firefox", p.fingerprint)
        assertEquals("AbCdEf123_-x", p.publicKey)
        assertEquals("0123abcd", p.shortId)
    }

    @Test
    fun parseAllMakesLabelsUniqueAndReportsErrors() {
        val (profiles, errors) = VlessLinks.parseAll("$link\n# comment\n$link\nhttp://bad\n")
        assertEquals(listOf("Белые списки", "Белые списки #2"), profiles.map { it.label })
        assertEquals(1, errors.size)
    }

    @Test
    fun rejectsUnsupportedTransport() {
        assertFailsWith<VlessParseException> { VlessLinks.parse(link.replace("type=tcp", "type=ws")) }
    }

    @Test
    fun xrayConfigHasInboundPerProfile() {
        val p = VlessLinks.parse(link)
        val cfg = XrayConfig.build(listOf(p, p.copy(label = "b", port = 443, sni = "example.org")), listOf(20001, 20002), mapOf(0 to "203.0.113.5"))
        val inbounds = cfg["inbounds"]!!.jsonArray
        assertEquals(2, inbounds.size)
        assertEquals("20002", inbounds[1].jsonObject["port"]!!.jsonPrimitive.content)
        val out0 = cfg["outbounds"]!!.jsonArray[0].jsonObject
        val vnext = out0["settings"]!!.jsonObject["vnext"]!!.jsonArray[0].jsonObject
        assertEquals("203.0.113.5", vnext["address"]!!.jsonPrimitive.content)
        val out1 = cfg["outbounds"]!!.jsonArray[1].jsonObject
        assertEquals("vpn.example.org", out1["settings"]!!.jsonObject["vnext"]!!.jsonArray[0].jsonObject["address"]!!.jsonPrimitive.content)
        val reality = out0["streamSettings"]!!.jsonObject["realitySettings"]!!.jsonObject
        assertEquals("ya.ru", reality["serverName"]!!.jsonPrimitive.content)
        assertEquals("AbCdEf123_-x", reality["publicKey"]!!.jsonPrimitive.content)
        val rules = cfg["routing"]!!.jsonObject["rules"]!!.jsonArray
        assertEquals("out-1", rules[1].jsonObject["outboundTag"]!!.jsonPrimitive.content)
    }

    @Test
    fun targetParsing() {
        assertEquals(Target("ya.ru"), Target.parse("  ya.ru  # comment"))
        assertEquals(Target("h", 8443, "/", "h:8443"), Target.parse("h:8443"))
        val u = Target.parse("https://speed.example.com/__down?bytes=100")!!
        assertEquals("speed.example.com", u.host)
        assertEquals("/__down?bytes=100", u.path)
        assertNull(Target.parse("# only comment"))
        assertTrue(Target.parseList(Defaults.RU_SITES).any { it.host == "gosuslugi.ru" })
    }

    @Test
    fun dnsQueryAndAnswer() {
        val q = DnsUdp.buildQuery(0x1234, "google.com")
        // ответ = запрос + флаги ответа + одна A-запись со ссылкой на имя
        val resp = q.copyOf()
        resp[2] = 0x81.toByte(); resp[3] = 0x80.toByte()
        resp[7] = 1
        val answer = byteArrayOf(0xC0.toByte(), 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 142.toByte(), 250.toByte(), 74, 46)
        val ips = DnsUdp.parseAnswer(resp + answer, 0x1234)
        assertEquals(listOf("142.250.74.46"), ips)
        assertEquals(emptyList(), DnsUdp.parseAnswer(resp + answer, 0x9999))
    }

    @Test
    fun pingParsing() {
        val out = """
            3 packets transmitted, 2 received, 33% packet loss, time 2003ms
            rtt min/avg/max/mdev = 60.123/65.500/70.000/4.000 ms
        """.trimIndent()
        val s = PingParser.parse(out)!!
        assertEquals(3, s.sent)
        assertEquals(2, s.received)
        assertEquals(65.5, s.avgMs)
        assertEquals(0, PingParser.parse("3 packets transmitted, 0 received, 100% packet loss")!!.received)
    }

    @Test
    fun badDecryptIsDataCorruption() {
        val e = javax.net.ssl.SSLProtocolException(
            "Read error: ssl=0x7d: Failure in SSL library, usually a protocol error\nerror:1e000065:Cipher functions:OPENSSL_internal:BAD_DECRYPT"
        )
        assertEquals(ProbeStatus.DATA_CORRUPT, ru.khodakovsky.wltester.core.probe.ErrorMapper.map(Stage.HTTP, e))
        assertEquals(ProbeStatus.HTTP_TIMEOUT, ru.khodakovsky.wltester.core.probe.ErrorMapper.map(Stage.HTTP, java.net.SocketTimeoutException("Read timed out")))
    }

    @Test
    fun verdicts() {
        assertEquals(Verdict.NORMAL, Analyzer.verdict(20, 20, 9, 9))
        assertEquals(Verdict.WHITELIST, Analyzer.verdict(15, 20, 0, 9))
        assertEquals(Verdict.WHITELIST, Analyzer.verdict(2, 2, 0, 2))
        assertEquals(Verdict.NO_INTERNET, Analyzer.verdict(0, 20, 0, 9))
        assertEquals(Verdict.PARTIAL, Analyzer.verdict(10, 20, 5, 9))
        assertEquals(Verdict.UNKNOWN, Analyzer.verdict(0, 0, 0, 0))
    }
}
