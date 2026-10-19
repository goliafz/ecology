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
            messengers = messengers(checks),
            verdict = verdict(ruOk, ru.size, fOk, foreign.size),
            ruOk = ruOk, ruTotal = ru.size,
            foreignOk = fOk, foreignTotal = foreign.size,
            vpn = vpn,
            serverTcpOk = tcp.count { it.ok }, serverTcpTotal = tcp.size,
        )
    }

    /** «Telegram, сервер 2 (149.154.167.51:443)» -> «Telegram»; «telegram.org» -> «Telegram». */
    fun serviceOf(label: String): String {
        val l = label.lowercase()
        return when {
            "telegram" in l || l.startsWith("149.154.") || l.startsWith("91.108.") -> "Telegram"
            "whatsapp" in l -> "WhatsApp"
            else -> label.substringBefore(',').substringBefore(" (").trim()
        }
    }

    val MESSENGER_NAMES = setOf("Telegram", "WhatsApp")

    /** Проверки мессенджеров через VPN: сайты и серверы Telegram/WhatsApp. */
    fun isMessengerCheck(c: CheckResult): Boolean =
        c.category == Category.VPN && c.status != ProbeStatus.SKIPPED && serviceOf(c.target) in MESSENGER_NAMES

    /**
     * Работает ли мессенджер через VPN. Если проверялся путь через NekoBox, решает он (так работает само приложение),
     * иначе достаточно одного профиля. Приложение ходит на свои серверы,
     * а не на сайт, поэтому если серверы проверялись, решают они; иначе смотрим на сайт.
     */
    fun messengers(checks: List<CheckResult>): Map<String, Boolean> =
        checks.filter { isMessengerCheck(it) }
            .let { all -> all.filter { it.route == CycleEngine.NEKOBOX }.ifEmpty { all } } // через NekoBox ходят сами приложения
            .groupBy { serviceOf(it.target) }
            .mapValues { (_, list) ->
                val servers = list.filter { it.sni == null }
                (servers.ifEmpty { list }).any { it.ok }
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
    val networkType: String? = null,
    val messengers: Map<String, Boolean> = emptyMap(),
    val messengersAt: Long? = null,
)

object EventDetector {

    fun detect(mem: MonitorMemory, c: CycleRecord, fmt: (Long) -> String): Pair<List<Event>, MonitorMemory> {
        val events = mutableListOf<Event>()
        var m = mem
        val ts = c.startedAt

        val op = c.network.operatorName?.takeIf { it.isNotBlank() }
        if (mem.transport != null && (mem.transport != c.network.transport || (op != null && mem.operator != null && op != mem.operator))) {
            events += Event(ts, EventType.NETWORK_CHANGED, "Сеть сменилась: ${netName(mem.transport, mem.operator)} → ${netName(c.network.transport, op)}")
        }
        val nt = c.network.networkType
        if (c.network.isCellular && nt != null && mem.networkType != null && nt != mem.networkType) {
            events += Event(ts, EventType.NETWORK_CHANGED, "Тип сети сменился: ${mem.networkType} → $nt", c.network.signalText())
        }
        m = m.copy(transport = c.network.transport, operator = op ?: mem.operator, networkType = nt ?: mem.networkType)

        if (c.kind == CycleKind.SKIPPED) return events to m

        val s = c.summary
        val counts = "Российских сайтов открылось ${s.ruOk} из ${s.ruTotal}, зарубежных ${s.foreignOk} из ${s.foreignTotal}" +
            if (s.vpnTested) ". VPN: " + s.vpn.entries.joinToString { "«${it.key}» ${if (it.value) "работает" else "не работает"}" } else ""

        if (c.kind == CycleKind.FULL && s.verdict != Verdict.UNKNOWN) {
            val prev = mem.lastVerdict
            val between = mem.lastFullAt?.let { "Это произошло между ${fmt(it)} и ${fmt(ts)}. " } ?: ""
            when {
                prev == null -> events += Event(ts, EventType.VERDICT_CHANGED, "Первая проверка: ${s.verdict.ru.lowercase()}", counts)
                prev != s.verdict -> {
                    val title = when {
                        s.verdict == Verdict.WHITELIST -> "Включились белые списки"
                        prev == Verdict.WHITELIST -> "Белые списки отключились"
                        else -> "Интернет: было «${prev.ru.lowercase()}», стало «${s.verdict.ru.lowercase()}»"
                    }
                    events += Event(ts, EventType.VERDICT_CHANGED, title, between + counts)
                }
            }
            m = m.copy(lastVerdict = s.verdict, lastFullAt = ts)
        }

        if (s.messengers.isNotEmpty()) {
            s.messengers.forEach { (name, ok) ->
                val was = mem.messengers[name]
                if (was != null && was != ok) {
                    val between = mem.messengersAt?.let { "Это произошло между ${fmt(it)} и ${fmt(ts)}. " } ?: ""
                    val sig = c.network.signalText()?.let { "Сигнал: $it. " } ?: ""
                    events += Event(
                        ts, EventType.MESSENGER,
                        if (ok) "$name через VPN снова работает" else "$name через VPN перестал работать",
                        between + sig + counts,
                    )
                }
            }
            m = m.copy(messengers = mem.messengers + s.messengers, messengersAt = ts)
        }

        if (s.vpnTested) {
            s.vpn.forEach { (profile, ok) ->
                val was = mem.vpn[profile]
                if (was != null && was != ok) {
                    val between = mem.vpnAt?.let { "Это произошло между ${fmt(it)} и ${fmt(ts)}. " } ?: ""
                    events += if (ok) {
                        Event(ts, EventType.VPN_UP, "VPN «$profile» снова работает", between + counts)
                    } else {
                        val reason = c.checks.firstOrNull { it.category == Category.VPN && it.route == profile && it.target == CycleEngine.HEALTH_LABEL }
                        Event(ts, EventType.VPN_DOWN, "VPN «$profile» перестал работать", between + (reason?.status?.ru?.let { "Причина: $it. " } ?: "") + counts)
                    }
                }
            }
            m = m.copy(vpn = mem.vpn + s.vpn, vpnAt = ts)
        }
        return events to m
    }

    private fun netName(transport: String?, op: String?) = when (transport) {
        "CELLULAR" -> "мобильный интернет" + (op?.let { " ($it)" } ?: "")
        "WIFI" -> "Wi-Fi"
        "NONE" -> "нет интернета"
        else -> transport ?: "?"
    }
}
