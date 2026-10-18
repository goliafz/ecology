package ru.khodakovsky.wltester.ui

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.khodakovsky.wltester.core.ProbeStatus
import ru.khodakovsky.wltester.core.Verdict
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun WlTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val ctx = LocalContext.current
    val scheme = when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> if (dark) dynamicDarkColorScheme(ctx) else dynamicLightColorScheme(ctx)
        dark -> darkColorScheme(primary = Color(0xFF93B4FF))
        else -> lightColorScheme(primary = Color(0xFF1E3A8A), secondary = Color(0xFF0F766E))
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

val Green = Color(0xFF16A34A)
val Red = Color(0xFFDC2626)
val Amber = Color(0xFFD97706)
val Gray = Color(0xFF6B7280)

fun verdictColor(v: Verdict): Color = when (v) {
    Verdict.NORMAL -> Green
    Verdict.WHITELIST -> Red
    Verdict.PARTIAL -> Amber
    Verdict.NO_INTERNET -> Color(0xFF7F1D1D)
    Verdict.UNKNOWN -> Gray
}

fun statusColor(s: ProbeStatus): Color = when {
    s == ProbeStatus.OK -> Green
    s == ProbeStatus.CERT_ERROR -> Amber
    s == ProbeStatus.SKIPPED -> Gray
    else -> Red
}

@Composable
fun Badge(text: String, color: Color) {
    Box(
        Modifier
            .background(color.copy(alpha = 0.15f), RoundedCornerShape(6.dp))
            .padding(horizontal = 8.dp, vertical = 2.dp)
    ) {
        Text(text, color = color, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun Dot(color: Color) {
    Box(Modifier.size(10.dp).background(color, CircleShape))
}

@Composable
fun SectionTitle(text: String) {
    Text(
        text, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 20.dp, bottom = 6.dp),
    )
}

@Composable
fun SwitchRow(title: String, checked: Boolean, subtitle: String? = null, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        Spacer(Modifier.width(8.dp))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

private val hms = DateTimeFormatter.ofPattern("HH:mm:ss")
private val dmhm = DateTimeFormatter.ofPattern("dd.MM HH:mm")

fun fmtTime(ms: Long): String = hms.format(Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()))
fun fmtDayTime(ms: Long): String = dmhm.format(Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()))
fun hhmm(min: Int) = "%02d:%02d".format(min / 60, min % 60)

/** Понятное название проверки вместо технической метки из журнала. */
fun friendlyTarget(r: ru.khodakovsky.wltester.core.CheckResult): String {
    val t = r.target
    return when {
        t == ru.khodakovsky.wltester.core.engine.CycleEngine.HEALTH_LABEL -> "Работает ли VPN"
        t.startsWith("TCP ") -> "Подключение к серверу " + t.removePrefix("TCP ")
        t.startsWith("TLS ") -> {
            val rest = t.removePrefix("TLS ")
            val sni = rest.substringAfter(" SNI=", "")
            "Шифрование с сервером " + rest.substringBefore(" SNI=") + if (sni.isNotEmpty()) " под именем $sni" else ""
        }
        t.startsWith("DNS ") -> "Адрес сервера " + t.removePrefix("DNS ")
        t.startsWith("PING ") -> "Пинг до сервера " + t.removePrefix("PING ")
        t.endsWith("(UDP)") -> "DNS-сервер " + t.substringAfter("@ ").removeSuffix(" (UDP)")
        t.startsWith("Загрузка") -> "Скачивание файла"
        else -> t
    }
}

/** Короткий итог для пользователя: открылся / не открылся и почему. */
fun friendlyStatus(r: ru.khodakovsky.wltester.core.CheckResult): String {
    val health = r.target == ru.khodakovsky.wltester.core.engine.CycleEngine.HEALTH_LABEL
    return when {
        r.status == ProbeStatus.OK -> if (health) "работает" else "открылся"
        r.ok -> r.status.ru
        health -> "не работает: " + r.status.ru
        else -> "не открылся: " + r.status.ru
    }
}

fun verdictTitle(v: Verdict): String = when (v) {
    Verdict.NORMAL -> "Интернет работает как обычно"
    Verdict.WHITELIST -> "Включены белые списки"
    Verdict.PARTIAL -> "Часть сайтов не открывается"
    Verdict.NO_INTERNET -> "Интернета нет"
    Verdict.UNKNOWN -> "Пока непонятно"
}

fun verdictExplain(v: Verdict): String = when (v) {
    Verdict.NORMAL -> "Открываются и российские, и зарубежные сайты."
    Verdict.WHITELIST -> "Открываются только российские сайты из разрешённого списка, зарубежные нет."
    Verdict.PARTIAL -> "Что-то открывается, что-то нет. Возможно, сеть нестабильна или ограничения включаются."
    Verdict.NO_INTERNET -> "Не открываются ни российские, ни зарубежные сайты."
    Verdict.UNKNOWN -> "Не хватило данных, чтобы сделать вывод."
}
