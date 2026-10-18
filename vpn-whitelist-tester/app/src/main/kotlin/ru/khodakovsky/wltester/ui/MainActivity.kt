package ru.khodakovsky.wltester.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { WlTheme { AppRoot() } }
    }
}

private data class Tab(val title: String, val icon: ImageVector)

private val tabs = listOf(
    Tab("Главная", Icons.Filled.Home),
    Tab("Журнал", Icons.AutoMirrored.Filled.List),
    Tab("Настройки", Icons.Filled.Settings),
    Tab("Помощь", Icons.Filled.Info),
)

const val TAB_SETTINGS = 2
const val TAB_HELP = 3

@Composable
fun AppRoot() {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    // Подписи меню не растут вместе с системным размером шрифта, иначе «Настройки» не влезают.
    val labelSize = with(LocalDensity.current) { 12.dp.toSp() }
    Scaffold(
        bottomBar = {
            NavigationBar {
                tabs.forEachIndexed { i, t ->
                    NavigationBarItem(
                        selected = tab == i,
                        onClick = { tab = i },
                        icon = { Icon(t.icon, contentDescription = t.title) },
                        label = { Text(t.title, maxLines = 1, softWrap = false, fontSize = labelSize) },
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (tab) {
                0 -> HomeScreen(onOpenSettings = { tab = TAB_SETTINGS }, onOpenHelp = { tab = TAB_HELP })
                1 -> JournalScreen()
                2 -> SettingsScreen()
                else -> HelpScreen()
            }
        }
    }
}
