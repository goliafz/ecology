package ru.khodakovsky.wltester.ui

import android.app.TimePickerDialog
import android.os.Build
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.khodakovsky.wltester.core.Defaults
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.core.VlessLinks
import ru.khodakovsky.wltester.data.AppSettings
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.Preset
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.monitor.Scheduler

@Composable
fun SettingsScreen() {
    val ctx = LocalContext.current
    val store = remember { SettingsStore.get(ctx) }
    val s by store.settings.collectAsState()
    val clipboard = LocalClipboardManager.current
    var confirmClear by remember { mutableStateOf(false) }

    fun upd(f: (AppSettings) -> AppSettings) {
        val n = store.update(f)
        if (n.monitoringEnabled) Scheduler.schedule(ctx)
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
        Text("Настройки", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)

        // ---------- Расписание
        SectionTitle("Когда проверять")
        ScheduleMode.entries.forEach { m ->
            RadioRow(m.ru, s.scheduleMode == m) { upd { it.copy(scheduleMode = m) } }
        }
        if (s.scheduleMode == ScheduleMode.WINDOW) {
            Text("Приложение работает только в этом промежутке (можно через полночь).", style = MaterialTheme.typography.bodySmall)
            Row(Modifier.padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = {
                    TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowStart = h * 60 + m) } }, s.windowStart / 60, s.windowStart % 60, true).show()
                }) { Text("С ${hhmm(s.windowStart)}") }
                OutlinedButton(onClick = {
                    TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowEnd = h * 60 + m) } }, s.windowEnd / 60, s.windowEnd % 60, true).show()
                }) { Text("До ${hhmm(s.windowEnd)}") }
            }
            if (s.windowStart == s.windowEnd) Text("Начало = конец: работает круглосуточно.", style = MaterialTheme.typography.bodySmall, color = Amber)
        } else {
            Text(
                "Постоянно делается быстрая проверка VPN. Если VPN не работает ${s.failThreshold} раза подряд (повтор через 1 мин), " +
                    "включаются полные проверки до восстановления VPN.",
                style = MaterialTheme.typography.bodySmall,
            )
        }

        // ---------- Частота
        SectionTitle("Частота проверок и батарея")
        Preset.entries.forEach { p ->
            RadioRow(p.ru, s.preset == p, p.hint) { upd { it.copy(preset = p) } }
        }
        if (s.preset == Preset.CUSTOM) {
            NumberField("Полная проверка, каждые N мин", s.customFullMin, 1..720) { v -> upd { it.copy(customFullMin = v) } }
            NumberField("Быстрая проверка VPN (режим «Постоянно»), каждые N мин", s.customWatchMin, 1..720) { v -> upd { it.copy(customWatchMin = v) } }
        }
        NumberField("Сколько неудачных быстрых проверок подряд = «VPN пропал»", s.failThreshold, 1..10) { v -> upd { it.copy(failThreshold = v) } }
        val kb = s.estimateFullKb()
        val perHour = kb * (60.0 / s.fullIntervalMin) / 1024
        Text(
            "Полная проверка расходует ≈ $kb КБ трафика; при текущей частоте ≈ ${"%.1f".format(perHour)} МБ в час.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        // ---------- Сеть и фон
        SectionTitle("Сеть и работа в фоне")
        SwitchRow("Только мобильная сеть", s.requireCellular, "Если подключён Wi-Fi — проверка пропускается и приходит напоминание") { v -> upd { it.copy(requireCellular = v) } }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            SwitchRow("Выключать Wi-Fi автоматически", s.autoWifiOff, "Работает только на Android 9 и ниже") { v -> upd { it.copy(autoWifiOff = v) } }
        }
        SwitchRow("Надёжный будильник", s.reliableAlarms, "Будит телефон точно в срок даже в глубоком сне (в строке состояния появится значок будильника)") { v -> upd { it.copy(reliableAlarms = v) } }
        SwitchRow("Уведомлять о событиях", s.notifyEvents, "Включение/отключение белых списков, пропадание и возврат VPN") { v -> upd { it.copy(notifyEvents = v) } }

        // ---------- VPN
        SectionTitle("VPN-профили (vless://)")
        Text(
            "Вставьте ссылки из подписки Marzban — по одной в строке. Ссылки хранятся только на телефоне.",
            style = MaterialTheme.typography.bodySmall,
        )
        OutlinedTextField(
            value = s.vlessLinks,
            onValueChange = { v -> upd { it.copy(vlessLinks = v) } },
            modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
            minLines = 3,
            maxLines = 8,
            textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace, fontSize = 11.sp),
            label = { Text("Ссылки vless://") },
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = {
                val clip = clipboard.getText()?.text.orEmpty().trim()
                if (clip.isNotEmpty()) upd { it.copy(vlessLinks = (it.vlessLinks.trim() + "\n" + clip).trim()) }
            }) { Text("Добавить из буфера") }
            OutlinedButton(onClick = { upd { it.copy(vlessLinks = "") } }) { Text("Очистить") }
        }
        val (profiles, errors) = remember(s.vlessLinks) { VlessLinks.parseAll(s.vlessLinks) }
        profiles.forEach { p ->
            Text(
                "✓ «${p.label}»: ${p.host}:${p.port}, ${p.security}, SNI ${p.effectiveSni}, ${p.flow.ifBlank { "без flow" }}, fp ${p.fingerprint}",
                style = MaterialTheme.typography.bodySmall, color = Green,
            )
        }
        errors.forEach { Text("✗ $it", style = MaterialTheme.typography.bodySmall, color = Red) }

        // ---------- Сайты
        SectionTitle("Сайты для проверки")
        Text("По одному в строке: домен, домен:порт или https://адрес. Всё после # — комментарий.", style = MaterialTheme.typography.bodySmall)
        SitesField("Российские (напрямую)", s.ruSites, { v -> upd { it.copy(ruSites = v) } }) { upd { it.copy(ruSites = Defaults.RU_SITES) } }
        SitesField("Зарубежные (напрямую)", s.foreignSites, { v -> upd { it.copy(foreignSites = v) } }) { upd { it.copy(foreignSites = Defaults.FOREIGN_SITES) } }
        SitesField("Через VPN (YouTube, Telegram, WhatsApp…)", s.vpnSites, { v -> upd { it.copy(vpnSites = v) } }) { upd { it.copy(vpnSites = Defaults.VPN_SITES) } }

        // ---------- Дополнительные тесты
        SectionTitle("Дополнительные тесты")
        SwitchRow("Тест загрузки", s.downloadEnabled, "Выявляет «заморозку» соединения после ~16 КБ — характерный признак ТСПУ") { v -> upd { it.copy(downloadEnabled = v) } }
        if (s.downloadEnabled) NumberField("Объём теста загрузки, КБ", s.downloadKb, 32..4096) { v -> upd { it.copy(downloadKb = v) } }
        SwitchRow("SNI-матрица для сервера VPN", s.sniMatrix, "Каждый порт сервера проверяется со всеми SNI из профилей и ya.ru — видно, режут ли по SNI") { v -> upd { it.copy(sniMatrix = v) } }
        SwitchRow("Ping сервера VPN", s.ping) { v -> upd { it.copy(ping = v) } }
        SwitchRow("DNS по UDP (8.8.8.8, 1.1.1.1, 77.88.8.8)", s.dnsUdp) { v -> upd { it.copy(dnsUdp = v) } }
        NumberField("Таймаут соединения, с", s.timeoutSec, 2..30) { v -> upd { it.copy(timeoutSec = v) } }
        NumberField("Хранить журнал, дней", s.retentionDays, 1..365) { v -> upd { it.copy(retentionDays = v) } }

        SectionTitle("Данные")
        OutlinedButton(onClick = { confirmClear = true }) { Text("Очистить журнал") }
        Spacer(Modifier.height(32.dp))
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text("Очистить журнал?") },
            text = { Text("Все записи проверок и события будут удалены без возможности восстановления. Сначала можно сохранить ZIP на вкладке «События».") },
            confirmButton = {
                TextButton(onClick = {
                    LogStore.get(ctx).clear()
                    LogStore.get(ctx).event(EventType.SERVICE, "Журнал очищен")
                    store.updateState { it.copy(memory = ru.khodakovsky.wltester.core.analysis.MonitorMemory()) }
                    confirmClear = false
                }) { Text("Удалить") }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("Отмена") } },
        )
    }
}

@Composable
private fun RadioRow(title: String, selected: Boolean, subtitle: String? = null, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().selectable(selected = selected, onClick = onClick).padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = onClick)
        Spacer(Modifier.width(4.dp))
        Column {
            Text(title)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
    }
}

@Composable
private fun NumberField(label: String, value: Int, range: IntRange, onChange: (Int) -> Unit) {
    var text by remember(value) { mutableStateOf(value.toString()) }
    OutlinedTextField(
        value = text,
        onValueChange = { t ->
            text = t.filter { it.isDigit() }.take(5)
            text.toIntOrNull()?.takeIf { it in range }?.let(onChange)
        },
        label = { Text(label) },
        singleLine = true,
        isError = text.toIntOrNull()?.let { it !in range } ?: true,
        supportingText = { Text("от ${range.first} до ${range.last}") },
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
    )
}

@Composable
private fun SitesField(label: String, value: String, onChange: (String) -> Unit, onReset: () -> Unit) {
    Column(Modifier.padding(vertical = 6.dp)) {
        OutlinedTextField(
            value = value,
            onValueChange = onChange,
            label = { Text(label) },
            minLines = 3,
            maxLines = 10,
            textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            modifier = Modifier.fillMaxWidth(),
        )
        TextButton(onClick = onReset) { Text("Вернуть список по умолчанию") }
    }
}
