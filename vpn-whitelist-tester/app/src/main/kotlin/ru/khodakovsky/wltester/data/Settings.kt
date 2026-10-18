package ru.khodakovsky.wltester.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import ru.khodakovsky.wltester.core.Defaults
import ru.khodakovsky.wltester.core.Target
import ru.khodakovsky.wltester.core.VlessLinks
import ru.khodakovsky.wltester.core.WlJson
import ru.khodakovsky.wltester.core.analysis.MonitorMemory
import ru.khodakovsky.wltester.core.engine.EngineConfig

@Serializable
enum class ScheduleMode(val ru: String) {
    WINDOW("По времени (окно)"),
    ALWAYS("Постоянно — следить за VPN"),
}

@Serializable
enum class Preset(val ru: String, val fullMin: Int, val watchMin: Int, val hint: String) {
    ECONOMY("Экономия батареи", 15, 30, "полная проверка раз в 15 мин, слежение за VPN раз в 30 мин"),
    BALANCED("Сбалансированный", 5, 15, "полная — раз в 5 мин, слежение — раз в 15 мин"),
    DETAILED("Подробный", 2, 5, "полная — раз в 2 мин, слежение — раз в 5 мин (заметный расход батареи)"),
    CUSTOM("Свой", 5, 15, "интервалы задаются вручную"),
}

@Serializable
data class AppSettings(
    val monitoringEnabled: Boolean = false,
    val scheduleMode: ScheduleMode = ScheduleMode.WINDOW,
    /** Минуты от полуночи. */
    val windowStart: Int = 0,
    val windowEnd: Int = 7 * 60,
    val preset: Preset = Preset.BALANCED,
    val customFullMin: Int = 5,
    val customWatchMin: Int = 15,
    /** Сколько неудачных быстрых проверок подряд считать «VPN пропал». */
    val failThreshold: Int = 2,
    val requireCellular: Boolean = true,
    val autoWifiOff: Boolean = true,
    val reliableAlarms: Boolean = true,
    val notifyEvents: Boolean = true,
    val vlessLinks: String = "",
    val ruSites: String = Defaults.RU_SITES,
    val foreignSites: String = Defaults.FOREIGN_SITES,
    val vpnSites: String = Defaults.VPN_SITES,
    val downloadEnabled: Boolean = true,
    val downloadKb: Int = 128,
    val sniMatrix: Boolean = true,
    val ping: Boolean = true,
    val dnsUdp: Boolean = true,
    val timeoutSec: Int = 6,
    val retentionDays: Int = 30,
) {
    val fullIntervalMin: Int get() = (if (preset == Preset.CUSTOM) customFullMin else preset.fullMin).coerceIn(1, 720)
    val watchIntervalMin: Int get() = (if (preset == Preset.CUSTOM) customWatchMin else preset.watchMin).coerceIn(1, 720)

    fun engineConfig(): EngineConfig = EngineConfig(
        ruTargets = Target.parseList(ruSites),
        foreignTargets = Target.parseList(foreignSites),
        vpnTargets = Target.parseList(vpnSites),
        profiles = VlessLinks.parseAll(vlessLinks).first,
        connectTimeoutMs = timeoutSec.coerceIn(2, 30) * 1000,
        readTimeoutMs = (timeoutSec.coerceIn(2, 30) + 2) * 1000,
        requireCellular = requireCellular,
        downloadEnabled = downloadEnabled,
        downloadBytes = downloadKb.coerceIn(32, 4096) * 1024L,
        sniMatrix = sniMatrix,
        ping = ping,
        dnsUdp = dnsUdp,
    )

    /** Примерный трафик одной полной проверки, КБ. */
    fun estimateFullKb(): Int {
        val c = engineConfig()
        val sites = (c.ruTargets.size + c.foreignTargets.size) * 12 + c.vpnTargets.size * c.profiles.size * 12
        val dl = if (downloadEnabled) downloadKb * (2 + c.profiles.size) else 0
        return sites + dl + 20
    }
}

@Serializable
enum class Phase { WATCH, ALERT }

/** Состояние мониторинга между запусками (переживает перезапуск процесса). */
@Serializable
data class MonitorState(
    val phase: Phase = Phase.WATCH,
    val failStreak: Int = 0,
    val okStreak: Int = 0,
    val lastRunAt: Long = 0,
    val nextRunAt: Long = 0,
    val memory: MonitorMemory = MonitorMemory(),
    val lastWasSkipped: Boolean = false,
    val windowActive: Boolean = false,
)

class SettingsStore private constructor(ctx: Context) {
    private val prefs = ctx.getSharedPreferences("wl", Context.MODE_PRIVATE)
    private val _settings = MutableStateFlow(load())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()

    private fun load(): AppSettings = prefs.getString("settings", null)?.let {
        runCatching { WlJson.decodeFromString(AppSettings.serializer(), it) }.getOrNull()
    } ?: AppSettings()

    fun get(): AppSettings = _settings.value

    @Synchronized
    fun update(f: (AppSettings) -> AppSettings): AppSettings {
        val n = f(_settings.value)
        _settings.value = n
        prefs.edit().putString("settings", WlJson.encodeToString(AppSettings.serializer(), n)).apply()
        return n
    }

    @Synchronized
    fun state(): MonitorState = prefs.getString("state", null)?.let {
        runCatching { WlJson.decodeFromString(MonitorState.serializer(), it) }.getOrNull()
    } ?: MonitorState()

    @Synchronized
    fun updateState(f: (MonitorState) -> MonitorState): MonitorState {
        val n = f(state())
        prefs.edit().putString("state", WlJson.encodeToString(MonitorState.serializer(), n)).commit()
        _stateFlow.value = n
        return n
    }

    private val _stateFlow = MutableStateFlow(state())
    val stateFlow: StateFlow<MonitorState> = _stateFlow.asStateFlow()

    companion object {
        @Volatile private var instance: SettingsStore? = null
        fun get(ctx: Context): SettingsStore = instance ?: synchronized(this) {
            instance ?: SettingsStore(ctx.applicationContext).also { instance = it }
        }
    }
}
