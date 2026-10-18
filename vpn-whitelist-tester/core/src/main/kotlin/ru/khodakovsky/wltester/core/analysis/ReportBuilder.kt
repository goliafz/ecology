package ru.khodakovsky.wltester.core.analysis

import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Defaults
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.Stage
import ru.khodakovsky.wltester.core.Verdict
import ru.khodakovsky.wltester.core.engine.CycleEngine
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * Текстовый отчёт: хронология, ночи с белыми списками, что доступно в каком режиме и выводы.
 */
class ReportBuilder(private val zone: ZoneId = ZoneId.systemDefault()) {

    private val full = DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm:ss").withZone(zone)
    private val dm = DateTimeFormatter.ofPattern("dd.MM HH:mm").withZone(zone)
    private val hm = DateTimeFormatter.ofPattern("HH:mm").withZone(zone)
    private val day = DateTimeFormatter.ofPattern("dd.MM.yyyy").withZone(zone)

    class Agg {
        var ok = 0
        var total = 0
        val statuses = mutableMapOf<ProbeStatus, Int>()
        val stalledBytes = mutableListOf<Long>()
        val ms = mutableListOf<Long>()

        fun add(c: CheckResult) {
            total++
            if (c.ok) {
                ok++
                ms += c.totalMs
            } else {
                statuses.merge(c.status, 1, Int::plus)
            }
            if (c.status == ProbeStatus.STALLED) c.bytes?.let { stalledBytes += it }
        }

        val rate: Double get() = if (total == 0) 0.0 else ok.toDouble() / total
        fun pct(): String = if (total == 0) "—" else "${(rate * 100).toInt()}%"
        fun topFail(): ProbeStatus? = statuses.maxByOrNull { it.value }?.key
        fun failText(): String = statuses.entries.sortedByDescending { it.value }.take(2).joinToString("; ") { "${it.key.ru} ×${it.value}" }
    }

    data class Period<T>(val value: T, val items: List<CycleRecord>, val prevAt: Long?, val nextAt: Long?) {
        val first: CycleRecord get() = items.first()
        val last: CycleRecord get() = items.last()
        val count: Int get() = items.size
    }

    fun build(cycles: List<CycleRecord>, events: List<Event>, header: String = "WL Tester"): String {
        val all = cycles.sortedBy { it.startedAt }
        val sb = StringBuilder()
        fun line(s: String = "") = sb.append(s).append('\n')

        line("ОТЧЁТ $header")
        line("Сформирован: ${full.format(Instant.now())} (часовой пояс $zone)")
        if (all.isEmpty()) {
            line("Нет данных: ещё не было ни одной проверки.")
            return sb.toString()
        }
        val fulls = all.filter { it.kind == CycleKind.FULL }
        val quick = all.count { it.kind == CycleKind.QUICK }
        val skipped = all.count { it.kind == CycleKind.SKIPPED }
        line("Период: ${full.format(Instant.ofEpochMilli(all.first().startedAt))} — ${full.format(Instant.ofEpochMilli(all.last().startedAt))}")
        line("Циклов: полных ${fulls.size}, быстрых $quick, пропущено (не моб. сеть) $skipped")
        val operators = all.mapNotNull { it.network.operatorName?.takeIf { n -> n.isNotBlank() } }.groupingBy { it }.eachCount()
        if (operators.isNotEmpty()) line("Операторы: " + operators.entries.joinToString { "${it.key} (${it.value})" })
        val types = all.mapNotNull { it.network.networkType }.groupingBy { it }.eachCount()
        if (types.isNotEmpty()) line("Тип сети: " + types.entries.joinToString { "${it.key} (${it.value})" })
        if (all.any { it.network.ourTrafficViaVpn }) {
            line("⚠ В части циклов трафик тестера шёл через VPN — такие прямые проверки недостоверны.")
        }
        line()

        // ---- Хронология состояния сети
        line("=== 1. ХРОНОЛОГИЯ СОСТОЯНИЯ СЕТИ (полные проверки) ===")
        val vPeriods = periods(fulls) { it.summary.verdict.takeIf { v -> v != Verdict.UNKNOWN } }
        if (vPeriods.isEmpty()) line("Полных проверок пока нет.")
        vPeriods.forEach { p ->
            val ru = p.rate { it.summary.ruOk to it.summary.ruTotal }
            val fr = p.rate { it.summary.foreignOk to it.summary.foreignTotal }
            line("${range(p)}  ${p.value.ru.uppercase()}  (${p.count} пров.; РФ $ru, зарубежные $fr)")
        }
        line()

        // ---- VPN
        line("=== 2. ХРОНОЛОГИЯ VPN (все проверки) ===")
        val profiles = all.flatMap { it.summary.vpn.keys }.distinct()
        if (profiles.isEmpty()) line("VPN-профили не проверялись (добавьте ссылки vless:// в настройках).")
        profiles.forEach { prof ->
            line("Профиль «$prof»:")
            periods(all.filter { it.kind != CycleKind.SKIPPED }) { it.summary.vpn[prof] }.forEach { p ->
                line("  ${range(p)}  ${if (p.value) "РАБОТАЕТ" else "НЕ РАБОТАЕТ"}  (${p.count} пров.)")
            }
        }
        line()

        // ---- Ночи
        line("=== 3. КОГДА ВКЛЮЧАЮТ И ОТКЛЮЧАЮТ БЕЛЫЕ СПИСКИ ===")
        val wlPeriods = vPeriods.filter { it.value == Verdict.WHITELIST }
        if (wlPeriods.isEmpty()) {
            line("Белые списки за период не зафиксированы.")
        } else {
            val byNight = wlPeriods.groupBy { day.format(Instant.ofEpochMilli(it.first.startedAt - 12 * 3600_000L)) }
            val starts = mutableListOf<String>()
            val ends = mutableListOf<String>()
            byNight.forEach { (night, ps) ->
                val f = ps.first()
                val l = ps.last()
                val startTxt = f.prevAt?.let { "между ${hm.format(Instant.ofEpochMilli(it))} и ${hm.format(Instant.ofEpochMilli(f.first.startedAt))}" }
                    ?: "не позже ${hm.format(Instant.ofEpochMilli(f.first.startedAt))} (раньше проверок не было)"
                val endTxt = l.nextAt?.let { "между ${hm.format(Instant.ofEpochMilli(l.last.startedAt))} и ${hm.format(Instant.ofEpochMilli(it))}" }
                    ?: "после ${hm.format(Instant.ofEpochMilli(l.last.startedAt))} (дальше проверок не было)"
                line("Ночь с $night: включение $startTxt; отключение $endTxt" + if (ps.size > 1) " (прерывалось ${ps.size - 1} раз)" else "")
                starts += hm.format(Instant.ofEpochMilli(f.first.startedAt))
                ends += hm.format(Instant.ofEpochMilli(l.last.startedAt))
            }
            if (byNight.size > 1) {
                line("Начало: от ${starts.minOrNull()} до ${starts.maxOrNull()}; окончание: от ${ends.minOrNull()} до ${ends.maxOrNull()}")
            }
        }
        line()

        // ---- Сравнение
        val normal = fulls.filter { it.summary.verdict == Verdict.NORMAL }
        val wl = fulls.filter { it.summary.verdict == Verdict.WHITELIST }
        val partial = fulls.filter { it.summary.verdict == Verdict.PARTIAL || it.summary.verdict == Verdict.NO_INTERNET }
        line("=== 4. ДОСТУПНОСТЬ: НОРМА / БЕЛЫЕ СПИСКИ / ПРОЧЕЕ ===")
        line("Формат: цель — норма | белые списки | частичн./нет сети; после «⟂» — частая ошибка при белых списках")
        section(sb, "РФ напрямую", normal, wl, partial) { it.category == Category.RU }
        section(sb, "Зарубежные напрямую", normal, wl, partial) { it.category == Category.FOREIGN }
        section(sb, "Сервер VPN напрямую (DNS / TCP / TLS+SNI / ping)", normal, wl, partial) { it.category == Category.SERVER }
        section(sb, "Через VPN", normal, wl, partial, keyOf = { "[${it.route}] ${it.target}" }) { it.category == Category.VPN }
        section(sb, "DNS по UDP", normal, wl, partial) { it.category == Category.DNS }
        section(sb, "Загрузка (проверка «заморозки» после ~16 КБ)", normal, wl, partial, keyOf = { "[${it.route}] ${it.target}" }) {
            it.category == Category.DOWNLOAD || (it.category == Category.VPN && it.target.startsWith("Загрузка"))
        }
        line()

        // ---- Выводы
        line("=== 5. ВЫВОДЫ И ИДЕИ ОБХОДА ===")
        conclusions(all, fulls, wl, normal).forEachIndexed { i, s -> line("${i + 1}. $s") }
        line()

        // ---- События
        line("=== 6. СОБЫТИЯ ===")
        if (events.isEmpty()) line("Событий нет.")
        events.sortedBy { it.ts }.forEach { e ->
            line("${dm.format(Instant.ofEpochMilli(e.ts))}  ${e.title}" + (e.details?.let { " — $it" } ?: ""))
        }
        return sb.toString()
    }

    private fun section(
        sb: StringBuilder,
        title: String,
        normal: List<CycleRecord>,
        wl: List<CycleRecord>,
        other: List<CycleRecord>,
        keyOf: (CheckResult) -> String = { it.target },
        filter: (CheckResult) -> Boolean,
    ) {
        val n = aggregate(normal, filter, keyOf)
        val w = aggregate(wl, filter, keyOf)
        val o = aggregate(other, filter, keyOf)
        val keys = (n.keys + w.keys + o.keys).distinct()
        if (keys.isEmpty()) return
        sb.append("\n— ").append(title).append(":\n")
        keys.forEach { k ->
            val wa = w[k]
            val fail = wa?.takeIf { it.ok < it.total }?.failText()?.let { "  ⟂ $it" } ?: ""
            sb.append("  ").append(k.take(46).padEnd(46)).append(' ')
                .append((n[k]?.pct() ?: "—").padStart(4)).append(" | ")
                .append((wa?.pct() ?: "—").padStart(4)).append(" | ")
                .append((o[k]?.pct() ?: "—").padStart(4)).append(fail).append('\n')
        }
    }

    private fun conclusions(all: List<CycleRecord>, fulls: List<CycleRecord>, wl: List<CycleRecord>, normal: List<CycleRecord>): List<String> {
        val out = mutableListOf<String>()
        if (fulls.isEmpty()) return listOf("Мало данных: нужны полные проверки (в том числе ночью, во время ограничений).")
        if (wl.isEmpty()) {
            out += "Режим белых списков не зафиксирован. Если ночью VPN всё же пропадает — уменьшите интервал проверок в окне ограничений или проверьте, что тест идёт через мобильную сеть, а не Wi-Fi."
        }

        if (wl.isNotEmpty()) {
            val ruW = aggregate(wl, { it.category == Category.RU }, { it.target })
            val allowed = ruW.filter { it.value.rate >= 0.7 }.keys
            val blocked = ruW.filter { it.value.rate < 0.3 }.keys
            out += "При белых списках из российских сайтов доступны: ${allowed.joinToString().ifEmpty { "—" }}; недоступны: ${blocked.joinToString().ifEmpty { "нет" }}."

            val fW = wl.flatMap { c -> c.checks.filter { it.category == Category.FOREIGN && !it.ok } }
            val stageHist = fW.groupingBy { it.status.stage }.eachCount()
            val main = stageHist.maxByOrNull { it.value }?.key
            when (main) {
                Stage.TCP -> out += "Зарубежные сайты при белых списках падают уже на этапе TCP (${pctOf(stageHist, Stage.TCP, fW.size)} отказов) — фильтрация по IP-адресу назначения: пакеты на «чужие» IP просто не проходят."
                Stage.TLS -> out += "Зарубежные сайты при белых списках падают на этапе TLS (${pctOf(stageHist, Stage.TLS, fW.size)}): TCP соединяется, но рукопожатие режется — DPI по SNI. Помогает SNI из белого списка."
                Stage.DNS -> out += "Зарубежные домены при белых списках не резолвятся (DNS) — блокировка на уровне DNS; попробуйте свой DNS/DoH."
                Stage.HTTP -> out += "Зарубежные сайты соединяются по TCP/TLS, но не отвечают по HTTP — ограничение передачи данных после рукопожатия."
                else -> {}
            }
            val ipFor = fW.filter { it.target.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+")) }
            if (ipFor.isNotEmpty()) out += "Прямой доступ к зарубежному IP (без домена) при белых списках: ${ipFor.first().status.ru}."

            // Сервер VPN
            val tcpW = aggregate(wl, { it.category == Category.SERVER && it.target.startsWith("TCP ") }, { it.target })
            val tlsW = aggregate(wl, { it.category == Category.SERVER && it.target.startsWith("TLS ") }, { it.target })
            val vpnW = aggregate(wl, { it.category == Category.VPN && it.target == CycleEngine.HEALTH_LABEL && it.status != ProbeStatus.SKIPPED }, { it.route })
            val tcpRate = rateOf(tcpW.values)
            val tlsRate = rateOf(tlsW.values)
            if (tcpW.isNotEmpty()) {
                if (tcpRate < 0.2) {
                    out += "До VPN-сервера при белых списках не устанавливается даже TCP (${(tcpRate * 100).toInt()}% успешных; ошибки: ${tcpW.values.flatMap { it.statuses.keys }.distinct().joinToString { it.ru }}). " +
                        "Значит, режут по IP сервера (хостинг в Нидерландах не в белом списке). Маскировка SNI под ya.ru (порт 9443) тут не поможет — до TLS дело не доходит. " +
                        "Рабочий путь — промежуточный сервер (мост) с IP, который входит в белый список: VPS в российском облаке/у хостера, чьи сети пропускаются ночью, и каскад «телефон → RU-сервер → NL-сервер». " +
                        "Проверьте по таблице выше, какие российские хостинги доступны при белых списках, и протестируйте VPS у них (добавьте его ссылку vless:// как профиль)."
                } else if (tlsW.isNotEmpty()) {
                    val good = tlsW.filter { it.value.rate >= 0.7 }.keys
                    val bad = tlsW.filter { it.value.rate < 0.3 }.keys
                    if (bad.isNotEmpty() && good.isNotEmpty()) {
                        out += "TCP до сервера проходит, но TLS зависит от SNI: проходят ${good.joinToString()}, режутся ${bad.joinToString()}. Это фильтрация по SNI — используйте Reality с SNI из прошедших."
                    } else if (tlsRate >= 0.7) {
                        out += "TCP и TLS до сервера при белых списках проходят (${(tlsRate * 100).toInt()}%)."
                    } else if (tlsRate < 0.3) {
                        out += "TCP до сервера проходит, а TLS — нет при любом SNI. Возможна фильтрация «TCP разрешён, данные — нет» для зарубежных IP; смотрите блок «Загрузка»."
                    }
                }
            }
            vpnW.forEach { (prof, agg) ->
                out += if (agg.rate >= 0.7) {
                    "Профиль «$prof» при белых списках РАБОТАЛ в ${agg.pct()} проверок — это рабочий способ обхода."
                } else {
                    "Профиль «$prof» при белых списках работал в ${agg.pct()} проверок (ошибки: ${agg.failText()})."
                }
            }

            val hosting = aggregate(wl, { it.category == Category.RU && it.target in Defaults.HOSTING }, { it.target })
            if (hosting.isNotEmpty()) {
                val ok = hosting.filter { it.value.rate >= 0.7 }.keys
                if (ok.isNotEmpty()) out += "Сайты российских хостингов, доступные при белых списках: ${ok.joinToString()}. Их сети — кандидаты для промежуточного сервера (но доступность сайта ≠ доступность любого VPS у них, нужно проверить конкретный IP)."
            }

            val dnsW = aggregate(wl, { it.category == Category.DNS }, { it.target })
            val dnsBad = dnsW.filter { it.value.rate < 0.3 }.keys
            if (dnsBad.isNotEmpty()) out += "При белых списках не отвечают DNS: ${dnsBad.joinToString()}. Используйте DNS провайдера или российский (77.88.8.8)."
        }

        val stalls = all.flatMap { c -> c.checks.filter { it.status == ProbeStatus.STALLED } }
        val freeze = stalls.mapNotNull { it.bytes }.filter { it in 8 * 1024..64 * 1024 }
        if (freeze.size >= 2) {
            out += "Обнаружена «заморозка» загрузки: данные перестают идти после ~${freeze.sorted()[freeze.size / 2] / 1024} КБ (${freeze.size} случаев). " +
                "Это типичное поведение ТСПУ для соединений к зарубежным IP. Помогают протоколы/настройки, переоткрывающие соединения (mux, XHTTP), или сервер с IP из белого списка."
        }

        val normalVpn = aggregate(normal, { it.category == Category.VPN && it.target == CycleEngine.HEALTH_LABEL && it.status != ProbeStatus.SKIPPED }, { it.route })
        normalVpn.filter { it.value.rate < 0.7 }.forEach { (prof, agg) ->
            out += "Профиль «$prof» плохо работает даже в обычном режиме (${agg.pct()}): проверьте настройки профиля/сервера (${agg.failText()})."
        }
        if (out.isEmpty()) out += "Явных закономерностей пока нет — нужно больше данных."
        return out
    }

    private fun pctOf(h: Map<Stage, Int>, s: Stage, total: Int) = if (total == 0) "0%" else "${(h[s] ?: 0) * 100 / total}%"

    private fun rateOf(aggs: Collection<Agg>): Double {
        val t = aggs.sumOf { it.total }
        return if (t == 0) 0.0 else aggs.sumOf { it.ok }.toDouble() / t
    }

    private fun aggregate(cycles: List<CycleRecord>, filter: (CheckResult) -> Boolean, keyOf: (CheckResult) -> String): LinkedHashMap<String, Agg> {
        val m = LinkedHashMap<String, Agg>()
        cycles.forEach { c -> c.checks.filter(filter).forEach { m.getOrPut(keyOf(it)) { Agg() }.add(it) } }
        return m
    }

    private fun <T> periods(list: List<CycleRecord>, key: (CycleRecord) -> T?): List<Period<T>> {
        val items = list.mapNotNull { c -> key(c)?.let { c to it } }
        val out = mutableListOf<Period<T>>()
        var i = 0
        while (i < items.size) {
            var j = i
            while (j + 1 < items.size && items[j + 1].second == items[i].second) j++
            out += Period(
                value = items[i].second,
                items = items.subList(i, j + 1).map { it.first },
                prevAt = if (i > 0) items[i - 1].first.startedAt else null,
                nextAt = if (j + 1 < items.size) items[j + 1].first.startedAt else null,
            )
            i = j + 1
        }
        return out
    }

    private fun <T> Period<T>.rate(f: (CycleRecord) -> Pair<Int, Int>): String {
        val ok = items.sumOf { f(it).first }
        val tot = items.sumOf { f(it).second }
        return if (tot == 0) "—" else "${ok * 100 / tot}%"
    }

    private fun <T> range(p: Period<T>): String {
        val a = dm.format(Instant.ofEpochMilli(p.first.startedAt))
        val b = if (p.first === p.last) "" else " – " + dm.format(Instant.ofEpochMilli(p.last.startedAt))
        return (a + b).padEnd(27)
    }
}
