package ru.khodakovsky.wltester.core.analysis

import kotlinx.serialization.Serializable
import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.Summary
import ru.khodakovsky.wltester.core.Verdict
import ru.khodakovsky.wltester.core.engine.CycleEngine

object Analyzer {

    fun summarize(checks: List<CheckResult>): Summary {
        val ru = checks.filter { it.category == Category.RU }
        val foreign = checks.filter { it.category == Category.FOREIGN }
        val vpn = checks.filter { it.category == Category.VPN && it.target == CycleEngine.HEALTH_LABEL && it.status != ProbeStatus.SKIPPED }
            .associate { it.route to it.ok }
        val tcp = checks.filter { it.category == Category.SERVER && it.target.startsWith("TCP ") }
        val ruOk = ru.count { it.ok }
        val fOk = foreign.count { it.ok }
        return Summary(
            verdict = verdict(ruOk, ru.size, fOk, foreign.size),
            ruOk = ruOk, ruTotal = ru.size,
            foreignOk = fOk, foreignTotal = foreign.size,
            vpn = vpn,
            serverTcpOk = tcp.count { it.ok }, serverTcpTotal = tcp.size,
        )
    }

    /**
     * Белые списки: российские сайты в основном открываются, зарубежные — почти нет.
     */
    fun verdict(ruOk: Int, ruTotal: Int, fOk: Int, fTotal: Int): Verdict {
        if (ruTotal == 0 && fTotal == 0) return Verdict.UNKNOWN
        val ruRate = if (ruTotal > 0) ruOk.toDouble() / ruTotal else null
        val fRate = if (fTotal > 0) fOk.toDouble() / fTotal else null
        return when {
            (ruRate ?: 0.0) < 0.3 && (fRate ?: 0.0) < 0.3 -> Verdict.NO_INTERNET
            fRate != null && fRate <= 0.25 && (ruRate ?: 1.0) >= 0.5 -> Verdict.WHITELIST
            (ruRate ?: 1.0) >= 0.7 && (fRate ?: 1.0) >= 0.7 -> Verdict.NORMAL
            else -> Verdict.PARTIAL
        }
    }
}

/** То, что помнит мониторинг между циклами, чтобы замечать изменения. */
@Serializable
data class MonitorMemory(
    val lastVerdict: Verdict? = null,
    val lastFullAt: Long? = null,
    val vpn: Map<String, Boolean> = emptyMap(),
    val vpnAt: Long? = null,
    val transport: String? = null,
    val operator: String? = null,
)

object EventDetector {

    fun detect(mem: MonitorMemory, c: CycleRecord, fmt: (Long) -> String): Pair<List<Event>, MonitorMemory> {
        val events = mutableListOf<Event>()
        var m = mem
        val ts = c.startedAt

        val op = c.network.operatorName?.takeIf { it.isNotBlank() }
        if (mem.transport != null && (mem.transport != c.network.transport || (op != null && mem.operator != null && op != mem.operator))) {
            events += Event(ts, EventType.NETWORK_CHANGED, "Сеть: ${netName(mem.transport, mem.operator)} → ${netName(c.network.transport, op)}")
        }
        m = m.copy(transport = c.network.transport, operator = op ?: mem.operator)

        if (c.kind == CycleKind.SKIPPED) return events to m

        val s = c.summary
        val counts = "РФ ${s.ruOk}/${s.ruTotal}, зарубежные ${s.foreignOk}/${s.foreignTotal}" +
            if (s.vpnTested) ", VPN " + s.vpn.entries.joinToString { "${it.key}: ${if (it.value) "✓" else "✗"}" } else ""

        if (c.kind == CycleKind.FULL && s.verdict != Verdict.UNKNOWN) {
            val prev = mem.lastVerdict
            val between = mem.lastFullAt?.let { "Изменение произошло между ${fmt(it)} и ${fmt(ts)}. " } ?: ""
            when {
                prev == null -> events += Event(ts, EventType.VERDICT_CHANGED, "Начальное состояние сети: ${s.verdict.ru}", counts)
                prev != s.verdict -> {
                    val title = when {
                        s.verdict == Verdict.WHITELIST -> "Включились белые списки (было: ${prev.ru})"
                        prev == Verdict.WHITELIST -> "Белые списки отключились (теперь: ${s.verdict.ru})"
                        else -> "Состояние сети: ${prev.ru} → ${s.verdict.ru}"
                    }
                    events += Event(ts, EventType.VERDICT_CHANGED, title, between + counts)
                }
            }
            m = m.copy(lastVerdict = s.verdict, lastFullAt = ts)
        }

        if (s.vpnTested) {
            s.vpn.forEach { (profile, ok) ->
                val was = mem.vpn[profile]
                if (was != null && was != ok) {
                    val between = mem.vpnAt?.let { "Между ${fmt(it)} и ${fmt(ts)}. " } ?: ""
                    events += if (ok) {
                        Event(ts, EventType.VPN_UP, "VPN «$profile» снова работает", between + counts)
                    } else {
                        val reason = c.checks.firstOrNull { it.category == Category.VPN && it.route == profile && it.target == CycleEngine.HEALTH_LABEL }
                        Event(ts, EventType.VPN_DOWN, "VPN «$profile» перестал работать", between + (reason?.status?.ru ?: "") + ". " + counts)
                    }
                }
            }
            m = m.copy(vpn = mem.vpn + s.vpn, vpnAt = ts)
        }
        return events to m
    }

    private fun netName(transport: String?, op: String?) = when (transport) {
        "CELLULAR" -> "моб. сеть" + (op?.let { " ($it)" } ?: "")
        "WIFI" -> "Wi-Fi"
        "NONE" -> "нет сети"
        else -> transport ?: "?"
    }
}
