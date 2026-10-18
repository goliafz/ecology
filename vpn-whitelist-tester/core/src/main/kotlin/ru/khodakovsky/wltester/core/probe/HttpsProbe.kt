package ru.khodakovsky.wltester.core.probe

import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.Stage
import ru.khodakovsky.wltester.core.Target
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.cert.X509Certificate
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SNIHostName
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory

/** То, что зависит от платформы (Android / JVM в тестах). */
interface NetPlatform {
    fun resolve(host: String): List<InetAddress> = InetAddress.getAllByName(host).toList()
    fun newSocket(): Socket = Socket()
}

object JvmPlatform : NetPlatform

/**
 * Пошаговая HTTPS-проверка: DNS → TCP → TLS (с заданным SNI) → HTTP.
 * Для каждой стадии сохраняется время и тип ошибки — это основа для выводов о способе блокировки.
 */
class HttpsProbe(
    private val platform: NetPlatform,
    private val connectTimeoutMs: Int = 6000,
    private val readTimeoutMs: Int = 8000,
    /** Для тестов можно подменить доверенные сертификаты. */
    private val customSslFactory: SSLSocketFactory? = null,
) {
    data class Request(
        val category: Category,
        val target: Target,
        val route: String = CheckResult.DIRECT,
        /** Если задан — соединение идёт через локальный SOCKS5 (xray), домен резолвит сервер VPN. */
        val socksPort: Int? = null,
        /** Подключаться к этому IP вместо DNS-резолва. */
        val connectIp: InetAddress? = null,
        /** SNI вместо host (например, «маскировка» под ya.ru). */
        val sni: String? = null,
        val tls: Boolean = true,
        val http: Boolean = true,
        val maxBytes: Long = 64 * 1024,
        val captureBody: Int = 0,
        /** Режим теста загрузки: таймаут чтения после начала данных = STALLED. */
        val download: Boolean = false,
        val deadlineMs: Long = 20_000,
        val label: String? = null,
        /** Сервер Telegram: после подключения отправить запрос протокола MTProto и дождаться ответа. */
        val mtproto: Boolean = false,
    )

    class Outcome(val result: CheckResult, val body: String?)

    private val sslFactory: SSLSocketFactory by lazy { customSslFactory ?: HttpsURLConnection.getDefaultSSLSocketFactory() }

    fun run(r: Request): Outcome {
        val t0 = System.nanoTime()
        var stage = Stage.DNS
        var dnsMs: Long? = null
        var tcpMs: Long? = null
        var tlsMs: Long? = null
        var ttfbMs: Long? = null
        var ip: String? = r.connectIp?.hostAddress
        var raw: Socket? = null
        var ssl: SSLSocket? = null
        var httpCode: Int? = null
        var bytes = 0L
        var bodyBytes = 0L
        var issuer: String? = null
        var certMismatch = false
        var status = ProbeStatus.ERROR
        var error: String? = null
        var info: String? = null
        val capture = ByteArrayOutputStream()
        val host = r.target.host
        val port = r.target.port
        val sni = r.sni ?: host

        try {
            if (r.socksPort != null) {
                stage = Stage.TCP
                val t = System.nanoTime()
                val s = Socket()
                raw = s
                try {
                    s.connect(InetSocketAddress(InetAddress.getByName("127.0.0.1"), r.socksPort), connectTimeoutMs)
                } catch (e: IOException) {
                    throw SocksException("проверка VPN не отвечает: ${e.message}")
                }
                s.soTimeout = connectTimeoutMs + readTimeoutMs
                Socks5.connect(s, host, port)
                tcpMs = msSince(t)
            } else {
                val addr = r.connectIp ?: run {
                    val t = System.nanoTime()
                    val list = resolveWithTimeout(host)
                    dnsMs = msSince(t)
                    list.firstOrNull { it is Inet4Address } ?: list.first()
                }
                ip = addr.hostAddress
                stage = Stage.TCP
                val t = System.nanoTime()
                val s = platform.newSocket()
                raw = s
                s.connect(InetSocketAddress(addr, port), connectTimeoutMs)
                tcpMs = msSince(t)
            }

            if (r.mtproto) {
                // Через VPN «подключение» к прокси есть всегда, поэтому проверяем, что сервер Telegram правда отвечает.
                stage = Stage.HTTP
                val th = System.nanoTime()
                raw!!.soTimeout = readTimeoutMs
                info = MtProto.ping(raw!!)
                ttfbMs = msSince(th)
                status = ProbeStatus.OK
            } else if (!r.tls) {
                status = ProbeStatus.OK
            } else {
                stage = Stage.TLS
                val t = System.nanoTime()
                val sock = sslFactory.createSocket(raw, sni, port, true) as SSLSocket
                ssl = sock
                if (!isIpLiteral(sni)) {
                    val params = sock.sslParameters
                    params.serverNames = listOf(SNIHostName(sni))
                    sock.sslParameters = params
                }
                sock.soTimeout = readTimeoutMs
                sock.startHandshake()
                tlsMs = msSince(t)
                val session = sock.session
                val leaf = session.peerCertificates.firstOrNull() as? X509Certificate
                issuer = leaf?.let { runCatching { shortDn(it.issuerX500Principal.name) }.getOrNull() }
                certMismatch = leaf == null || !HostnameCheck.matches(leaf, sni)
                if (certMismatch) error = "сертификат выдан не для $sni"

                if (!r.http) {
                    status = if (certMismatch) ProbeStatus.CERT_ERROR else ProbeStatus.OK
                } else {
                    stage = Stage.HTTP
                    val req = "GET ${r.target.path} HTTP/1.1\r\nHost: $host\r\nUser-Agent: $USER_AGENT\r\n" +
                        "Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n"
                    val out = sock.outputStream
                    out.write(req.toByteArray(Charsets.US_ASCII))
                    out.flush()
                    val th = System.nanoTime()
                    val input = sock.inputStream
                    val buf = ByteArray(16 * 1024)
                    val head = ByteArrayOutputStream()
                    var headerDone = false
                    var statusLine: String? = null
                    var stalled = false
                    var hitDeadline = false
                    val deadline = t0 + r.deadlineMs * 1_000_000
                    while (true) {
                        if (System.nanoTime() > deadline) {
                            hitDeadline = true
                            break
                        }
                        val n = try {
                            input.read(buf)
                        } catch (e: SocketTimeoutException) {
                            if (statusLine == null) throw e
                            stalled = true
                            break
                        }
                        if (n < 0) break
                        if (ttfbMs == null) ttfbMs = msSince(th)
                        bytes += n
                        if (!headerDone) {
                            head.write(buf, 0, n)
                            val arr = head.toByteArray()
                            if (statusLine == null) {
                                val le = indexOf(arr, CRLF)
                                if (le >= 0) statusLine = String(arr, 0, le, Charsets.ISO_8859_1)
                            }
                            val he = indexOf(arr, CRLFCRLF)
                            if (he >= 0) {
                                headerDone = true
                                val bodyStart = he + 4
                                val bodyLen = arr.size - bodyStart
                                bodyBytes += bodyLen
                                if (r.captureBody > 0) capture.write(arr, bodyStart, minOf(bodyLen, r.captureBody))
                            } else if (arr.size > 64 * 1024) {
                                headerDone = true
                            }
                        } else {
                            bodyBytes += n
                            val room = r.captureBody - capture.size()
                            if (room > 0) capture.write(buf, 0, minOf(n, room))
                        }
                        if (bytes >= r.maxBytes) break
                    }
                    if (statusLine == null) throw EOFException("соединение закрыто без HTTP-ответа")
                    httpCode = statusLine.split(' ').getOrNull(1)?.toIntOrNull()
                    if (httpCode == null) {
                        status = ProbeStatus.HTTP_ERROR
                        error = "не HTTP-ответ: ${statusLine.take(60)}"
                    } else if (r.download) {
                        status = if (stalled) ProbeStatus.STALLED else ProbeStatus.OK
                        info = buildString {
                            append("скачано ").append(kb(bodyBytes)).append(" КБ")
                            if (stalled) append(", потом данные перестали приходить")
                            if (hitDeadline) append(", остановлено: вышло время")
                        }
                    } else {
                        status = if (certMismatch) ProbeStatus.CERT_ERROR else ProbeStatus.OK
                        if (stalled) info = "сайт начал отвечать, но ответ оборвался на ${kb(bodyBytes)} КБ"
                    }
                }
            }
        } catch (e: Throwable) {
            status = ErrorMapper.map(stage, e)
            error = ErrorMapper.describe(e)
            if (r.download && bodyBytes > 0) info = "до ошибки скачано ${kb(bodyBytes)} КБ"
        } finally {
            runCatching { ssl?.close() }
            runCatching { raw?.close() }
        }

        val totalMs = msSince(t0)
        val speed = if (r.download && bodyBytes > 0 && ttfbMs != null) {
            val ms = (totalMs - (dnsMs ?: 0) - (tcpMs ?: 0) - (tlsMs ?: 0)).coerceAtLeast(1)
            bodyBytes * 8 / ms
        } else null
        val result = CheckResult(
            category = r.category,
            target = r.label ?: r.target.label,
            route = r.route,
            status = status,
            ip = ip,
            port = port,
            sni = if (r.tls) sni else null,
            dnsMs = dnsMs,
            tcpMs = tcpMs,
            tlsMs = tlsMs,
            ttfbMs = ttfbMs,
            totalMs = totalMs,
            httpCode = httpCode,
            bytes = if (r.http && r.tls) bodyBytes else null,
            speedKbps = speed,
            certIssuer = issuer,
            info = info,
            error = error,
        )
        val body = if (capture.size() > 0) capture.toString("UTF-8") else null
        return Outcome(result, body)
    }

    fun resolveWithTimeout(host: String): List<InetAddress> {
        if (isIpLiteral(host)) return listOf(InetAddress.getByName(host))
        val f = resolverPool.submit<List<InetAddress>> { platform.resolve(host) }
        return try {
            f.get(connectTimeoutMs.toLong(), TimeUnit.MILLISECONDS).also {
                if (it.isEmpty()) throw UnknownHostException("$host: пустой ответ DNS")
            }
        } catch (e: TimeoutException) {
            f.cancel(true)
            throw UnknownHostException("$host: таймаут DNS")
        } catch (e: java.util.concurrent.ExecutionException) {
            throw e.cause ?: e
        }
    }

    companion object {
        const val USER_AGENT =
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36"
        private val CRLF = byteArrayOf(13, 10)
        private val CRLFCRLF = byteArrayOf(13, 10, 13, 10)

        private val resolverPool = Executors.newCachedThreadPool { r ->
            Thread(r, "wl-dns").apply { isDaemon = true }
        }

        fun msSince(t: Long): Long = (System.nanoTime() - t) / 1_000_000

        private fun kb(b: Long): String = if (b < 10 * 1024) String.format(java.util.Locale.US, "%.1f", b / 1024.0) else (b / 1024).toString()

        fun isIpLiteral(s: String): Boolean = s.contains(':') || IPV4.matches(s)

        private val IPV4 = Regex("^\\d{1,3}(\\.\\d{1,3}){3}$")

        fun indexOf(data: ByteArray, pat: ByteArray): Int {
            outer@ for (i in 0..data.size - pat.size) {
                for (j in pat.indices) if (data[i + j] != pat[j]) continue@outer
                return i
            }
            return -1
        }

        fun shortDn(dn: String): String {
            val cn = Regex("CN=([^,]+)").find(dn)?.groupValues?.get(1)
            val o = Regex("O=([^,]+)").find(dn)?.groupValues?.get(1)
            return listOfNotNull(cn, o?.let { "($it)" }).joinToString(" ").ifBlank { dn }
        }
    }
}

/**
 * Минимальный запрос к серверу Telegram: req_pq_multi в транспорте «abridged», без шифрования.
 * Сервер отвечает resPQ с тем же nonce; так проверяется, что до Telegram доходят именно данные приложения.
 */
object MtProto {
    fun ping(socket: Socket): String {
        val nonce = ByteArray(16).also { java.util.Random().nextBytes(it) }
        val ms = System.currentTimeMillis()
        val msgId = ((ms / 1000) shl 32) or (((ms % 1000) shl 22) and 0xFFFFFFFCL)
        val body = java.nio.ByteBuffer.allocate(40).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            .putLong(0L).putLong(msgId).putInt(20).putInt(0xbe7e8ef1.toInt()).put(nonce).array()
        val out = socket.getOutputStream()
        out.write(byteArrayOf(0xef.toByte(), (body.size / 4).toByte()))
        out.write(body)
        out.flush()

        val input = java.io.DataInputStream(socket.getInputStream())
        var words = input.readUnsignedByte()
        if (words == 0x7f) {
            words = input.readUnsignedByte() or (input.readUnsignedByte() shl 8) or (input.readUnsignedByte() shl 16)
        }
        val reply = ByteArray(words * 4).also { input.readFully(it) }
        if (reply.size == 4) {
            val code = java.nio.ByteBuffer.wrap(reply).order(java.nio.ByteOrder.LITTLE_ENDIAN).int
            return "сервер Telegram ответил кодом $code"
        }
        if (reply.size < 40) throw IOException("сервер прислал слишком короткий ответ (${reply.size} байт)")
        val bb = java.nio.ByteBuffer.wrap(reply).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        val ctor = bb.getInt(20)
        if (ctor != 0x05162463) throw IOException("ответ не похож на Telegram")
        if (!reply.copyOfRange(24, 40).contentEquals(nonce)) throw IOException("ответ Telegram не совпал с запросом")
        return "сервер Telegram ответил"
    }
}

/** Проверка соответствия сертификата имени (SAN / CN, с поддержкой *.wildcard). */
object HostnameCheck {
    fun matches(cert: X509Certificate, host: String): Boolean {
        val h = host.lowercase().trimEnd('.')
        val sans = runCatching { cert.subjectAlternativeNames }.getOrNull().orEmpty()
        val ip = HttpsProbe.isIpLiteral(h)
        val names = sans.filter { (it[0] as? Int) == if (ip) 7 else 2 }.mapNotNull { it[1] as? String }
        if (ip) return names.any { it.equals(h, ignoreCase = true) }
        val candidates = names.ifEmpty {
            listOfNotNull(Regex("CN=([^,]+)").find(cert.subjectX500Principal.name)?.groupValues?.get(1))
        }
        return candidates.any { matchName(it.lowercase().trimEnd('.'), h) }
    }

    fun matchName(pattern: String, host: String): Boolean {
        if (!pattern.startsWith("*.")) return pattern == host
        val suffix = pattern.substring(1) // .example.com
        if (!host.endsWith(suffix)) return false
        val head = host.substring(0, host.length - suffix.length)
        return head.isNotEmpty() && !head.contains('.')
    }
}

object ErrorMapper {
    fun map(stage: Stage, e: Throwable): ProbeStatus {
        if (e is SocksException) return ProbeStatus.PROXY_ERROR
        if (e is UnknownHostException) return ProbeStatus.DNS_FAIL
        if (hasCertCause(e)) return ProbeStatus.CERT_ERROR
        val msg = chainMessages(e)
        // Рукопожатие прошло, но запись не расшифровалась: байты изменились по дороге (туннель/DPI).
        if ("bad_decrypt" in msg || "bad_record_mac" in msg || "bad record mac" in msg || "decryption_failed" in msg) {
            return ProbeStatus.DATA_CORRUPT
        }
        val timeout = e is SocketTimeoutException || "timed out" in msg || "etimedout" in msg || "timeout" in msg
        val reset = e is EOFException || "reset" in msg || "broken pipe" in msg || "epipe" in msg || "eof" in msg ||
            "closed" in msg || "abort" in msg || "end of stream" in msg || "unexpected end" in msg
        return when (stage) {
            Stage.DNS -> ProbeStatus.DNS_FAIL
            Stage.TCP -> when {
                timeout -> ProbeStatus.TCP_TIMEOUT
                e is java.net.NoRouteToHostException || "unreachable" in msg || "no route" in msg -> ProbeStatus.TCP_UNREACHABLE
                "refused" in msg -> ProbeStatus.TCP_REFUSED
                reset -> ProbeStatus.TCP_RESET
                else -> ProbeStatus.TCP_ERROR
            }
            Stage.TLS -> when {
                timeout -> ProbeStatus.TLS_TIMEOUT
                reset -> ProbeStatus.TLS_RESET
                else -> ProbeStatus.TLS_ERROR
            }
            Stage.HTTP -> when {
                timeout -> ProbeStatus.HTTP_TIMEOUT
                reset -> ProbeStatus.HTTP_RESET
                else -> ProbeStatus.HTTP_ERROR
            }
            Stage.OTHER -> ProbeStatus.ERROR
        }
    }

    fun describe(e: Throwable): String {
        val root = generateSequence(e) { it.cause }.take(4).toList()
        return root.joinToString(" ← ") { "${it.javaClass.simpleName}: ${it.message ?: ""}".trim() }.take(300)
    }

    private fun chainMessages(e: Throwable): String =
        generateSequence(e) { it.cause }.take(5).joinToString(" ") { (it.message ?: "") + " " + it.javaClass.simpleName }.lowercase()

    private fun hasCertCause(e: Throwable): Boolean =
        generateSequence(e) { it.cause }.take(6).any {
            it is java.security.cert.CertificateException || it is java.security.cert.CertPathValidatorException ||
                it.javaClass.simpleName.contains("CertPath") ||
                (it is javax.net.ssl.SSLHandshakeException && (it.message ?: "").contains("trust anchor", ignoreCase = true))
        }
}
