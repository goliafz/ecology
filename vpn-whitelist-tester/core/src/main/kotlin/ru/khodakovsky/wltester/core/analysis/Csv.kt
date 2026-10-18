package ru.khodakovsky.wltester.core.analysis

import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Event
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** CSV с разделителем «;» и BOM — открывается в Excel/Google Таблицах без настройки. */
class Csv(zone: ZoneId = ZoneId.systemDefault()) {
    private val fmt = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss").withZone(zone)
    private fun t(ms: Long) = fmt.format(Instant.ofEpochMilli(ms))

    fun checks(cycles: List<CycleRecord>): String = table(
        listOf(
            "time", "cycle_id", "kind", "trigger", "verdict", "transport", "operator", "net_type", "category", "route", "target",
            "ip", "port", "sni", "status", "status_ru", "ok", "dns_ms", "tcp_ms", "tls_ms", "ttfb_ms", "total_ms", "http",
            "bytes", "speed_kbps", "cert_issuer", "info", "error",
        ),
        cycles.flatMap { c ->
            c.checks.map { r ->
                listOf(
                    t(c.startedAt), c.id, c.kind.name, c.trigger.name, c.summary.verdict.name, c.network.transport,
                    c.network.operatorName, c.network.networkType, r.category.name, r.route, r.target, r.ip, r.port, r.sni,
                    r.status.name, r.status.ru, if (r.ok) 1 else 0, r.dnsMs, r.tcpMs, r.tlsMs, r.ttfbMs, r.totalMs, r.httpCode,
                    r.bytes, r.speedKbps, r.certIssuer, r.info, r.error,
                )
            }
        },
    )

    fun cycles(cycles: List<CycleRecord>): String = table(
        listOf(
            "time", "cycle_id", "kind", "trigger", "verdict", "ru_ok", "ru_total", "foreign_ok", "foreign_total", "vpn",
            "server_tcp_ok", "server_tcp_total", "transport", "operator", "mcc_mnc", "net_type", "signal_dbm", "vpn_in_system",
            "tester_via_vpn", "public_ip", "public_ip_vpn", "duration_s", "notes",
        ),
        cycles.map { c ->
            val s = c.summary
            listOf(
                t(c.startedAt), c.id, c.kind.name, c.trigger.name, s.verdict.name, s.ruOk, s.ruTotal, s.foreignOk, s.foreignTotal,
                s.vpn.entries.joinToString(" ") { "${it.key}=${if (it.value) "ok" else "fail"}" },
                s.serverTcpOk, s.serverTcpTotal, c.network.transport, c.network.operatorName, c.network.mccMnc,
                c.network.networkType, c.network.signalDbm, if (c.network.vpnInSystem) 1 else 0,
                if (c.network.ourTrafficViaVpn) 1 else 0, c.publicIpDirect,
                c.publicIpVpn.entries.joinToString(" ") { "${it.key}=${it.value}" },
                (c.finishedAt - c.startedAt) / 1000, c.notes.joinToString(" | "),
            )
        },
    )

    fun events(events: List<Event>): String = table(
        listOf("time", "type", "type_ru", "title", "details"),
        events.map { listOf(t(it.ts), it.type.name, it.type.ru, it.title, it.details) },
    )

    private fun table(header: List<String>, rows: List<List<Any?>>): String {
        val sb = StringBuilder("﻿")
        sb.append(header.joinToString(";")).append("\r\n")
        rows.forEach { row -> sb.append(row.joinToString(";") { cell(it) }).append("\r\n") }
        return sb.toString()
    }

    private fun cell(v: Any?): String {
        val s = v?.toString() ?: return ""
        return if (s.any { it == ';' || it == '"' || it == '\n' || it == '\r' }) "\"" + s.replace("\"", "\"\"") + "\"" else s
    }
}
