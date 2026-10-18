@file:OptIn(ExperimentalMaterial3Api::class)

package ru.khodakovsky.wltester.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.core.Category
import ru.khodakovsky.wltester.core.CheckResult
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.data.LogStore
import java.time.LocalDate
import java.time.format.DateTimeFormatter

private val dayLabel = DateTimeFormatter.ofPattern("dd.MM")

@Composable
fun JournalScreen() {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    Column(Modifier.fillMaxSize()) {
        Text(
            "Журнал", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold,
            modifier = Modifier.padding(16.dp, 16.dp, 16.dp, 8.dp),
        )
        TabRow(selectedTabIndex = tab) {
            Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("Проверки") })
            Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("События и отчёт") })
        }
        if (tab == 0) ChecksTab() else EventsTab()
    }
}

@Composable
private fun ChecksTab() {
    val ctx = LocalContext.current
    val log = remember { LogStore.get(ctx) }
    val version by log.version.collectAsState()
    val days by produceState(emptyList<LocalDate>(), version) { value = withContext(Dispatchers.IO) { log.days() } }
    var selected by remember { mutableStateOf<LocalDate?>(null) }
    val day = selected ?: days.firstOrNull()
    val cycles by produceState(emptyList<CycleRecord>(), day, version) {
        value = withContext(Dispatchers.IO) { day?.let { log.cycles(it).asReversed() } ?: emptyList() }
    }
    var onlyFailures by rememberSaveable { mutableStateOf(false) }

    Column(Modifier.fillMaxSize()) {
        if (days.isEmpty()) {
            Text("Здесь появятся проверки. Пока их не было.", modifier = Modifier.padding(16.dp))
            return@Column
        }
        Text(
            "Нажмите на проверку, чтобы увидеть каждый сайт отдельно.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(16.dp, 8.dp, 16.dp, 4.dp),
        )
        LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            items(days) { d ->
                FilterChip(selected = d == day, onClick = { selected = d }, label = { Text(dayLabel.format(d)) })
            }
        }
        Row(Modifier.padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = onlyFailures, onCheckedChange = { onlyFailures = it })
            Text("Показывать только то, что не открылось", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
            Text("${cycles.size} пров.", style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(end = 8.dp))
        }
        LazyColumn(contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(cycles, key = { it.id }) { c -> CycleItem(c, onlyFailures) }
        }
    }
}

@Composable
private fun CycleItem(c: CycleRecord, onlyFailures: Boolean) {
    var expanded by rememberSaveable(c.id) { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth().clickable { expanded = !expanded }) {
        Column(Modifier.padding(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(fmtTime(c.startedAt).take(5), fontWeight = FontWeight.Bold)
                Spacer(Modifier.width(8.dp))
                Text("${c.kind.ru}, ${c.trigger.ru}", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                if (c.kind != CycleKind.SKIPPED) Badge(c.summary.verdict.ru, verdictColor(c.summary.verdict)) else Badge("пропущена", Gray)
            }
            val s = c.summary
            if (c.kind == CycleKind.SKIPPED) {
                c.notes.firstOrNull()?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = Amber) }
            } else {
                Text(
                    "Российские сайты: ${s.ruOk} из ${s.ruTotal}. Зарубежные: ${s.foreignOk} из ${s.foreignTotal}.",
                    style = MaterialTheme.typography.bodySmall,
                )
                s.vpn.forEach { (name, ok) ->
                    Text(
                        "VPN «$name»: ${if (ok) "работает" else "не работает"}",
                        style = MaterialTheme.typography.bodySmall, color = if (ok) Green else Red,
                    )
                }
            }
            Text(c.network.shortRu(), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (!expanded) return@Column

            c.notes.forEach { Text(it, color = Amber, style = MaterialTheme.typography.bodySmall) }
            c.publicIpDirect?.let { Text("Ваш IP-адрес без VPN: $it", style = MaterialTheme.typography.bodySmall) }
            c.publicIpVpn.forEach { (k, v) -> Text("IP-адрес через VPN «$k»: $v", style = MaterialTheme.typography.bodySmall) }
            Category.entries.forEach { cat ->
                val list = c.checks.filter { it.category == cat && (!onlyFailures || !it.ok) }
                if (list.isNotEmpty()) {
                    HorizontalDivider(Modifier.padding(vertical = 6.dp))
                    Text(cat.ru, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                    list.forEach { CheckRow(it) }
                }
            }
        }
    }
}

@Composable
private fun CheckRow(r: CheckResult) {
    Column(Modifier.padding(vertical = 3.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Dot(statusColor(r.status))
            Spacer(Modifier.width(6.dp))
            val via = if (r.route != CheckResult.DIRECT) " (через «${r.route}»)" else ""
            Text(friendlyTarget(r) + via, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f), maxLines = 2)
        }
        Text(
            friendlyStatus(r), fontSize = 12.sp, color = statusColor(r.status),
            modifier = Modifier.padding(start = 16.dp),
        )
        val tech = listOfNotNull(
            r.dnsMs?.let { "DNS $it мс" }, r.tcpMs?.let { "TCP $it мс" }, r.tlsMs?.let { "TLS $it мс" },
            r.ttfbMs?.let { "ответ $it мс" }, r.httpCode?.let { "код $it" },
            r.bytes?.takeIf { it > 0 }?.let { "${it / 1024} КБ" }, r.speedKbps?.let { "$it кбит/с" },
            r.ip?.let { "IP $it" }, "всего ${r.totalMs} мс",
        ).joinToString(" · ")
        Text(tech, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 16.dp))
        r.info?.let { Text(it, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 16.dp)) }
        r.error?.let {
            Text(
                it, fontSize = 11.sp, fontFamily = FontFamily.Monospace, color = Red.copy(alpha = 0.8f),
                modifier = Modifier.padding(start = 16.dp), maxLines = 3,
            )
        }
    }
}
