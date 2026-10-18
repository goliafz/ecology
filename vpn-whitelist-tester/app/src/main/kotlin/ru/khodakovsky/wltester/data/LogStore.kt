package ru.khodakovsky.wltester.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import ru.khodakovsky.wltester.BuildConfig
import ru.khodakovsky.wltester.core.CycleRecord
import ru.khodakovsky.wltester.core.Event
import ru.khodakovsky.wltester.core.EventType
import ru.khodakovsky.wltester.core.WlJson
import ru.khodakovsky.wltester.core.analysis.Csv
import ru.khodakovsky.wltester.core.analysis.ReportBuilder
import java.io.File
import java.io.OutputStream
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * Журнал: один JSON на строку. Циклы — по файлу на день, события — в одном файле.
 * Формат удобен и для приложения, и для разбора на компьютере.
 */
class LogStore private constructor(private val ctx: Context) {
    private val dir = File(ctx.filesDir, "logs").apply { mkdirs() }
    private val eventsFile = File(dir, "events.jsonl")
    private val dayFmt = DateTimeFormatter.ofPattern("yyyy-MM-dd")
    private val zone: ZoneId get() = ZoneId.systemDefault()

    /** Увеличивается при каждой записи — UI по нему перечитывает данные. */
    private val _version = MutableStateFlow(0L)
    val version: StateFlow<Long> = _version.asStateFlow()

    private fun dayFile(date: LocalDate) = File(dir, "cycles-${dayFmt.format(date)}.jsonl")

    @Synchronized
    fun appendCycle(c: CycleRecord) {
        val date = Instant.ofEpochMilli(c.startedAt).atZone(zone).toLocalDate()
        dayFile(date).appendText(WlJson.encodeToString(CycleRecord.serializer(), c) + "\n")
        _version.value++
    }

    @Synchronized
    fun appendEvents(events: List<Event>) {
        if (events.isEmpty()) return
        eventsFile.appendText(events.joinToString("") { WlJson.encodeToString(Event.serializer(), it) + "\n" })
        _version.value++
    }

    fun event(type: EventType, title: String, details: String? = null) =
        appendEvents(listOf(Event(System.currentTimeMillis(), type, title, details)))

    fun days(): List<LocalDate> = dir.listFiles().orEmpty()
        .mapNotNull { f -> Regex("cycles-(\\d{4}-\\d{2}-\\d{2})\\.jsonl").matchEntire(f.name)?.groupValues?.get(1) }
        .mapNotNull { runCatching { LocalDate.parse(it, dayFmt) }.getOrNull() }
        .sortedDescending()

    fun cycles(date: LocalDate): List<CycleRecord> = readLines(dayFile(date)) {
        WlJson.decodeFromString(CycleRecord.serializer(), it)
    }

    /** Все циклы за последние [days] дней (по возрастанию времени). */
    fun cycles(days: Int = 3650): List<CycleRecord> {
        val from = LocalDate.now(zone).minusDays(days.toLong())
        return days().filter { !it.isBefore(from) }.sorted().flatMap { cycles(it) }
    }

    fun lastCycle(): CycleRecord? = days().firstOrNull()?.let { d -> cycles(d).lastOrNull() }

    fun events(): List<Event> = readLines(eventsFile) { WlJson.decodeFromString(Event.serializer(), it) }

    @Synchronized
    fun cleanup(retentionDays: Int) {
        val keepFrom = LocalDate.now(zone).minusDays(retentionDays.toLong().coerceAtLeast(1))
        days().filter { it.isBefore(keepFrom) }.forEach { dayFile(it).delete() }
        val cutoff = keepFrom.atStartOfDay(zone).toInstant().toEpochMilli()
        val ev = events()
        if (ev.any { it.ts < cutoff }) {
            eventsFile.writeText(ev.filter { it.ts >= cutoff }.joinToString("") { WlJson.encodeToString(Event.serializer(), it) + "\n" })
        }
    }

    @Synchronized
    fun clear() {
        dir.listFiles().orEmpty().forEach { it.delete() }
        _version.value++
    }

    fun report(days: Int = 3650): String =
        ReportBuilder(zone).build(cycles(days), events(), "WL Tester ${BuildConfig.VERSION_NAME}")

    /** ZIP: отчёт, CSV для таблиц и исходные JSONL. */
    fun exportZip(out: OutputStream, days: Int = 3650) {
        val cycles = cycles(days)
        val events = events()
        val csv = Csv(zone)
        ZipOutputStream(out).use { zip ->
            fun put(name: String, text: String) {
                zip.putNextEntry(ZipEntry(name))
                zip.write(text.toByteArray(Charsets.UTF_8))
                zip.closeEntry()
            }
            put("report.txt", ReportBuilder(zone).build(cycles, events, "WL Tester ${BuildConfig.VERSION_NAME}"))
            put("cycles.csv", csv.cycles(cycles))
            put("checks.csv", csv.checks(cycles))
            put("events.csv", csv.events(events))
            put(
                "README.txt",
                """
                Журнал WL Tester
                report.txt: отчёт с выводами, его удобно читать первым.
                cycles.csv: по строке на каждую проверку (итог, сеть, оператор).
                checks.csv: по строке на каждый сайт в каждой проверке (этапы DNS, TCP, TLS, HTTP, время, ошибка).
                events.csv: события (белые списки включили или отключили, VPN пропал или вернулся, сменилась сеть).
                raw/*.jsonl: исходные данные, по одному JSON на строку.
                CSV открываются в Excel: разделитель точка с запятой, кодировка UTF-8.
                """.trimIndent(),
            )
            dir.listFiles().orEmpty().filter { it.name.endsWith(".jsonl") }.forEach { f ->
                zip.putNextEntry(ZipEntry("raw/${f.name}"))
                f.inputStream().use { it.copyTo(zip) }
                zip.closeEntry()
            }
        }
    }

    fun exportFile(): File {
        val d = File(ctx.cacheDir, "export").apply { mkdirs() }
        d.listFiles().orEmpty().forEach { it.delete() }
        val stamp = DateTimeFormatter.ofPattern("yyyyMMdd-HHmm").withZone(zone).format(Instant.now())
        val f = File(d, "wltester-$stamp.zip")
        f.outputStream().use { exportZip(it) }
        return f
    }

    private fun <T> readLines(f: File, parse: (String) -> T): List<T> {
        if (!f.exists()) return emptyList()
        return f.readLines().mapNotNull { line -> if (line.isBlank()) null else runCatching { parse(line) }.getOrNull() }
    }

    companion object {
        @Volatile private var instance: LogStore? = null
        fun get(ctx: Context): LogStore = instance ?: synchronized(this) {
            instance ?: LogStore(ctx.applicationContext).also { instance = it }
        }
    }
}
