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
import androidx.compose.material3.TextButton
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
import ru.khodakovsky.wltester.data.AppSettings
import ru.khodakovsky.wltester.data.LogStore
import ru.khodakovsky.wltester.data.MonitorState
import ru.khodakovsky.wltester.data.Phase
import ru.khodakovsky.wltester.data.ScheduleMode
import ru.khodakovsky.wltester.data.SettingsStore
import ru.khodakovsky.wltester.monitor.MonitorRunner
import ru.khodakovsky.wltester.monitor.MonitorService
import ru.khodakovsky.wltester.monitor.Scheduler
import ru.khodakovsky.wltester.platform.AndroidPlatform
import ru.khodakovsky.wltester.platform.XrayTunnelFactory
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

private data class Todo(
    val text: String,
    val action: String? = null,
    /** false: необязательный совет, показывается спокойным цветом. */
    val important: Boolean = true,
    val onClick: (() -> Unit)? = null,
)

@Composable
fun HomeScreen(onOpenSettings: () -> Unit, onOpenHelp: () -> Unit) {
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
    val todos = remember(s, net, resumeTick) {
        buildTodos(ctx, s.vlessLinks, s.requireCellular, net, onOpenSettings) { perm -> permLauncher.launch(perm) }
    }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column {
            Text("WL Tester", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
            Text("Проверка белых списков и VPN", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }

        NowCard(last, progress)

        AutoCard(s, st, last)

        if (todos.isNotEmpty()) {
            Text("Что нужно сделать", style = MaterialTheme.typography.titleMedium)
            todos.forEach { TodoCard(it) }
        }

        DetailsCard(last, net)

        TextButton(onClick = onOpenHelp) { Text("Как пользоваться приложением") }
    }
}

@Composable
private fun NowCard(last: CycleRecord?, progress: String?) {
    val ctx = LocalContext.current
    val (color, title, explain) = when {
        last == null -> Triple(Gray, "Проверок ещё не было", "Нажмите «Проверить сейчас». Проверка займёт около минуты.")
        last.kind == CycleKind.SKIPPED -> Triple(Amber, "Последняя проверка не прошла", last.notes.firstOrNull() ?: "")
        else -> Triple(verdictColor(last.summary.verdict), verdictTitle(last.summary.verdict), verdictExplain(last.summary.verdict))
    }
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = color.copy(alpha = 0.12f))) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text("Сейчас", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Dot(color)
                Spacer(Modifier.width(8.dp))
                Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            }
            if (explain.isNotEmpty()) Text(explain, style = MaterialTheme.typography.bodyMedium)

            if (last != null && last.kind != CycleKind.SKIPPED) {
                val vpn = last.summary.vpn
                when {
                    vpn.isEmpty() -> Text("VPN не проверялся: добавьте ссылку на VPN в настройках.", color = Gray)
                    else -> vpn.forEach { (name, ok) ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Dot(if (ok) Green else Red)
                            Spacer(Modifier.width(8.dp))
                            val who = if (vpn.size > 1) "VPN «$name»" else "VPN"
                            Text(
                                if (ok) "$who работает" else "$who не работает",
                                style = MaterialTheme.typography.titleMedium,
                                color = if (ok) Green else Red,
                            )
                        }
                    }
                }
                last.summary.messengers.forEach { (name, ok) ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Dot(if (ok) Green else Red)
                        Spacer(Modifier.width(8.dp))
                        Text(if (ok) "$name через VPN работает" else "$name через VPN не работает", color = if (ok) Green else Red)
                    }
                }
                last.network.signalGrade()?.let { g ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Dot(gradeColor(g))
                        Spacer(Modifier.width(8.dp))
                        Text("Сигнал мобильной сети: ${g.ru}")
                    }
                }
            }
            if (last != null) {
                Text(
                    "Проверено ${whenText(last.startedAt)}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            if (progress != null) {
                Text("Идёт проверка, это займёт около минуты", color = MaterialTheme.colorScheme.primary)
                LinearProgressIndicator(Modifier.fillMaxWidth())
            }
            Button(
                onClick = { MonitorService.start(ctx, MonitorService.ACTION_RUN_MANUAL) },
                enabled = progress == null,
                modifier = Modifier.padding(top = 4.dp),
            ) { Text("Проверить сейчас") }
        }
    }
}

@Composable
private fun AutoCard(s: AppSettings, st: MonitorState, last: CycleRecord?) {
    val ctx = LocalContext.current
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Автоматические проверки", style = MaterialTheme.typography.titleMedium)
                    Text(
                        if (s.monitoringEnabled) "Включены" else "Выключены",
                        color = if (s.monitoringEnabled) Green else Gray,
                    )
                }
                Switch(checked = s.monitoringEnabled, onCheckedChange = { setMonitoring(ctx, it) })
            }
            if (!s.monitoringEnabled) {
                Text("Включите, и телефон будет сам проверять интернет в фоне, даже когда приложение закрыто.", style = MaterialTheme.typography.bodyMedium)
                return@Column
            }
            val plan = when (s.scheduleMode) {
                ScheduleMode.WINDOW -> "Каждый день с ${hhmm(s.windowStart)} до ${hhmm(s.windowEnd)}, раз в ${minutes(s.fullIntervalMin)}."
                ScheduleMode.ALWAYS -> "Весь день: раз в ${minutes(s.watchIntervalMin)} проверяю VPN, Telegram и WhatsApp, " +
                    "а ночью с ${hhmm(s.windowStart)} до ${hhmm(s.windowEnd)} раз в ${minutes(s.nightWatchMin)}. " +
                    "Если VPN не ответит 2 раза подряд, проверяю всё подробно раз в ${minutes(s.alertIntervalMin)}, пока он не вернётся."
            }
            Text(plan, style = MaterialTheme.typography.bodyMedium)
            when {
                s.scheduleMode == ScheduleMode.WINDOW && !Scheduler.inWindow(s) ->
                    Text("Сейчас не время проверок. Начну в ${hhmm(s.windowStart)}.", color = Gray)
                s.scheduleMode == ScheduleMode.ALWAYS && st.phase == Phase.ALERT -> {
                    val vpnBack = last?.summary?.let { it.vpnTested && it.vpnAny } == true
                    if (vpnBack) {
                        val left = (MonitorRunner.RECOVERY_OK - st.okStreak).coerceAtLeast(1)
                        Text(
                            if (left == 1) "VPN снова отвечает. Ещё одна удачная проверка, и вернусь к обычному режиму."
                            else "VPN снова отвечает. Ещё $left удачные проверки подряд, и вернусь к обычному режиму.",
                            color = Amber,
                        )
                    } else {
                        Text("VPN пропал, поэтому проверяю всё подробно раз в ${minutes(s.alertIntervalMin)}, пока он не вернётся.", color = Red)
                    }
                }
            }
            if (st.nextRunAt > 0) Text("Следующая проверка ${whenText(st.nextRunAt)}", style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun TodoCard(t: Todo) {
    val bg = if (t.important) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.surfaceVariant
    val fg = if (t.important) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.onSurfaceVariant
    Card(colors = CardDefaults.cardColors(containerColor = bg), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Text(t.text, color = fg)
            if (t.action != null && t.onClick != null) {
                OutlinedButton(onClick = t.onClick, modifier = Modifier.padding(top = 6.dp)) { Text(t.action) }
            }
        }
    }
}

@Composable
private fun DetailsCard(c: CycleRecord?, net: NetworkSnapshot?) {
    var open by rememberSaveable { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Подробности", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                TextButton(onClick = { open = !open }) { Text(if (open) "Скрыть" else "Показать") }
            }
            if (!open) return@Column
            net?.let { n ->
                Text("Сеть сейчас: ${n.shortRu()}")
                if (n.vpnInSystem) {
                    Text(
                        if (n.ourTrafficViaVpn) "VPN на телефоне включён, и приложение ходит через него. Так проверки неточные."
                        else "VPN на телефоне включён, приложение проверяет интернет мимо него. Так и должно быть.",
                        color = if (n.ourTrafficViaVpn) Red else Green,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
            if (c == null || c.kind == CycleKind.SKIPPED) return@Column
            Spacer(Modifier.height(4.dp))
            val sm = c.summary
            Text("Последняя проверка: ${c.kind.ru}, ${c.trigger.ru}")
            Text("Российские сайты: открылись ${sm.ruOk} из ${sm.ruTotal}")
            Text("Зарубежные сайты: открылись ${sm.foreignOk} из ${sm.foreignTotal}")
            if (sm.serverTcpTotal > 0) Text("Подключение к VPN-серверу: ${if (sm.serverTcpOk == sm.serverTcpTotal) "есть" else "нет"}")
            c.publicIpDirect?.let { Text("Ваш IP-адрес без VPN: $it", style = MaterialTheme.typography.bodySmall) }
            c.publicIpVpn.forEach { (k, v) -> Text("IP-адрес через VPN «$k»: $v", style = MaterialTheme.typography.bodySmall) }
            signalDetails(c).forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
            Text("Проверка шла ${(c.finishedAt - c.startedAt) / 1000} с", style = MaterialTheme.typography.bodySmall)
            c.notes.forEach { Text(it, color = Amber, style = MaterialTheme.typography.bodySmall) }
            Text("Все сайты по отдельности видны в разделе «Журнал».", style = MaterialTheme.typography.bodySmall, color = Gray)
        }
    }
}

/** «сегодня в 11:35», «вчера в 23:10» или «18.10 в 02:15». */
fun whenText(ms: Long): String {
    val zone = ZoneId.systemDefault()
    val d = Instant.ofEpochMilli(ms).atZone(zone)
    val today = LocalDate.now(zone)
    val time = "%02d:%02d".format(d.hour, d.minute)
    return when (d.toLocalDate()) {
        today -> "сегодня в $time"
        today.minusDays(1) -> "вчера в $time"
        today.plusDays(1) -> "завтра в $time"
        else -> "%02d.%02d в %s".format(d.dayOfMonth, d.monthValue, time)
    }
}

fun minutes(n: Int): String {
    val word = when {
        n % 10 == 1 && n % 100 != 11 -> "минуту"
        n % 10 in 2..4 && n % 100 !in 12..14 -> "минуты"
        else -> "минут"
    }
    return if (n == 1) "минуту" else "$n $word"
}

fun setMonitoring(ctx: Context, on: Boolean) {
    val store = SettingsStore.get(ctx)
    val s = store.update { it.copy(monitoringEnabled = on) }
    if (on) store.updateState { it.copy(phase = Phase.WATCH, failStreak = 0, okStreak = 0, lastRunAt = 0, windowActive = false) }
    val mode = when (s.scheduleMode) {
        ScheduleMode.WINDOW -> "С ${hhmm(s.windowStart)} до ${hhmm(s.windowEnd)}, раз в ${minutes(s.fullIntervalMin)}"
        ScheduleMode.ALWAYS -> "Весь день, VPN раз в ${minutes(s.watchIntervalMin)}, ночью раз в ${minutes(s.nightWatchMin)}, " +
            "при пропаже VPN подробно раз в ${minutes(s.alertIntervalMin)}"
    }
    LogStore.get(ctx).event(EventType.SERVICE, if (on) "Автопроверки включены" else "Автопроверки выключены", if (on) mode else null)
    Scheduler.schedule(ctx)
    if (!on && MonitorService.isRunning) MonitorService.start(ctx, MonitorService.ACTION_STOP)
}

@SuppressLint("BatteryLife")
private fun buildTodos(
    ctx: Context,
    links: String,
    requireCellular: Boolean,
    net: NetworkSnapshot?,
    openSettings: () -> Unit,
    requestPermission: (String) -> Unit,
): List<Todo> {
    val out = mutableListOf<Todo>()
    val (profiles, errors) = VlessLinks.parseAll(links)
    if (profiles.isEmpty()) {
        out += Todo("Добавьте ссылку на ваш VPN. Без неё приложение не сможет проверять, работает ли VPN.", "Добавить ссылку", onClick = openSettings)
    }
    if (errors.isNotEmpty()) out += Todo("В ссылке на VPN ошибка: ${errors.joinToString("; ")}", "Исправить", onClick = openSettings)
    if (!XrayTunnelFactory(ctx).available()) {
        out += Todo("В этой версии приложения нет модуля проверки VPN. Скачайте приложение заново со страницы релизов.")
    }
    if (requireCellular && net != null && net.transport == "WIFI") {
        out += Todo(
            "Сейчас включён Wi-Fi. Белые списки бывают только на мобильном интернете, поэтому на время проверок выключите Wi-Fi.",
            "Выключить Wi-Fi",
        ) {
            val i = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) Intent(Settings.Panel.ACTION_WIFI) else Intent(Settings.ACTION_WIFI_SETTINGS)
            ctx.startActivity(i)
        }
    }
    if (net?.ourTrafficViaVpn == true) {
        out += Todo("Приложение сейчас ходит в интернет через VPN, и проверки получаются неточными. В NekoBox уберите WL Tester из списка приложений, которые идут через VPN.")
    }
    if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
        out += Todo("Разрешите уведомления. Без них Android может остановить проверки в фоне, а вы не узнаете, что пропал VPN.", "Разрешить") {
            requestPermission(Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    val pm = ctx.getSystemService(PowerManager::class.java)
    if (!pm.isIgnoringBatteryOptimizations(ctx.packageName)) {
        out += Todo("Разрешите приложению работать в фоне без ограничений. Иначе ночью телефон «усыпит» проверки.", "Разрешить") {
            ctx.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${ctx.packageName}")))
        }
    }
    if (!Scheduler.canScheduleExact(ctx) && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        out += Todo("Разрешите будильники, чтобы проверки начинались вовремя.", "Разрешить") {
            ctx.startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:${ctx.packageName}")))
        }
    }
    if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
        out += Todo(
            "Необязательно: разрешите доступ к местоположению, тогда в журнале будет видно, к какой вышке подключён телефон " +
                "и в каком диапазоне. Координаты приложение не записывает.",
            "Разрешить",
            important = false,
        ) { requestPermission(Manifest.permission.ACCESS_FINE_LOCATION) }
    } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q &&
        ContextCompat.checkSelfPermission(ctx, Manifest.permission.ACCESS_BACKGROUND_LOCATION) != PackageManager.PERMISSION_GRANTED
    ) {
        out += Todo(
            "Необязательно: чтобы вышка записывалась и ночью, в настройках приложения откройте «Разрешения», затем «Местоположение» " +
                "и выберите «Разрешить в любом режиме».",
            "Открыть настройки",
            important = false,
        ) {
            ctx.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${ctx.packageName}")))
        }
    }
    if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) {
        out += Todo(
            "Необязательно: разрешите доступ к сведениям о сети, тогда в журнале будет видно, 4G у вас или 5G.",
            "Разрешить",
            important = false,
        ) { requestPermission(Manifest.permission.READ_PHONE_STATE) }
    }
    return out
}
