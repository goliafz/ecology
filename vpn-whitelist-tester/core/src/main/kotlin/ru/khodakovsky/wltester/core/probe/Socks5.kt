package ru.khodakovsky.wltester.core.probe

import java.io.DataInputStream
import java.io.IOException
import java.net.Socket

class SocksException(message: String) : IOException(message)

/** Минимальный SOCKS5-клиент (CONNECT по доменному имени, без авторизации). */
object Socks5 {

    fun connect(socket: Socket, host: String, port: Int) {
        val out = socket.getOutputStream()
        val input = DataInputStream(socket.getInputStream())
        out.write(byteArrayOf(5, 1, 0))
        out.flush()
        val ver = input.readUnsignedByte()
        val method = input.readUnsignedByte()
        if (ver != 5 || method != 0) throw SocksException("SOCKS: неожиданный ответ приветствия ($ver,$method)")

        val hostBytes = host.toByteArray(Charsets.US_ASCII)
        require(hostBytes.size in 1..255) { "bad host" }
        val req = ByteArray(7 + hostBytes.size)
        req[0] = 5; req[1] = 1; req[2] = 0; req[3] = 3
        req[4] = hostBytes.size.toByte()
        System.arraycopy(hostBytes, 0, req, 5, hostBytes.size)
        req[5 + hostBytes.size] = (port shr 8).toByte()
        req[6 + hostBytes.size] = port.toByte()
        out.write(req)
        out.flush()

        val rVer = input.readUnsignedByte()
        val rep = input.readUnsignedByte()
        input.readUnsignedByte()
        val atyp = input.readUnsignedByte()
        if (rVer != 5) throw SocksException("SOCKS: неверная версия ответа $rVer")
        val addrLen = when (atyp) {
            1 -> 4
            4 -> 16
            3 -> input.readUnsignedByte()
            else -> throw SocksException("SOCKS: неизвестный тип адреса $atyp")
        }
        input.readFully(ByteArray(addrLen + 2))
        if (rep != 0) throw SocksException("SOCKS: прокси вернул ошибку ${replyText(rep)}")
    }

    private fun replyText(code: Int) = when (code) {
        1 -> "1 (общий сбой)"
        2 -> "2 (запрещено)"
        3 -> "3 (сеть недоступна)"
        4 -> "4 (хост недоступен)"
        5 -> "5 (соединение отклонено)"
        6 -> "6 (TTL истёк)"
        else -> code.toString()
    }
}
