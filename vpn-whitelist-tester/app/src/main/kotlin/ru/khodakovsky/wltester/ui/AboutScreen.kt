package ru.khodakovsky.wltester.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.BuildConfig
import ru.khodakovsky.wltester.platform.XrayTunnelFactory

@Composable
fun AboutScreen() {
    val ctx = LocalContext.current
    val xray by produceState<String?>("…") { value = withContext(Dispatchers.IO) { XrayTunnelFactory(ctx).version() } }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("WL Tester", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
        Text("Тестер белых списков и VPN для мобильного интернета")
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp)) {
                Text("Разработчик: Ходаковский Александр", fontWeight = FontWeight.SemiBold)
                Text("Версия ${BuildConfig.VERSION_NAME} (сборка ${BuildConfig.VERSION_CODE})")
                Text("Ядро: ${xray ?: "не найдено"}")
            }
        }
        Block(
            "Что делает приложение",
            "• В заданное время (или постоянно) проверяет российские и зарубежные сайты напрямую через мобильную сеть: DNS → TCP → TLS → HTTP, с временем каждого этапа.\n" +
                "• Проверяет ваш VPN-сервер: доступность портов, TLS с разными SNI, ping.\n" +
                "• Поднимает собственное ядро xray (без VPN-интерфейса) и проверяет каждый профиль VLESS Reality: открываются ли через него YouTube, Telegram, WhatsApp и др.\n" +
                "• Определяет режим сети: «Норма», «Белые списки», «Частичные ограничения», «Нет интернета», и записывает моменты переключения.",
        )
        Block(
            "Как пользоваться",
            "1. В «Настройках» вставьте ссылки vless:// из подписки.\n" +
                "2. Выберите режим: окно по времени (например, 00:00–07:00) или «Постоянно» — тогда приложение следит за VPN и включает подробные проверки, когда VPN пропадает.\n" +
                "3. Выберите частоту (профиль «Экономия батареи» — для всей ночи).\n" +
                "4. На «Статусе» уберите все предупреждения: отключите оптимизацию батареи, разрешите уведомления.\n" +
                "5. Выключите Wi-Fi и включите мониторинг.\n" +
                "6. Утром откройте «События» → «Отчёт»: там хронология, время включения/отключения белых списков и выводы. «Сохранить ZIP» — полный журнал с CSV для Excel.",
        )
        Block(
            "NekoBox и другие VPN",
            "Тестер не должен ходить через ваш VPN-клиент. Если в NekoBox включён режим «только выбранные приложения» и тестер в список не добавлен — всё в порядке. " +
                "Если трафик тестера всё же идёт через VPN, приложение покажет предупреждение и отметит это в журнале.",
        )
        Block(
            "Как читать результаты",
            "• TCP: таймаут — пакеты до IP не доходят (блокировка по IP / белый список IP).\n" +
                "• TLS: сброс — соединение есть, но рукопожатие режется (фильтрация по SNI).\n" +
                "• Загрузка зависла на ~16–20 КБ — «заморозка» ТСПУ для зарубежных IP.\n" +
                "• «TLS ок, сертификат не прошёл проверку» — сервер ответил (это доступность), но сертификат не совпал с именем: так бывает при «чужом» SNI или подмене.",
        )
        Block(
            "Конфиденциальность",
            "Все данные и ссылки хранятся только на телефоне. Приложение никуда не отправляет журнал — только если вы сами нажмёте «Поделиться».",
        )
    }
}

@Composable
private fun Block(title: String, text: String) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            Text(text, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 6.dp))
        }
    }
}
