package ru.khodakovsky.wltester.platform

import android.content.Context
import ru.khodakovsky.wltester.core.VlessProfile
import ru.khodakovsky.wltester.core.XrayConfig
import ru.khodakovsky.wltester.core.engine.TunnelFactory
import ru.khodakovsky.wltester.core.engine.TunnelSession
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.TimeUnit

/**
 * Запускает ядро xray (libxray.so из nativeLibraryDir) отдельным процессом только на время проверки.
 * VPN-интерфейс не поднимается, поэтому NekoBox и другие VPN не мешают.
 */
class XrayTunnelFactory(private val ctx: Context) : TunnelFactory {

    val binary: File get() = File(ctx.applicationInfo.nativeLibraryDir, "libxray.so")
    fun available(): Boolean = binary.exists()

    override fun start(profiles: List<VlessProfile>, addresses: Map<Int, String>): TunnelSession {
        if (!available()) throw IOException("ядро xray не найдено (${binary.path})")
        val ports = freePorts(profiles.size)
        val dir = File(ctx.filesDir, "xray").apply { mkdirs() }
        val cfg = File(dir, "config.json")
        cfg.writeText(XrayConfig.build(profiles, ports, addresses).toString())

        val pb = ProcessBuilder(binary.absolutePath, "run", "-c", cfg.absolutePath)
            .directory(dir)
            .redirectErrorStream(true)
        pb.environment()["XRAY_LOCATION_ASSET"] = dir.absolutePath
        val proc = pb.start()
        val log = ArrayDeque<String>()
        val reader = Thread {
            runCatching {
                proc.inputStream.bufferedReader().forEachLine { line ->
                    synchronized(log) {
                        log.addLast(line.take(300))
                        while (log.size > 40) log.removeFirst()
                    }
                }
            }
        }.apply { isDaemon = true; start() }

        val deadline = System.currentTimeMillis() + 8000
        var ready = false
        while (System.currentTimeMillis() < deadline) {
            if (!proc.isAlive) break
            if (ports.all { canConnect(it) }) {
                ready = true
                break
            }
            Thread.sleep(100)
        }
        if (!ready) {
            val tail = synchronized(log) { log.joinToString(" | ") }
            proc.destroy()
            throw IOException(if (!proc.isAlive) "xray завершился: $tail" else "xray не открыл порты за 8 с: $tail")
        }

        return object : TunnelSession {
            override val socksPorts: List<Int> = ports
            override fun diagnostics(): String? = synchronized(log) { log.takeLast(15).joinToString(" | ") }.ifBlank { null }
            override fun close() {
                proc.destroy()
                if (!proc.waitFor(2, TimeUnit.SECONDS)) proc.destroyForcibly()
                reader.interrupt()
            }
        }
    }

    /** Версия ядра для экрана «О программе». */
    fun version(): String? {
        if (!available()) return null
        return runCatching {
            val p = ProcessBuilder(binary.absolutePath, "version").redirectErrorStream(true).start()
            val text = p.inputStream.bufferedReader().readText()
            p.waitFor(3, TimeUnit.SECONDS)
            text.lineSequence().firstOrNull()?.trim()
        }.getOrNull()
    }

    private fun freePorts(n: Int): List<Int> {
        val sockets = (0 until n).map { ServerSocket(0, 1, InetAddress.getByName("127.0.0.1")) }
        return sockets.map { it.localPort }.also { sockets.forEach { s -> s.close() } }
    }

    private fun canConnect(port: Int): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), 200) }
        true
    } catch (e: IOException) {
        false
    }
}
