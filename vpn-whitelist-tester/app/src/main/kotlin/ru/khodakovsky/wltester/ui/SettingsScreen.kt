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
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.runtime.saveable.rememberSaveable
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
import ru.khodakovsky.wltester.core.analysis.MonitorMemory
import ru.khodakovsky.wltester.data.AppSettings
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.Phase
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
    var advanced by rememberSaveable { mutableStateOf(false) }

    fun upd(f: (AppSettings) -> AppSettings) {
        val n = store.update(f)
        if (n.monitoringEnabled) Scheduler.schedule(ctx)
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
        Text("Настройки", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
        Text("Всё сохраняется само.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

        // ---------- VPN
        SectionTitle("1. Ваш VPN")
        Text(
            "Нужна ссылка, которая начинается с vless://. Её дают в подписке (в панели Marzban). " +
                "Скопируйте ссылку и нажмите «Вставить». Ссылок может быть несколько, каждая с новой строки. " +
                "Ссылки хранятся только на этом телефоне.",
            style = MaterialTheme.typography.bodySmall,
        )
        Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = {
                val clip = clipboard.getText()?.text.orEmpty().trim()
                if (clip.isNotEmpty()) upd { it.copy(vlessLinks = (it.vlessLinks.trim() + "\n" + clip).trim()) }
            }) { Text("Вставить") }
            OutlinedButton(onClick = { upd { it.copy(vlessLinks = "") } }) { Text("Удалить все") }
        }
        OutlinedTextField(
            value = s.vlessLinks,
            onValueChange = { v -> upd { it.copy(vlessLinks = v) } },
            modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
            minLines = 2,
            maxLines = 6,
            textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace, fontSize = 11.sp),
            label = { Text("Ссылки на VPN") },
        )
        val (profiles, errors) = remember(s.vlessLinks) { VlessLinks.parseAll(s.vlessLinks) }
        if (profiles.isEmpty() && errors.isEmpty()) Text("Пока ни одной ссылки.", style = MaterialTheme.typography.bodySmall, color = Gray)
        profiles.forEach { p ->
            Text("✓ «${p.label}»: сервер ${p.host}, порт ${p.port}", style = MaterialTheme.typography.bodySmall, color = Green)
        }
        errors.forEach { Text("✗ $it", style = MaterialTheme.typography.bodySmall, color = Red) }

        // ---------- Когда
        SectionTitle("2. Когда проверять")
        RadioRow(
            ScheduleMode.WINDOW.ru, s.scheduleMode == ScheduleMode.WINDOW,
            "Проверять только в выбранные часы. Подходит, если ограничения бывают ночью.",
        ) { upd { it.copy(scheduleMode = ScheduleMode.WINDOW) } }
        if (s.scheduleMode == ScheduleMode.WINDOW) {
            Row(Modifier.padding(start = 48.dp, bottom = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = {
                    TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowStart = h * 60 + m) } }, s.windowStart / 60, s.windowStart % 60, true).show()
                }) { Text("С ${hhmm(s.windowStart)}") }
                OutlinedButton(onClick = {
                    TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowEnd = h * 60 + m) } }, s.windowEnd / 60, s.windowEnd % 60, true).show()
                }) { Text("До ${hhmm(s.windowEnd)}") }
            }
            if (s.windowStart == s.windowEnd) {
                Text("Начало и конец совпадают, поэтому проверки будут идти круглые сутки.", style = MaterialTheme.typography.bodySmall, color = Amber, modifier = Modifier.padding(start = 48.dp))
            }
        }
        RadioRow(
            ScheduleMode.ALWAYS.ru, s.scheduleMode == ScheduleMode.ALWAYS,
            "Обычно быстро проверять VPN, Telegram и WhatsApp. Как только VPN не ответит 2 раза подряд (обычно это значит, " +
                "что включили белые списки), проверять всё подробно и часто. Когда VPN вернётся, снова перейти на быстрые проверки.",
        ) {
            upd { it.copy(scheduleMode = ScheduleMode.ALWAYS) }
            store.updateState { it.copy(phase = Phase.WATCH, failStreak = 0, okStreak = 0) }
        }
        if (s.scheduleMode == ScheduleMode.ALWAYS) {
            Column(Modifier.padding(start = 48.dp)) {
                Text("Ночные часы: в это время белые списки вероятнее, поэтому VPN проверяется чаще.", style = MaterialTheme.typography.bodySmall)
                Row(Modifier.padding(vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = {
                        TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowStart = h * 60 + m) } }, s.windowStart / 60, s.windowStart % 60, true).show()
                    }) { Text("С ${hhmm(s.windowStart)}") }
                    OutlinedButton(onClick = {
                        TimePickerDialog(ctx, { _, h, m -> upd { it.copy(windowEnd = h * 60 + m) } }, s.windowEnd / 60, s.windowEnd % 60, true).show()
                    }) { Text("До ${hhmm(s.windowEnd)}") }
                }
                NumberField("Ночью проверять VPN раз во сколько минут", s.nightWatchMin, 1..720) { v -> upd { it.copy(nightWatchMin = v) } }
                NumberField("Когда VPN пропал: подробная проверка раз во сколько минут", s.alertIntervalMin, 1..720) { v ->
                    upd { it.copy(alertIntervalMin = v) }
                }
                val alertMb = s.estimateFullKb() * (60.0 / s.alertIntervalMin) / 1024
                Text(
                    "Пока VPN нет, уходит около ${"%.0f".format(alertMb)} МБ мобильного интернета в час. Зато журнал получается подробным.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        // ---------- Частота
        SectionTitle("3. Как часто")
        Preset.entries.forEach { p ->
            RadioRow(p.ru, s.preset == p, p.hint) { upd { it.copy(preset = p) } }
        }
        if (s.preset == Preset.CUSTOM) {
            NumberField("Подробная проверка: раз во сколько минут", s.customFullMin, 1..720) { v -> upd { it.copy(customFullMin = v) } }
            NumberField("Проверка VPN (режим «Весь день»): раз во сколько минут", s.customWatchMin, 1..720) { v -> upd { it.copy(customWatchMin = v) } }
        }
        val kb = s.estimateFullKb()
        val perHour = kb * (60.0 / s.fullIntervalMin) / 1024
        Text(
            "Одна подробная проверка тратит около $kb КБ мобильного интернета. С такой частотой выйдет около ${"%.1f".format(perHour)} МБ в час.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        // ---------- Фон
        SectionTitle("4. Работа в фоне")
        SwitchRow(
            "Проверять только мобильный интернет", s.requireCellular,
            "Если подключён Wi-Fi, проверка пропускается и приходит напоминание выключить его",
        ) { v -> upd { it.copy(requireCellular = v) } }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            SwitchRow("Выключать Wi-Fi самому", s.autoWifiOff, "Работает только на Android 9 и более ранних") { v -> upd { it.copy(autoWifiOff = v) } }
        }
        SwitchRow(
            "Будить телефон точно по времени", s.reliableAlarms,
            "Проверки идут вовремя даже ночью. Вверху экрана появится значок будильника",
        ) { v -> upd { it.copy(reliableAlarms = v) } }
        SwitchRow(
            "Уведомлять о важном", s.notifyEvents,
            "Когда включают или отключают белые списки и когда VPN пропадает или возвращается",
        ) { v -> upd { it.copy(notifyEvents = v) } }

        // ---------- Для опытных
        Spacer(Modifier.height(16.dp))
        HorizontalDivider()
        Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Для опытных", style = MaterialTheme.typography.titleMedium)
                Text("Списки сайтов и технические настройки. Обычно менять не нужно.", style = MaterialTheme.typography.bodySmall)
            }
            TextButton(onClick = { advanced = !advanced }) { Text(if (advanced) "Скрыть" else "Показать") }
        }
        if (advanced) AdvancedSettings(s, ::upd) { confirmClear = true }
        Spacer(Modifier.height(32.dp))
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text("Удалить весь журнал?") },
            text = { Text("Все проверки и события удалятся насовсем. Если журнал ещё нужен, сначала сохраните файл: «Журнал», вкладка «События и отчёт».") },
            confirmButton = {
                TextButton(onClick = {
                    LogStore.get(ctx).clear()
                    LogStore.get(ctx).event(EventType.SERVICE, "Журнал очищен")
                    store.updateState { it.copy(memory = MonitorMemory()) }
                    confirmClear = false
                }) { Text("Удалить") }
            },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("Отмена") } },
        )
    }
}

@Composable
private fun AdvancedSettings(s: AppSettings, upd: ((AppSettings) -> AppSettings) -> Unit, onClear: () -> Unit) {
    NumberField("Сколько раз подряд VPN должен не ответить, чтобы считать, что он пропал", s.failThreshold, 1..10) { v ->
        upd { it.copy(failThreshold = v) }
    }

    SectionTitle("Сайты для проверки")
    Text(
        "По одному в строке: адрес сайта (ya.ru), адрес с портом (ya.ru:443) или ссылка (https://…). Текст после # не учитывается.",
        style = MaterialTheme.typography.bodySmall,
    )
    SitesField("Российские сайты", s.ruSites, { v -> upd { it.copy(ruSites = v) } }) { upd { it.copy(ruSites = Defaults.RU_SITES) } }
    SitesField("Зарубежные сайты", s.foreignSites, { v -> upd { it.copy(foreignSites = v) } }) { upd { it.copy(foreignSites = Defaults.FOREIGN_SITES) } }
    SitesField("Сайты и мессенджеры через VPN", s.vpnSites, { v -> upd { it.copy(vpnSites = v) } }) {
        upd { it.copy(vpnSites = Defaults.VPN_SITES) }
    }

    SectionTitle("Дополнительные проверки")
    SwitchRow(
        "Скачивание файла", s.downloadEnabled,
        "Показывает, не «замерзает» ли связь после первых 16 КБ. Так оператор часто режет зарубежные сайты",
    ) { v -> upd { it.copy(downloadEnabled = v) } }
    if (s.downloadEnabled) NumberField("Сколько скачивать, КБ", s.downloadKb, 32..4096) { v -> upd { it.copy(downloadKb = v) } }
    SwitchRow(
        "Проверять сервер под разными именами (SNI)", s.sniMatrix,
        "Видно, режет ли оператор подключения по имени сайта",
    ) { v -> upd { it.copy(sniMatrix = v) } }
    SwitchRow(
        "Проверять через NekoBox", s.checkNekobox,
        "Кроме своей проверки VPN, приложение проверяет Telegram и сайты через сам NekoBox, так же, как ходят ваши приложения",
    ) { v -> upd { it.copy(checkNekobox = v) } }
    if (s.checkNekobox) {
        NumberField("Порт прокси NekoBox (в NekoBox: Настройки, «Порт прокси», обычно 2080)", s.nekoboxPort, 1..65535) { v ->
            upd { it.copy(nekoboxPort = v) }
        }
    }
    SwitchRow("Пинг до VPN-сервера", s.ping) { v -> upd { it.copy(ping = v) } }
    SwitchRow(
        "Мерить качество связи", s.quality,
        "Пинг до Яндекса: задержка, разброс и потери. Помогает понять, виноват ли слабый сигнал",
    ) { v -> upd { it.copy(quality = v) } }
    SwitchRow("Проверять DNS-серверы Google, Cloudflare и Яндекса", s.dnsUdp) { v -> upd { it.copy(dnsUdp = v) } }
    NumberField("Сколько секунд ждать ответа сайта", s.timeoutSec, 2..30) { v -> upd { it.copy(timeoutSec = v) } }
    NumberField("Сколько дней хранить журнал", s.retentionDays, 1..365) { v -> upd { it.copy(retentionDays = v) } }

    SectionTitle("Журнал")
    OutlinedButton(onClick = onClear) { Text("Удалить весь журнал") }
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
        supportingText = { Text("Можно от ${range.first} до ${range.last}") },
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
        TextButton(onClick = onReset) { Text("Вернуть исходный список") }
    }
}
