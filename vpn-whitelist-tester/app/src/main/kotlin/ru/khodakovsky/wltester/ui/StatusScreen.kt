package ru.khodakovsky.wltester.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.core.CycleKind
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.core.NetworkSnapshot
import ru.khodakovsky.wltester.core.VlessLinks
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.Phase
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.monitor.MonitorRunner
import ru.khodakovsky.wltester.monitor.MonitorService
import ru.khodakovsky.wltester.monitor.Scheduler
import ru.khodakovsky.wltester.platform.AndroidPlatform
import ru.khodakovsky.wltester.platform.XrayTunnelFactory

private data class Warning(val text: String, val action: String? = null, val onClick: (() -> Unit)? = null)

@Composable
fun StatusScreen(onOpenSettings: () -> Unit) {
    val ctx = LocalContext.current
    val store = remember { SettingsStore.get(ctx) }
    val log = remember { LogStore.get(ctx) }
    val s by store.settings.collectAsState()
    val st by store.stateFlow.collectAsState()
    val progress by MonitorRunner.progress.collectAsState()
    val version by log.version.collectAsState()
    var resumeTick by remember { mutableIntStateOf(0) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { resumeTick++ }

    val last by produceState<CycleRecord?>(null, version) { value = withContext(Dispatchers.IO) { log.lastCycle() } }
    val net by produceState<NetworkSnapshot?>(null, resumeTick, version) {
        value = withContext(Dispatchers.IO) { runCatching { AndroidPlatform(ctx).network() }.getOrNull() }
    }

    val permLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { resumeTick++ }
    val warnings = remember(s, net, resumeTick) { buildWarnings(ctx, s.vlessLinks, s.requireCellular, net, onOpenSettings) { perm -> permLauncher.launch(perm) } }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("WL Tester", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)

        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Мониторинг", style = MaterialTheme.typography.titleMedium)
                        Text(
                            if (s.monitoringEnabled) "включён" else "выключен",
                            color = if (s.monitoringEnabled) Green else Gray,
                        )
                    }
                    Switch(checked = s.monitoringEnabled, onCheckedChange = { setMonitoring(ctx, it) })
                }
                Spacer(Modifier.height(8.dp))
                val modeText = when (s.scheduleMode) {
                    ScheduleMode.WINDOW -> "По времени: ${hhmm(s.windowStart)}–${hhmm(s.windowEnd)}, полная проверка каждые ${s.fullIntervalMin} мин"
                    ScheduleMode.ALWAYS -> "Постоянно: слежение за VPN каждые ${s.watchIntervalMin} мин; если VPN пропал — полная проверка каждые ${s.fullIntervalMin} мин"
                }
                Text(modeText, style = MaterialTheme.typography.bodyMedium)
                Text("Профиль частоты: ${s.preset.ru}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (s.monitoringEnabled) {
                    if (s.scheduleMode == ScheduleMode.ALWAYS) {
                        Text(
                            if (st.phase == Phase.ALERT) "Сейчас: VPN недоступен — подробные проверки" else "Сейчас: слежение за VPN",
                            color = if (st.phase == Phase.ALERT) Red else MaterialTheme.colorScheme.onSurface,
                        )
                    } else if (!Scheduler.inWindow(s)) {
                        Text("Сейчас вне окна — ожидание", color = Gray)
                    }
                    if (st.nextRunAt > 0) Text("Следующая проверка: ${fmtDayTime(st.nextRunAt)}")
                }
                if (progress != null) {
                    Spacer(Modifier.height(8.dp))
                    Text(progress ?: "", color = MaterialTheme.colorScheme.primary)
                    LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
                }
                Spacer(Modifier.height(12.dp))
                Row {
                    Button(onClick = { MonitorService.start(ctx, MonitorService.ACTION_RUN_MANUAL) }, enabled = progress == null) {
                        Text("Проверить сейчас")
                    }
                }
            }
        }

        warnings.forEach { w ->
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer), modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp)) {
                    Text(w.text, color = MaterialTheme.colorScheme.onErrorContainer)
                    if (w.action != null && w.onClick != null) {
                        OutlinedButton(onClick = w.onClick, modifier = Modifier.padding(top = 6.dp)) { Text(w.action) }
                    }
                }
            }
        }

        net?.let { n ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Text("Сеть сейчас", style = MaterialTheme.typography.titleMedium)
                    Text(n.shortRu())
                    n.mccMnc?.let { Text("MCC/MNC: $it" + (n.simOperator?.let { sim -> " · SIM: $sim" } ?: ""), style = MaterialTheme.typography.bodySmall) }
                    if (n.vpnInSystem) {
                        Text(
                            if (n.ourTrafficViaVpn) "VPN в системе: трафик тестера идёт через VPN ✗" else "VPN в системе включён, тестер его обходит ✓",
                            color = if (n.ourTrafficViaVpn) Red else Green, style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }
        }

        LastCycleCard(last)

        Text(
            "Совет: на Xiaomi/Huawei/Samsung разрешите приложению автозапуск и работу в фоне «без ограничений», иначе система может остановить ночные проверки.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun LastCycleCard(c: CycleRecord?) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Последняя проверка", style = MaterialTheme.typography.titleMedium)
            if (c == null) {
                Text("Ещё не было. Нажмите «Проверить сейчас».", color = Gray)
                return@Column
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("${fmtDayTime(c.startedAt)} · ${c.kind.ru} · ${c.trigger.ru}", modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                if (c.kind != CycleKind.SKIPPED) Badge(c.summary.verdict.ru, verdictColor(c.summary.verdict))
            }
            if (c.kind == CycleKind.SKIPPED) {
                c.notes.forEach { Text(it, color = Amber) }
                return@Column
            }
            val sm = c.summary
            Text("РФ напрямую: ${sm.ruOk}/${sm.ruTotal}    Зарубежные: ${sm.foreignOk}/${sm.foreignTotal}")
            if (sm.serverTcpTotal > 0) Text("Порты VPN-сервера (TCP): ${sm.serverTcpOk}/${sm.serverTcpTotal}")
            sm.vpn.forEach { (name, ok) ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Dot(if (ok) Green else Red)
                    Spacer(Modifier.width(6.dp))
                    Text("VPN «$name»: ${if (ok) "работает" else "не работает"}")
                }
            }
            c.publicIpDirect?.let { Text("Внешний IP напрямую: $it", style = MaterialTheme.typography.bodySmall) }
            c.publicIpVpn.forEach { (k, v) -> Text("IP через «$k»: $v", style = MaterialTheme.typography.bodySmall) }
            Text("Сеть: ${c.network.shortRu()} · ${(c.finishedAt - c.startedAt) / 1000} с", style = MaterialTheme.typography.bodySmall)
            c.notes.forEach { Text(it, color = Amber, style = MaterialTheme.typography.bodySmall) }
        }
    }
}

fun setMonitoring(ctx: Context, on: Boolean) {
    val store = SettingsStore.get(ctx)
    val s = store.update { it.copy(monitoringEnabled = on) }
    if (on) store.updateState { it.copy(phase = Phase.WATCH, failStreak = 0, okStreak = 0, lastRunAt = 0, windowActive = false) }
    val mode = when (s.scheduleMode) {
        ScheduleMode.WINDOW -> "окно ${hhmm(s.windowStart)}–${hhmm(s.windowEnd)}, каждые ${s.fullIntervalMin} мин"
        ScheduleMode.ALWAYS -> "постоянно, слежение каждые ${s.watchIntervalMin} мин"
    }
    LogStore.get(ctx).event(EventType.SERVICE, if (on) "Мониторинг включён" else "Мониторинг выключен", if (on) mode else null)
    Scheduler.schedule(ctx)
    if (!on && MonitorService.isRunning) MonitorService.start(ctx, MonitorService.ACTION_STOP)
}

@SuppressLint("BatteryLife")
private fun buildWarnings(
    ctx: Context,
    links: String,
    requireCellular: Boolean,
    net: NetworkSnapshot?,
    openSettings: () -> Unit,
    requestPermission: (String) -> Unit,
): List<Warning> {
    val out = mutableListOf<Warning>()
    val (profiles, errors) = VlessLinks.parseAll(links)
    if (profiles.isEmpty()) out += Warning("Не добавлены VPN-профили — тест VPN выполняться не будет. Вставьте ссылки vless:// из подписки Marzban.", "Открыть настройки", openSettings)
    if (errors.isNotEmpty()) out += Warning("Ошибки в ссылках VPN: ${errors.joinToString("; ")}", "Открыть настройки", openSettings)
    if (!XrayTunnelFactory(ctx).available()) out += Warning("В этой сборке нет ядра xray — тест через VPN недоступен. Установите APK из GitHub Releases.")
    if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
        out += Warning("Нет разрешения на уведомления — без него Android может остановить фоновую службу, и вы не увидите события.", "Разрешить") {
            requestPermission(Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    val pm = ctx.getSystemService(PowerManager::class.java)
    if (!pm.isIgnoringBatteryOptimizations(ctx.packageName)) {
        out += Warning("Включена оптимизация батареи — ночью система будет «усыплять» проверки и блокировать сеть.", "Отключить оптимизацию") {
            ctx.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${ctx.packageName}")))
        }
    }
    if (!Scheduler.canScheduleExact(ctx) && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        out += Warning("Нет разрешения на точные будильники — проверки будут запускаться с опозданием.", "Разрешить") {
            ctx.startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:${ctx.packageName}")))
        }
    }
    if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) {
        out += Warning("Чтобы записывать тип сети (4G/5G), разрешите доступ к состоянию телефона (необязательно).", "Разрешить") {
            requestPermission(Manifest.permission.READ_PHONE_STATE)
        }
    }
    if (requireCellular && net != null && net.transport == "WIFI") {
        out += Warning("Подключён Wi-Fi. Проверки идут только через мобильный интернет — выключите Wi-Fi на время теста.", "Выключить Wi-Fi") {
            val i = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) Intent(Settings.Panel.ACTION_WIFI) else Intent(Settings.ACTION_WIFI_SETTINGS)
            ctx.startActivity(i)
        }
    }
    if (net?.ourTrafficViaVpn == true) {
        out += Warning("Трафик тестера идёт через VPN. Добавьте WL Tester в исключения NekoBox (обход для приложения), иначе прямые проверки недостоверны.")
    }
    return out
}
