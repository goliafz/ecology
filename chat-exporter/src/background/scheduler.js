// Scheduled backups with chrome.alarms.
//
// Facts this design relies on (Chrome docs, chrome.alarms):
//  - Alarms "generally persist until an extension is updated", but "may be cleared when the
//    browser is restarted" -> the next run time is also stored in chrome.storage.local and the
//    alarm is re-created on every service-worker start.
//  - Alarms never fire while Chrome is closed; after sleep they "may fire significantly later".
//    -> on startup the stored time is compared with the last run and a missed run is handled
//    according to the user's choice (run automatically / ask / skip).
import { SERVICES } from '../adapters/registry.js';
import { computeNextRun, isRunMissed } from '../core/schedule-math.js';
import { findCandidates } from './changes.js';
import { addLog } from './log.js';
import { cancellationSignal, enqueue, getQueue, setListing } from './queue.js';
import { loadSettings } from './settings.js';
import { getKey, updateKey } from './storage.js';
import { abortable, ErrorCode } from '../core/util.js';
import { getTracked } from './tracked.js';

export const BACKUP_ALARM = 'auto-backup';
export const CATCHUP_ALARM = 'catch-up';
const LATE_FIRE_MS = 15 * 60_000;

/**
 * @typedef {Object} ScheduleState
 * @property {number | null} [nextRunAt]
 * @property {number | null} [lastRunStartedAt]
 * @property {number | null} [lastRunFinishedAt]
 * @property {string} [lastRunSummary]
 * @property {{ dueAt: number } | null} [missedPending]
 * @property {string} [scheduleKey] settings the stored time was computed from
 * @property {number | null} [previousRunStartedAt] start of the run before the latest one
 */

/** @returns {Promise<ScheduleState>} */
export async function getSchedule() {
  return (await getKey('schedule')) || {};
}

/**
 * (Re)creates the alarm from settings. Keeps a still-valid stored time.
 */
export async function syncSchedule() {
  const settings = await loadSettings();
  if (!settings.auto.enabled) {
    await chrome.alarms.clear(BACKUP_ALARM);
    await updateKey('schedule', (s) => ({ ...(s || {}), nextRunAt: null, missedPending: null }));
    return;
  }
  const key = `${settings.auto.time}|${settings.auto.days.join(',')}`;
  const sched = await getSchedule();
  let next = sched.nextRunAt && sched.scheduleKey === key && sched.nextRunAt > Date.now() ? sched.nextRunAt : null;
  if (!next) next = computeNextRun(new Date(), settings.auto.time, settings.auto.days).getTime();
  await updateKey('schedule', (s) => ({ ...(s || {}), nextRunAt: next, scheduleKey: key }));
  const existing = await chrome.alarms.get(BACKUP_ALARM);
  if (!existing || Math.abs(existing.scheduledTime - next) > 1000) {
    await chrome.alarms.create(BACKUP_ALARM, { when: next });
  }
}

/** Called on browser/extension start: handles a run missed while Chrome was closed. */
export async function checkMissedRun() {
  const settings = await loadSettings();
  if (!settings.auto.enabled) return;
  const sched = await getSchedule();
  if (!isRunMissed(sched, Date.now())) {
    await syncSchedule();
    return;
  }
  await handleMissed(/** @type {number} */ (sched.nextRunAt), settings.auto.missedRun);
}

/**
 * @param {number} dueAt
 * @param {'auto' | 'ask' | 'skip'} policy
 */
async function handleMissed(dueAt, policy) {
  const due = new Date(dueAt).toLocaleString('ru-RU');
  if (policy === 'auto') {
    await addLog('info', `Пропущено резервное копирование (${due}); догоняющий запуск через 2 минуты`);
    // A short delay lets the browser restore tabs and sessions first.
    await chrome.alarms.create(CATCHUP_ALARM, { delayInMinutes: 2 });
    await updateKey('schedule', (s) => ({ ...(s || {}), missedPending: { dueAt } }));
  } else if (policy === 'ask') {
    await addLog('warn', `Пропущено резервное копирование (${due}); ожидает подтверждения в окне расширения`);
    await updateKey('schedule', (s) => ({ ...(s || {}), missedPending: { dueAt } }));
    await chrome.action.setBadgeBackgroundColor({ color: '#c58a24' });
    await chrome.action.setBadgeText({ text: '!' });
  } else {
    await addLog('info', `Пропущено резервное копирование (${due}); пропущено согласно настройкам`);
    await updateKey('schedule', (s) => ({ ...(s || {}), missedPending: null, lastRunStartedAt: Date.now() }));
  }
  // Schedule the next regular run from now.
  await updateKey('schedule', (s) => ({ ...(s || {}), nextRunAt: null }));
  await syncSchedule();
}

/**
 * Alarm handler.
 * @param {chrome.alarms.Alarm} alarm
 */
export async function onScheduleAlarm(alarm) {
  const settings = await loadSettings();
  if (alarm.name === BACKUP_ALARM) {
    if (!settings.auto.enabled) return;
    // An alarm firing long after its time (Chrome was closed / computer slept) is a missed run.
    if (Date.now() - alarm.scheduledTime > LATE_FIRE_MS && settings.auto.missedRun !== 'auto') {
      await handleMissed(alarm.scheduledTime, settings.auto.missedRun);
      return;
    }
    await runBackup('scheduled');
  } else if (alarm.name === CATCHUP_ALARM) {
    if (!settings.auto.enabled) return;
    // The overdue regular alarm may already have started the run.
    if (!(await getSchedule()).missedPending) return;
    await runBackup('catch-up');
  }
}

/**
 * Detects changed chats on every enabled service and queues them.
 * @param {'scheduled' | 'catch-up' | 'manual'} trigger
 * @returns {Promise<{ queued: number, errors: string[], note?: string }>}
 */
export async function runBackup(trigger) {
  const settings = await loadSettings();
  const sched = await getSchedule();
  const q = await getQueue();
  if (q.jobs.some((j) => j.status === 'running' || j.status === 'pending')) {
    await addLog('info', 'Резервное копирование уже выполняется — новый запуск не нужен');
    return { queued: 0, errors: [] };
  }
  if (settings.auto.scope === 'tracked' && !settings.autoTrackUppercase && Object.keys(await getTracked()).length === 0) {
    const note = 'Нет помеченных чатов: отметьте «Сохранять» в окне расширения на нужных чатах';
    await updateKey('schedule', (s) => ({ ...(s || {}), lastRunStartedAt: Date.now(), lastRunFinishedAt: Date.now(), lastRunSummary: note, missedPending: null, nextRunAt: null }));
    if (settings.auto.enabled) await syncSchedule();
    await addLog('info', note);
    return { queued: 0, errors: [], note };
  }
  const cancelled = cancellationSignal();
  const previousRun = sched.lastRunStartedAt || null;
  await updateKey('schedule', (s) => ({ ...(s || {}), lastRunStartedAt: Date.now(), previousRunStartedAt: previousRun, missedPending: null, nextRunAt: null }));
  if (settings.auto.enabled) await syncSchedule();

  await addLog('info', `Резервное копирование запущено (${{ scheduled: 'по расписанию', 'catch-up': 'догоняющий запуск', manual: 'вручную' }[trigger]})`);
  /** @type {string[]} */
  const errors = [];
  /** @type {import('../core/change-detection.js').Candidate[]} */
  const all = [];
  // All lists are read before the queue starts, so services do not interfere with each other.
  for (const service of /** @type {import('../core/model.js').ServiceId[]} */ (['chatgpt', 'claude'])) {
    if (!settings.services[service]) continue;
    setListing(`${SERVICES[service].label}: чтение списка чатов…`);
    try {
      const { candidates, stats, skipped } = await abortable(findCandidates(service, settings, { lastRunStartedAt: previousRun }), cancelled);
      if (skipped) continue; // no marked chats of this service: nothing to list
      await addLog('info', `${SERVICES[service].label}: в списке ${stats.listed}, к сохранению ${candidates.length}${stats.excluded ? `, исключено ${stats.excluded}` : ''}`);
      all.push(...candidates);
    } catch (err) {
      if (/** @type {any} */ (err)?.code === ErrorCode.CANCELLED || cancelled.aborted) break;
      const message = `${SERVICES[service].label}: не удалось получить список чатов — ${err instanceof Error ? err.message : err}`;
      errors.push(message);
      await addLog('error', message, { code: /** @type {any} */ (err)?.code });
    }
  }
  setListing(null);
  if (cancelled.aborted) {
    // "Отменить" while the lists were read: nothing is queued.
    // The next run lists chats as far back as if this one had not started.
    await updateKey('schedule', (s) => ({ ...(s || {}), lastRunStartedAt: previousRun, lastRunFinishedAt: Date.now(), lastRunSummary: 'Отменено пользователем' }));
    return { queued: 0, errors, note: 'Отменено пользователем' };
  }
  const queued = all.length === 0 ? 0 : await enqueue(all.slice(0, settings.auto.maxChatsPerRun).map((c) => ({
    service: c.service,
    id: c.id,
    title: c.title,
    formats: settings.auto.formats,
    reason: /** @type {const} */ ('auto'),
    expectedUpdatedAt: c.updatedAt,
  })), 'auto');
  if (queued === 0) {
    await updateKey('schedule', (s) => ({ ...(s || {}), lastRunFinishedAt: Date.now(), lastRunSummary: errors.length ? `Ошибки: ${errors.join('; ')}` : 'Изменившихся чатов нет' }));
  }
  return { queued, errors };
}
