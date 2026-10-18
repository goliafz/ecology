package ru.khodakovsky.wltester.ui

import android.content.Intent
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.data.LogStore
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun EventsTab() {
    val ctx = LocalContext.current
    val log = remember { LogStore.get(ctx) }
    val scope = rememberCoroutineScope()
    val version by log.version.collectAsState()
    val events by produceState(emptyList<Event>(), version) { value = withContext(Dispatchers.IO) { log.events().asReversed() } }
    var report by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }

    val saveLauncher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/zip")) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            busy = true
            val ok = withContext(Dispatchers.IO) {
                runCatching { ctx.contentResolver.openOutputStream(uri)?.use { log.exportZip(it) } }.isSuccess
            }
            busy = false
            Toast.makeText(ctx, if (ok) "Файл сохранён" else "Не получилось сохранить файл", Toast.LENGTH_SHORT).show()
        }
    }

    Column(Modifier.fillMaxSize()) {
        Text(
            "Отчёт расскажет, когда включали белые списки и когда пропадал VPN. Файл с журналом можно сохранить или отправить разработчику.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(16.dp, 12.dp, 16.dp, 8.dp),
        )
        Row(Modifier.padding(horizontal = 16.dp).horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(enabled = !busy, onClick = {
                scope.launch {
                    busy = true
                    report = withContext(Dispatchers.IO) { log.report() }
                    busy = false
                }
            }) { Text("Показать отчёт") }
            OutlinedButton(enabled = !busy, onClick = {
                val stamp = DateTimeFormatter.ofPattern("yyyyMMdd-HHmm").withZone(ZoneId.systemDefault()).format(Instant.now())
                saveLauncher.launch("wltester-$stamp.zip")
            }) { Text("Сохранить файл") }
            OutlinedButton(enabled = !busy, onClick = {
                scope.launch {
                    busy = true
                    val file = withContext(Dispatchers.IO) { runCatching { log.exportFile() }.getOrNull() }
                    busy = false
                    if (file == null) {
                        Toast.makeText(ctx, "Не получилось подготовить файл", Toast.LENGTH_SHORT).show()
                        return@launch
                    }
                    val uri = FileProvider.getUriForFile(ctx, "${ctx.packageName}.files", file)
                    val send = Intent(Intent.ACTION_SEND).apply {
                        type = "application/zip"
                        putExtra(Intent.EXTRA_STREAM, uri)
                        putExtra(Intent.EXTRA_SUBJECT, "Журнал WL Tester")
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    }
                    ctx.startActivity(Intent.createChooser(send, "Куда отправить журнал"))
                }
            }) { Text("Отправить") }
        }
        if (events.isEmpty()) Text("Событий пока нет. Здесь будет видно, когда включали белые списки, пропадал VPN или менялась сеть.", modifier = Modifier.padding(16.dp))
        LazyColumn(contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(events) { e -> EventItem(e) }
        }
    }

    report?.let { text -> ReportDialog(text) { report = null } }
}

private fun eventColor(t: EventType): Color = when (t) {
    EventType.VERDICT_CHANGED -> Amber
    EventType.VPN_DOWN, EventType.ERROR -> Red
    EventType.VPN_UP -> Green
    EventType.PHASE_CHANGED -> Color(0xFF7C3AED)
    else -> Gray
}

@Composable
private fun EventItem(e: Event) {
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.Top) {
            Column(Modifier.padding(top = 5.dp)) { Dot(eventColor(e.type)) }
            Spacer(Modifier.width(10.dp))
            Column {
                Text(fmtDayTime(e.ts) + " · " + e.type.ru, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(e.title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
                e.details?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
        }
    }
}

@Composable
private fun ReportDialog(text: String, onClose: () -> Unit) {
    val clipboard = LocalClipboardManager.current
    var wrap by remember { mutableStateOf(true) }
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize()) {
            Column {
                Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Отчёт", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f).padding(start = 8.dp))
                    TextButton(onClick = { wrap = !wrap }) { Text(if (wrap) "Таблицей" else "Текстом") }
                    TextButton(onClick = { clipboard.setText(AnnotatedString(text)) }) { Text("Копировать") }
                    TextButton(onClick = onClose) { Text("Закрыть") }
                }
                val base = Modifier.weight(1f).verticalScroll(rememberScrollState())
                SelectionContainer(if (wrap) base.padding(12.dp) else base.horizontalScroll(rememberScrollState()).padding(12.dp)) {
                    Text(text, fontFamily = FontFamily.Monospace, fontSize = 11.sp, softWrap = wrap)
                }
            }
        }
    }
}
