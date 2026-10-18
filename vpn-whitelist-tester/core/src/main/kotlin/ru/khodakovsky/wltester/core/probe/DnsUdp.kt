package ru.khodakovsky.wltester.core.probe

import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.ProbeStatus
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import kotlin.random.Random

/** Прямой UDP-запрос к DNS-серверу (A-запись) — видно, режут ли сторонние DNS. */
object DnsUdp {

    fun query(server: String, name: String, timeoutMs: Int): CheckResult {
        val t0 = System.nanoTime()
        val id = Random.nextInt(0, 0xFFFF)
        return try {
            DatagramSocket().use { s ->
                s.soTimeout = timeoutMs
                val q = buildQuery(id, name)
                s.send(DatagramPacket(q, q.size, InetAddress.getByName(server), 53))
                val buf = ByteArray(1500)
                val p = DatagramPacket(buf, buf.size)
                s.receive(p)
                val ips = parseAnswer(buf.copyOf(p.length), id)
                CheckResult(
                    category = Category.DNS,
                    target = "$name @ $server (UDP)",
                    status = if (ips.isNotEmpty()) ProbeStatus.OK else ProbeStatus.DNS_FAIL,
                    ip = server,
                    port = 53,
                    dnsMs = HttpsProbe.msSince(t0),
                    totalMs = HttpsProbe.msSince(t0),
                    info = ips.joinToString(", ").ifEmpty { null },
                    error = if (ips.isEmpty()) "нет A-записей в ответе" else null,
                )
            }
        } catch (e: Exception) {
            CheckResult(
                category = Category.DNS,
                target = "$name @ $server (UDP)",
                status = ProbeStatus.DNS_FAIL,
                ip = server,
                port = 53,
                totalMs = HttpsProbe.msSince(t0),
                error = if (e is SocketTimeoutException) "нет ответа за $timeoutMs мс" else ErrorMapper.describe(e),
            )
        }
    }

    fun buildQuery(id: Int, name: String): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        out.write(id shr 8); out.write(id and 0xFF)
        out.write(0x01); out.write(0x00) // RD
        out.write(0); out.write(1) // QDCOUNT
        repeat(6) { out.write(0) }
        name.trimEnd('.').split('.').forEach { label ->
            val b = label.toByteArray(Charsets.US_ASCII)
            out.write(b.size); out.write(b)
        }
        out.write(0)
        out.write(0); out.write(1) // A
        out.write(0); out.write(1) // IN
        return out.toByteArray()
    }

    fun parseAnswer(msg: ByteArray, expectedId: Int): List<String> {
        if (msg.size < 12) return emptyList()
        val id = u16(msg, 0)
        if (id != expectedId) return emptyList()
        val rcode = msg[3].toInt() and 0x0F
        if (rcode != 0) return emptyList()
        val qd = u16(msg, 4)
        val an = u16(msg, 6)
        var pos = 12
        repeat(qd) { pos = skipName(msg, pos) + 4 }
        val ips = mutableListOf<String>()
        repeat(an) {
            if (pos >= msg.size) return ips
            pos = skipName(msg, pos)
            if (pos + 10 > msg.size) return ips
            val type = u16(msg, pos)
            val rdLen = u16(msg, pos + 8)
            pos += 10
            if (type == 1 && rdLen == 4 && pos + 4 <= msg.size) {
                ips += (0 until 4).joinToString(".") { (msg[pos + it].toInt() and 0xFF).toString() }
            }
            pos += rdLen
        }
        return ips
    }

    private fun u16(b: ByteArray, i: Int) = ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF)

    private fun skipName(b: ByteArray, start: Int): Int {
        var pos = start
        while (pos < b.size) {
            val len = b[pos].toInt() and 0xFF
            if (len == 0) return pos + 1
            if (len and 0xC0 == 0xC0) return pos + 2
            pos += len + 1
        }
        return pos
    }
}

/** Разбор вывода системного ping. */
object PingParser {
    data class Stats(val sent: Int, val received: Int, val avgMs: Double?, val mdevMs: Double? = null, val maxMs: Double? = null) {
        val lossPct: Int get() = if (sent == 0) 100 else (sent - received) * 100 / sent
    }

    fun parse(output: String): Stats? {
        val m = Regex("(\\d+) packets transmitted, (\\d+) (?:packets )?received").find(output) ?: return null
        val rtt = Regex("= ([\\d.]+)/([\\d.]+)/([\\d.]+)(?:/([\\d.]+))?").find(output)?.groupValues
        return Stats(
            sent = m.groupValues[1].toInt(),
            received = m.groupValues[2].toInt(),
            avgMs = rtt?.get(2)?.toDoubleOrNull(),
            mdevMs = rtt?.get(4)?.toDoubleOrNull(),
            maxMs = rtt?.get(3)?.toDoubleOrNull(),
        )
    }
}
