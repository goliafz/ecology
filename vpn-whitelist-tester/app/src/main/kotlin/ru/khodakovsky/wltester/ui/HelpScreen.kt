package ru.khodakovsky.wltester.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import ru.khodakovsky.wltester.BuildConfig
import ru.khodakovsky.wltester.platform.XrayTunnelFactory

@Composable
fun HelpScreen() {
    val ctx = LocalContext.current
    val core by produceState<String?>("…") { value = withContext(Dispatchers.IO) { XrayTunnelFactory(ctx).version() } }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text("Помощь", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
        Text("Нажмите на вопрос, чтобы прочитать ответ.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

        Topic(
            "Зачем это приложение",
            "Иногда мобильный оператор включает «белые списки». Тогда работают только разрешённые сайты, в основном российские: " +
                "Госуслуги, Яндекс, ВКонтакте, банки. Всё остальное (YouTube, Telegram, зарубежные сайты) перестаёт открываться, " +
                "и часто перестаёт работать VPN.\n\n" +
                "Приложение само, в фоне, проверяет, какие сайты открываются и работает ли ваш VPN. Всё записывается в журнал. " +
                "По журналу видно, в какое время включают ограничения, когда их снимают и что из этого работает.",
            startOpen = true,
        )
        Topic(
            "Первый запуск: что настроить (один раз)",
            "1. Откройте «Настройки» и в пункте «Ваш VPN» нажмите «Вставить». Перед этим скопируйте ссылку на VPN, " +
                "она начинается с vless://. Её можно попросить у того, кто настраивал VPN.\n\n" +
                "2. Там же выберите, когда проверять:\n" +
                "• «Ночью по расписанию»: проверки идут только в выбранные часы, например с 00:00 до 07:00;\n" +
                "• «Весь день, следить за VPN»: приложение быстро проверяет VPN, Telegram и WhatsApp, а ночью делает это чаще. " +
                "Как только VPN не ответит 2 раза подряд (обычно это значит, что включили белые списки), приложение начинает " +
                "проверять всё подробно, раз в 2 минуты. Когда VPN вернётся, оно снова переходит на быстрые проверки. " +
                "Этот режим лучше всего ловит момент, когда включают и отключают белые списки.\n\n" +
                "3. Выберите, как часто проверять. «Обычно» подходит почти всегда. На всю ночь лучше «Реже, бережём батарею».\n\n" +
                "4. Вернитесь на «Главную». Если там есть блок «Что нужно сделать», нажмите кнопку в каждом пункте и разрешите то, что просит телефон.\n\n" +
                "5. Выключите Wi-Fi, чтобы телефон был на мобильном интернете.\n\n" +
                "6. Включите переключатель «Автоматические проверки».",
        )
        Topic(
            "Как пользоваться каждый день",
            "Ничего делать не нужно: приложение проверяет само, даже когда оно закрыто. Пока идут проверки, вверху экрана висит значок WL Tester.\n\n" +
                "Чтобы узнать, что сейчас с интернетом, откройте «Главную». Там крупно написано, работает ли интернет как обычно, " +
                "включены ли белые списки и работает ли VPN.\n\n" +
                "Утром откройте «Журнал», вкладку «События и отчёт». Там видно, когда ночью включали и отключали ограничения.\n\n" +
                "Если хочется проверить прямо сейчас, нажмите «Проверить сейчас» на «Главной».",
        )
        Topic(
            "Что значат надписи и цвета",
            "Зелёный: всё хорошо.\n" +
                "• «Интернет работает как обычно»: открываются и российские, и зарубежные сайты.\n\n" +
                "Красный: ограничения или поломка.\n" +
                "• «Включены белые списки»: открываются только российские сайты из разрешённого списка.\n" +
                "• «Интернета нет»: не открывается вообще ничего.\n" +
                "• «VPN не работает»: через ваш VPN сайты не открываются.\n\n" +
                "Жёлтый: что-то среднее.\n" +
                "• «Часть сайтов не открывается»: сеть работает с перебоями, или ограничения только включаются.\n" +
                "• «Последняя проверка не прошла»: например, был включён Wi-Fi или не было сигнала.",
        )
        Topic(
            "Почему просит выключить Wi-Fi",
            "Белые списки включает мобильный оператор, и действуют они только на мобильном интернете. " +
                "Через домашний Wi-Fi всё работает как обычно, поэтому проверять через него нет смысла. " +
                "Пока подключён Wi-Fi, приложение пропускает проверки и напоминает его выключить.",
        )
        Topic(
            "Проверки не идут ночью или идут с опозданием",
            "Android экономит батарею и «усыпляет» приложения в фоне. Что помогает:\n" +
                "• на «Главной» выполните все пункты из блока «Что нужно сделать»;\n" +
                "• в «Настройках» включите «Будить телефон точно по времени»;\n" +
                "• на Xiaomi, Huawei, Honor и Samsung зайдите в настройки телефона, раздел «Приложения», найдите WL Tester " +
                "и разрешите автозапуск и работу в фоне без ограничений.",
        )
        Topic(
            "VPN в NekoBox работает, а приложение пишет, что нет",
            "Приложение проверяет VPN своими силами и не зависит от NekoBox. Посмотрите «Журнал»: откройте последнюю проверку " +
                "и найдите строку «Работает ли VPN». Если там написано «работает», а не открываются только Google и YouTube, " +
                "это особенность проверки, на обычную работу VPN она не влияет.\n\n" +
                "Если NekoBox включён, но приложение пишет, что ходит в интернет через VPN, уберите WL Tester " +
                "из списка приложений, которые идут через VPN в NekoBox. Иначе проверки без VPN получаются неточными.",
        )
        Topic(
            "Telegram не работает, а WhatsApp работает: виноват сигнал?",
            "Приложение проверяет это само. Telegram и WhatsApp через VPN проверяются при каждой проверке, даже быстрой. При подробной проверке приложение ещё смотрит:\n" +
                "• открываются ли Telegram и WhatsApp через ваш VPN, причём у Telegram проверяются его настоящие серверы, а не только сайт;\n" +
                "• какой сейчас сигнал: уровень, шум, к какой вышке подключён телефон;\n" +
                "• как быстро и без потерь отвечает Яндекс (это показывает качество самой связи).\n\n" +
                "Если сигнал хороший, остальное через VPN открывается, а Telegram нет, значит дело не в связи: " +
                "Telegram не может достучаться до своих серверов через ваш VPN-сервер или у самого Telegram сбой. " +
                "Если же в это время сигнал слабый и пакеты теряются, скорее всего, виновата связь.\n\n" +
                "Готовый вывод есть в отчёте («Журнал», вкладка «События и отчёт», «Показать отчёт»). " +
                "Лучше поставить приложение на оба телефона: тогда видно, в чём разница между ними.",
        )
        Topic(
            "Сколько тратится батареи и интернета",
            "Зависит от частоты. В «Настройках», в пункте «Как часто», написано, сколько мобильного интернета уйдёт за час. " +
                "Батарею больше всего тратит режим «Часто». На ночь подходит «Реже, бережём батарею».",
        )
        Topic(
            "Как отправить журнал разработчику",
            "Откройте «Журнал», вкладку «События и отчёт», и нажмите «Отправить». Выберите Telegram, почту или другое приложение. " +
                "Отправится один файл, внутри отчёт и подробные таблицы.\n\n" +
                "Если во время белых списков Telegram не работает, нажмите «Сохранить файл» и отправьте его позже.",
        )
        Topic(
            "Безопасно ли это",
            "Ссылки на VPN и журнал хранятся только на этом телефоне. Приложение никуда их не отправляет. " +
                "Файл с журналом уходит, только если вы сами нажмёте «Отправить».",
        )

        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text("О приложении", style = MaterialTheme.typography.titleMedium)
                Text("WL Tester, версия ${BuildConfig.VERSION_NAME}")
                Text("Разработчик: Ходаковский Александр")
                Text(
                    "Модуль проверки VPN: ${core ?: "не найден"}",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun Topic(title: String, text: String, startOpen: Boolean = false) {
    var open by rememberSaveable(title) { mutableStateOf(startOpen) }
    Card(Modifier.fillMaxWidth().clickable { open = !open }) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                Spacer(Modifier.width(8.dp))
                Text(if (open) "▲" else "▼", color = Color.Gray)
            }
            if (open) Text(text, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 8.dp))
        }
    }
}
