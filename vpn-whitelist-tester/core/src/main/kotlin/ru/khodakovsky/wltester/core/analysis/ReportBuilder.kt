package ru.khodakovsky.wltester.core.analysis

import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Defaults
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.SignalGrade
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
        fun pct(): String = if (total == 0) "-" else "${(rate * 100).toInt()}%"
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
        line("Составлен ${full.format(Instant.now())}, часовой пояс $zone")
        if (all.isEmpty()) {
            line("Проверок ещё не было, отчёт пустой.")
            return sb.toString()
        }
        val fulls = all.filter { it.kind == CycleKind.FULL }
        val quick = all.count { it.kind == CycleKind.QUICK }
        val skipped = all.count { it.kind == CycleKind.SKIPPED }
        line("Период: с ${full.format(Instant.ofEpochMilli(all.first().startedAt))} по ${full.format(Instant.ofEpochMilli(all.last().startedAt))}")
        line("Проверок: полных ${fulls.size}, быстрых проверок VPN $quick, пропущено $skipped (не было мобильного интернета)")
        val operators = all.mapNotNull { it.network.operatorText()?.takeIf { n -> n.isNotBlank() } }.groupingBy { it }.eachCount()
        if (operators.isNotEmpty()) line("Оператор: " + operators.entries.joinToString { "${it.key} (${it.value} пров.)" })
        val types = all.mapNotNull { it.network.networkType }.groupingBy { it }.eachCount()
        if (types.isNotEmpty()) line("Тип сети: " + types.entries.joinToString { "${it.key} (${it.value} пров.)" })
        if (all.any { it.network.ourTrafficViaVpn }) {
            line("Внимание: часть проверок прошла через VPN, а не напрямую. Их результаты без VPN неточные.")
        }
        line()

        line("=== 1. КАК РАБОТАЛ ИНТЕРНЕТ (по полным проверкам) ===")
        val vPeriods = periods(fulls) { it.summary.verdict.takeIf { v -> v != Verdict.UNKNOWN } }
        if (vPeriods.isEmpty()) line("Полных проверок пока не было.")
        vPeriods.forEach { p ->
            val ru = p.rate { it.summary.ruOk to it.summary.ruTotal }
            val fr = p.rate { it.summary.foreignOk to it.summary.foreignTotal }
            line("${range(p)}  ${p.value.ru.uppercase()}  (${p.count} пров.; российские сайты $ru, зарубежные $fr)")
        }
        line()

        line("=== 2. КАК РАБОТАЛ VPN ===")
        val profiles = all.flatMap { it.summary.vpn.keys }.distinct()
        if (profiles.isEmpty()) line("VPN не проверялся: в настройках нет ссылок vless://.")
        profiles.forEach { prof ->
            line("«$prof»:")
            periods(all.filter { it.kind != CycleKind.SKIPPED }) { it.summary.vpn[prof] }.forEach { p ->
                line("  ${range(p)}  ${if (p.value) "РАБОТАЛ" else "НЕ РАБОТАЛ"}  (${p.count} пров.)")
            }
        }
        line()

        line("=== 3. КОГДА ВКЛЮЧАЛИ И ОТКЛЮЧАЛИ БЕЛЫЕ СПИСКИ ===")
        val wlPeriods = vPeriods.filter { it.value == Verdict.WHITELIST }
        if (wlPeriods.isEmpty()) {
            line("За этот период белых списков не было.")
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
                line("Ночь на $night: включили $startTxt, отключили $endTxt" + if (ps.size > 1) " (с перерывами: ${ps.size - 1})" else "")
                starts += hm.format(Instant.ofEpochMilli(f.first.startedAt))
                ends += hm.format(Instant.ofEpochMilli(l.last.startedAt))
            }
            if (byNight.size > 1) {
                line("Обычно включают с ${starts.minOrNull()} до ${starts.maxOrNull()}, отключают с ${ends.minOrNull()} до ${ends.maxOrNull()}")
            }
        }
        line()

        val normal = fulls.filter { it.summary.verdict == Verdict.NORMAL }
        val wl = fulls.filter { it.summary.verdict == Verdict.WHITELIST }
        val partial = fulls.filter { it.summary.verdict == Verdict.PARTIAL || it.summary.verdict == Verdict.NO_INTERNET }
        line("=== 4. ЧТО ОТКРЫВАЛОСЬ ===")
        line("Колонки: обычный интернет | белые списки | сбои или нет интернета. Число: в скольких проверках открылось.")
        line("После «⟂» самая частая ошибка при белых списках. Прочерк: таких проверок не было.")
        section(sb, "Российские сайты", normal, wl, partial) { it.category == Category.RU }
        section(sb, "Зарубежные сайты", normal, wl, partial) { it.category == Category.FOREIGN }
        section(sb, "Ваш VPN-сервер напрямую (DNS, TCP, TLS с разными SNI, ping)", normal, wl, partial) { it.category == Category.SERVER }
        section(sb, "Сайты через VPN", normal, wl, partial, keyOf = { "[${it.route}] ${it.target}" }) { it.category == Category.VPN }
        section(sb, "Поиск адресов (DNS по UDP)", normal, wl, partial) { it.category == Category.DNS }
        section(sb, "Скачивание файла (видно, «замерзает» ли связь после ~16 КБ)", normal, wl, partial, keyOf = { "[${it.route}] ${it.target}" }) {
            it.category == Category.DOWNLOAD ||
                (it.category == Category.VPN && (it.target.startsWith("Скачивание") || it.target.startsWith("Загрузка")))
        }
        line()

        line("=== 5. СИГНАЛ И КАЧЕСТВО СВЯЗИ ===")
        signalSection(fulls, normal, wl, partial).forEach { line(it) }
        line()

        line("=== 6. ВЫВОДЫ И КАК МОЖНО ОБОЙТИ ===")
        (conclusions(all, fulls, wl, normal) + messengerConclusions(fulls) + signalConclusions(fulls))
            .forEachIndexed { i, s -> line("${i + 1}. $s") }
        line()

        line("=== 7. СОБЫТИЯ ===")
        if (events.isEmpty()) line("Событий нет.")
        events.sortedBy { it.ts }.forEach { e ->
            line("${dm.format(Instant.ofEpochMilli(e.ts))}  ${e.title}" + (e.details?.let { ". $it" } ?: ""))
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
        sb.append("\n").append(title).append(":\n")
        keys.forEach { k ->
            val wa = w[k]
            val fail = wa?.takeIf { it.ok < it.total }?.failText()?.let { "  ⟂ $it" } ?: ""
            sb.append("  ").append(k.take(46).padEnd(46)).append(' ')
                .append((n[k]?.pct() ?: "-").padStart(4)).append(" | ")
                .append((wa?.pct() ?: "-").padStart(4)).append(" | ")
                .append((o[k]?.pct() ?: "-").padStart(4)).append(fail).append('\n')
        }
    }

    private fun conclusions(all: List<CycleRecord>, fulls: List<CycleRecord>, wl: List<CycleRecord>, normal: List<CycleRecord>): List<String> {
        val out = mutableListOf<String>()
        if (fulls.isEmpty()) return listOf("Пока мало данных. Нужны полные проверки, лучше ночью, когда включают ограничения.")
        if (wl.isEmpty()) {
            out += "Белых списков за этот период не было. Если ночью VPN всё же пропадает, проверяйте чаще в это время " +
                "и убедитесь, что телефон в этот момент не на Wi-Fi."
        }

        if (wl.isNotEmpty()) {
            val ruW = aggregate(wl, { it.category == Category.RU }, { it.target })
            val allowed = ruW.filter { it.value.rate >= 0.7 }.keys
            val blocked = ruW.filter { it.value.rate < 0.3 }.keys
            out += "При белых списках открывались российские сайты: ${allowed.joinToString().ifEmpty { "ни один" }}. " +
                "Не открывались: ${blocked.joinToString().ifEmpty { "таких нет" }}."

            val fW = wl.flatMap { c -> c.checks.filter { it.category == Category.FOREIGN && !it.ok } }
            val stageHist = fW.groupingBy { it.status.stage }.eachCount()
            when (stageHist.maxByOrNull { it.value }?.key) {
                Stage.TCP -> out += "Зарубежные сайты при белых списках не отвечают совсем, связь обрывается на первом шаге " +
                    "(TCP, ${pctOf(stageHist, Stage.TCP, fW.size)} отказов). Значит, оператор пропускает только адреса из своего списка, " +
                    "а всё остальное просто не доходит."
                Stage.TLS -> out += "Зарубежные сайты при белых списках отвечают, но соединение рвётся на шифровании " +
                    "(TLS, ${pctOf(stageHist, Stage.TLS, fW.size)} отказов). Оператор смотрит на имя сайта в запросе (SNI). " +
                    "Может помочь подключение, которое называется именем разрешённого сайта."
                Stage.DNS -> out += "При белых списках у зарубежных сайтов не находится адрес (DNS). Может помочь другой DNS-сервер."
                Stage.HTTP -> out += "Зарубежные сайты при белых списках соединяются, но не присылают ответ: данные режут уже после подключения."
                else -> {}
            }
            val ipFor = fW.filter { it.target.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+")) }
            if (ipFor.isNotEmpty()) out += "Зарубежный адрес без имени сайта (${ipFor.first().target}) при белых списках: ${ipFor.first().status.ru}."

            val tcpW = aggregate(wl, { it.category == Category.SERVER && it.target.startsWith("TCP ") }, { it.target })
            val tlsW = aggregate(wl, { it.category == Category.SERVER && it.target.startsWith("TLS ") }, { it.target })
            val vpnW = aggregate(wl, { it.category == Category.VPN && it.target == CycleEngine.HEALTH_LABEL && it.status != ProbeStatus.SKIPPED }, { it.route })
            val tcpRate = rateOf(tcpW.values)
            val tlsRate = rateOf(tlsW.values)
            if (tcpW.isNotEmpty()) {
                if (tcpRate < 0.2) {
                    out += "При белых списках до вашего VPN-сервера не доходит даже первый пакет (TCP: успешно ${(tcpRate * 100).toInt()}%; " +
                        "ошибки: ${tcpW.values.flatMap { it.statuses.keys }.distinct().joinToString { it.ru }}). " +
                        "Оператор не пускает к адресу сервера в Нидерландах, и смена имени на ya.ru (порт 9443) тут не поможет: " +
                        "до шифрования дело не доходит. Рабочий вариант: сервер-посредник в России с адресом, который есть в белом списке. " +
                        "Телефон подключается к нему, а он уже к серверу в Нидерландах. Какие российские хостинги открываются при белых списках, " +
                        "видно в таблице выше. Если возьмёте там сервер, добавьте его ссылку vless:// в настройки и проверьте ночью."
                } else if (tlsW.isNotEmpty()) {
                    val good = tlsW.filter { it.value.rate >= 0.7 }.keys
                    val bad = tlsW.filter { it.value.rate < 0.3 }.keys
                    if (bad.isNotEmpty() && good.isNotEmpty()) {
                        out += "Подключение к серверу проходит, но шифрование зависит от имени сайта (SNI). Проходят: ${good.joinToString()}. " +
                            "Режутся: ${bad.joinToString()}. В профиле VPN нужно указывать имя из тех, что проходят."
                    } else if (tlsRate >= 0.7) {
                        out += "При белых списках подключение к серверу и шифрование проходят (${(tlsRate * 100).toInt()}%)."
                    } else if (tlsRate < 0.3) {
                        out += "Подключиться к серверу можно, а зашифровать соединение нельзя ни с каким именем сайта. " +
                            "Похоже, к зарубежным адресам пропускают только сам факт подключения, без данных. Смотрите блок «Скачивание файла»."
                    }
                }
            }
            vpnW.forEach { (prof, agg) ->
                out += if (agg.rate >= 0.7) {
                    "VPN «$prof» при белых списках работал (${agg.pct()} проверок). Через него ограничения обходятся."
                } else {
                    "VPN «$prof» при белых списках работал только в ${agg.pct()} проверок. Ошибки: ${agg.failText()}."
                }
            }

            val hosting = aggregate(wl, { it.category == Category.RU && it.target in Defaults.HOSTING }, { it.target })
            val hostingOk = hosting.filter { it.value.rate >= 0.7 }.keys
            if (hostingOk.isNotEmpty()) {
                out += "При белых списках открывались сайты российских хостингов: ${hostingOk.joinToString()}. " +
                    "У них можно попробовать взять сервер-посредник. Открытый сайт хостинга ещё не значит, что откроется любой их сервер, " +
                    "поэтому конкретный сервер нужно проверить."
            }

            val dnsW = aggregate(wl, { it.category == Category.DNS }, { it.target })
            val dnsBad = dnsW.filter { it.value.rate < 0.3 }.keys
            if (dnsBad.isNotEmpty()) out += "При белых списках не отвечают DNS-серверы: ${dnsBad.joinToString()}. Подойдёт DNS оператора или Яндекса (77.88.8.8)."
        }

        val corrupt = all.flatMap { c -> c.checks.filter { it.status == ProbeStatus.DATA_CORRUPT } }
        corrupt.groupBy { it.route }.forEach { (route, list) ->
            val targets = list.map { it.target }.distinct().joinToString()
            out += if (route == CheckResult.DIRECT) {
                "Без VPN у сайтов $targets данные приходят испорченными (${list.size} раз). Похоже, кто-то вмешивается в трафик по дороге."
            } else {
                "Через VPN «$route» не открываются $targets: сайт отвечает, но данные приходят испорченными (ошибка расшифровки BAD_DECRYPT, ${list.size} раз). " +
                    "Остальные сайты через этот VPN открываются, значит сам VPN работает. Скорее всего, это особенность ядра Xray в тестере " +
                    "при режиме xtls-rprx-vision. Если в NekoBox эти сайты открываются, на обычную работу VPN это не влияет."
            }
        }

        val stalls = all.flatMap { c -> c.checks.filter { it.status == ProbeStatus.STALLED } }
        val freeze = stalls.mapNotNull { it.bytes }.filter { it in 8 * 1024..64 * 1024 }
        if (freeze.size >= 2) {
            out += "Скачивание «замерзает» примерно на ${freeze.sorted()[freeze.size / 2] / 1024} КБ (${freeze.size} раз): " +
                "данные идут, а потом перестают. Так оператор обычно режет соединения с зарубежными адресами. " +
                "Помогают настройки, которые чаще переоткрывают соединение (mux, XHTTP), или сервер-посредник с адресом из белого списка."
        }

        val normalVpn = aggregate(normal, { it.category == Category.VPN && it.target == CycleEngine.HEALTH_LABEL && it.status != ProbeStatus.SKIPPED }, { it.route })
        normalVpn.filter { it.value.rate < 0.7 }.forEach { (prof, agg) ->
            out += "VPN «$prof» плохо работал даже при обычном интернете (${agg.pct()} проверок). Проверьте ссылку и сервер. Ошибки: ${agg.failText()}."
        }
        if (out.isEmpty()) out += "Явных закономерностей пока нет, нужно больше проверок."
        return out
    }

    private fun median(xs: List<Double>): Double? = xs.sorted().let { if (it.isEmpty()) null else it[it.size / 2] }

    private fun qualityOf(c: CycleRecord): CheckResult? = c.checks.firstOrNull { it.category == Category.QUALITY }

    private fun lossOf(c: CycleRecord): Int? =
        qualityOf(c)?.let { q -> if (!q.ok && q.status != ProbeStatus.SKIPPED) 100 else Regex("потерь (\\d+)%").find(q.info ?: "")?.groupValues?.get(1)?.toIntOrNull() }

    /** Связь плохая: слабый сигнал, много шума или заметные потери пакетов. */
    private fun poorLink(c: CycleRecord): Boolean =
        (c.network.signalGrade()?.let { it == SignalGrade.WEAK || it == SignalGrade.BAD } == true) || (lossOf(c) ?: 0) >= 20

    private fun signalLine(name: String, cs: List<CycleRecord>): String? {
        if (cs.isEmpty()) return null
        val parts = listOfNotNull(
            median(cs.mapNotNull { it.network.signalDbm?.toDouble() })?.let { "сигнал ${it.toInt()} dBm" },
            median(cs.mapNotNull { it.network.sinr })?.let { "сигнал/шум ${it.toInt()} дБ" },
            median(cs.mapNotNull { qualityOf(it)?.ttfbMs?.toDouble() })?.let { "пинг ${it.toInt()} мс" },
            median(cs.mapNotNull { lossOf(it)?.toDouble() })?.let { "потери ${it.toInt()}%" },
        )
        if (parts.isEmpty()) return null
        return "$name (${cs.size} пров., типичные значения): ${parts.joinToString(", ")}"
    }

    private fun signalSection(fulls: List<CycleRecord>, normal: List<CycleRecord>, wl: List<CycleRecord>, partial: List<CycleRecord>): List<String> {
        val out = mutableListOf<String>()
        out += "Сигнал: от -90 dBm и выше отличный, до -105 хороший, до -115 слабый, ниже очень слабый. " +
            "Сигнал/шум: от 13 дБ отлично, от 5 хорошо, от 0 слабо, ниже нуля плохо."
        listOfNotNull(
            signalLine("Обычный интернет", normal),
            signalLine("Белые списки", wl),
            signalLine("Сбои или нет интернета", partial),
        ).ifEmpty { listOf("Данных о сигнале пока нет.") }.forEach { out += it }

        val withMsg = fulls.filter { it.summary.messengers.isNotEmpty() }
        withMsg.flatMap { it.summary.messengers.keys }.distinct().forEach { svc ->
            signalLine("$svc через VPN работал", withMsg.filter { it.summary.messengers[svc] == true })?.let { out += it }
            signalLine("$svc через VPN не работал", withMsg.filter { it.summary.messengers[svc] == false })?.let { out += it }
        }

        val byCell = fulls.filter { it.network.cellLabel() != null }.groupBy { it.network.cellLabel()!! }
        if (byCell.isNotEmpty()) {
            out += "По вышкам:"
            byCell.entries.sortedByDescending { it.value.size }.forEach { (cell, cs) ->
                val normalPct = cs.count { it.summary.verdict == Verdict.NORMAL } * 100 / cs.size
                val band = cs.mapNotNull { it.network.band }.firstOrNull()?.let { ", диапазон $it" } ?: ""
                val dist = median(cs.mapNotNull { it.network.towerDistanceM?.toDouble() })?.let { ", до вышки ~${(it / 100).toInt() / 10.0} км" } ?: ""
                val msgFail = cs.count { c -> c.summary.messengers.values.any { !it } }
                out += "  $cell$band$dist: ${cs.size} пров., обычный интернет в $normalPct%, мессенджеры с перебоями в $msgFail"
            }
        } else {
            val why = fulls.mapNotNull { it.network.cellNote }.groupingBy { it }.eachCount().maxByOrNull { it.value }?.key
                ?: "нужно разрешить приложению доступ к местоположению"
            out += "Номер вышки не записывался: $why."
        }
        return out
    }

    private fun messengerConclusions(fulls: List<CycleRecord>): List<String> {
        val out = mutableListOf<String>()
        val withMsg = fulls.filter { it.summary.messengers.isNotEmpty() }
        val services = withMsg.flatMap { it.summary.messengers.keys }.distinct()
        services.forEach { svc ->
            val tested = withMsg.filter { svc in it.summary.messengers }
            val fails = tested.filter { it.summary.messengers[svc] == false }
            if (fails.isEmpty()) {
                out += "$svc через VPN работал во всех ${tested.size} проверках."
                return@forEach
            }
            val others = services - svc
            val othersOk = fails.count { c -> others.any { c.summary.messengers[it] == true } }
            val vpnOk = fails.count { it.summary.vpnAny }
            val poor = fails.count { poorLink(it) }
            val siteOkServersFail = fails.count { c ->
                val mine = c.checks.filter { Analyzer.isMessengerCheck(it) && Analyzer.serviceOf(it.target) == svc }
                mine.any { it.sni != null && it.ok } && mine.filter { it.sni == null }.none { it.ok }
            }
            val sb = StringBuilder("$svc через VPN не работал в ${fails.size} из ${tested.size} проверок.")
            when {
                poor * 2 > fails.size -> sb.append(
                    " В большинстве этих случаев связь была плохой (слабый сигнал, шум или потери пакетов), так что вероятная причина в связи.",
                )
                othersOk * 2 >= fails.size || vpnOk * 2 >= fails.size -> {
                    val what = if (othersOk * 2 >= fails.size) others.joinToString() + " через VPN" else "остальные сайты через VPN"
                    sb.append(
                        " При этом связь была нормальной, а $what работали. Значит, дело не в сигнале: не доходят именно данные $svc. " +
                            "Обычно это значит, что $svc не может достучаться до своих серверов с вашего VPN-сервера, или у самого $svc сбой.",
                    )
                }
                else -> sb.append(" Одновременно не работал и сам VPN, так что это общий сбой, а не только $svc.")
            }
            if (siteOkServersFail * 2 >= fails.size) {
                sb.append(" Сайт $svc через VPN открывался, а серверы самого приложения нет: страдает именно приложение.")
            }
            out += sb.toString()
        }
        return out
    }

    private fun signalConclusions(fulls: List<CycleRecord>): List<String> {
        val bad = fulls.filter { it.summary.verdict != Verdict.NORMAL && it.summary.verdict != Verdict.UNKNOWN }
        val good = fulls.filter { it.summary.verdict == Verdict.NORMAL }
        if (bad.size < 2 || fulls.none { it.network.signalDbm != null || qualityOf(it) != null }) return emptyList()
        val poorBad = bad.count { poorLink(it) }
        val poorGood = good.count { poorLink(it) }
        return when {
            poorBad * 2 > bad.size && poorGood * 2 < good.size.coerceAtLeast(1) -> listOf(
                "Сбои чаще всего совпадают с плохой связью ($poorBad из ${bad.size}). Возможно, часть проблем не из-за ограничений, " +
                    "а из-за слабого сигнала: сравните с другим местом или другой SIM-картой.",
            )
            poorBad * 4 < bad.size -> listOf(
                "Во время сбоев связь в основном была нормальной (плохая только в $poorBad из ${bad.size}). Значит, сбои вызывает не сигнал, а ограничения.",
            )
            else -> emptyList()
        }
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
        return if (tot == 0) "-" else "${ok * 100 / tot}%"
    }

    private fun <T> range(p: Period<T>): String {
        val a = dm.format(Instant.ofEpochMilli(p.first.startedAt))
        val b = if (p.first === p.last) "" else " – " + dm.format(Instant.ofEpochMilli(p.last.startedAt))
        return (a + b).padEnd(27)
    }
}
