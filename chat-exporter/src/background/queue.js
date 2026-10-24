// Persistent, sequential export queue (one chat at a time).
//
// State lives in chrome.storage.local ("queue") so a run survives a service-worker restart:
// jobs that were "running" when the worker died are put back to "pending" on startup.
// Loop protection: per-job attempt limit, rate-limit pauses, and long waits are handed to
// chrome.alarms instead of keeping the worker busy.
import { chatKey } from '../adapters/registry.js';
import { backoffDelay, ErrorCode, serializeError, sleepUnlessCancelled } from '../core/util.js';
import { exportChat } from './exporter.js';
import { addLog } from './log.js';
import { loadSettings } from './settings.js';
import { getKey, setKey, updateIndexEntry, updateKey } from './storage.js';
import { cancelTabRequests, closeOpenedTabs } from './tabs.js';

export const QUEUE_ALARM = 'queue-resume';

/**
 * @typedef {Object} Job
 * @property {string} key
 * @property {import('../core/model.js').ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {import('../core/model.js').ExportFormat[]} formats
 * @property {'manual' | 'batch' | 'auto'} reason
 * @property {string | null} [expectedUpdatedAt]
 * @property {boolean} [force]
 * @property {number} [preferTabId]
 * @property {number} attempts
 * @property {'pending' | 'running' | 'done' | 'failed' | 'cancelled'} status
 * @property {number} [notBefore]
 * @property {{ code: string, message: string }} [error]
 * @property {string} [outcome]
 * @property {string} [completeness]
 * @property {string[]} [warnings]
 */

/**
 * @typedef {Object} RunInfo
 * @property {string} id
 * @property {'manual' | 'batch' | 'auto'} reason
 * @property {number} startedAt
 * @property {number | null} finishedAt
 * @property {boolean} cancelled
 */

/** @typedef {{ run: RunInfo | null, jobs: Job[] }} QueueState */

let processing = false;
let cancelRequested = false;
// Aborted by "Отменить": stops the running export, pauses and a backup run still reading lists.
let cancelController = new AbortController();

/** @type {string | null} what a backup run is doing before its jobs are queued */
let listing = null;

/** @param {string | null} text */
export function setListing(text) {
  listing = text;
  broadcastState();
}

/** Signal of the next "Отменить" (a new one after each cancel). */
export function cancellationSignal() {
  return cancelController.signal;
}
/** @type {string} */
let currentProgress = '';

/** @returns {Promise<QueueState>} */
export async function getQueue() {
  const q = await getKey('queue');
  return q && Array.isArray(q.jobs) ? q : { run: null, jobs: [] };
}

/** @param {(q: QueueState) => QueueState | void} fn */
function updateQueue(fn) {
  return updateKey('queue', (q) => {
    const state = q && Array.isArray(q.jobs) ? q : { run: null, jobs: [] };
    return fn(state) || state;
  });
}

export function broadcastState() {
  chrome.runtime.sendMessage({ type: 'state-changed' }).catch(() => {});
}

/** @param {QueueState} q */
export function summarize(q) {
  const count = (/** @type {Job['status']} */ s) => q.jobs.filter((j) => j.status === s).length;
  return {
    run: q.run,
    total: q.jobs.length,
    pending: count('pending'),
    running: count('running'),
    done: count('done'),
    failed: count('failed'),
    cancelled: count('cancelled'),
    incomplete: q.jobs.filter((j) => j.completeness === 'incomplete').length,
    warnings: q.jobs.filter((j) => j.completeness === 'complete_with_warnings').length,
    unchanged: q.jobs.filter((j) => j.outcome === 'unchanged').length,
    current: q.jobs.find((j) => j.status === 'running') || null,
    progress: currentProgress,
    processing,
    listing,
  };
}

/**
 * Adds jobs. A new run starts when the previous one has finished.
 * @param {Array<Omit<Job, 'key' | 'attempts' | 'status'>>} items
 * @param {'manual' | 'batch' | 'auto'} reason
 * @returns {Promise<number>} number of jobs added
 */
export async function enqueue(items, reason) {
  let added = 0;
  await updateQueue((q) => {
    const active = q.jobs.some((j) => j.status === 'pending' || j.status === 'running');
    if (!active) {
      q.run = { id: crypto.randomUUID(), reason, startedAt: Date.now(), finishedAt: null, cancelled: false };
      q.jobs = [];
    }
    for (const it of items) {
      const key = chatKey(it.service, it.id);
      const existing = q.jobs.find((j) => j.key === key && (j.status === 'pending' || j.status === 'running'));
      if (existing) {
        // Merge formats; a manual request upgrades the job (force, active tab).
        existing.formats = [...new Set([...existing.formats, ...it.formats])];
        if (it.force) existing.force = true;
        if (it.preferTabId !== undefined) existing.preferTabId = it.preferTabId;
        continue;
      }
      q.jobs.push({ ...it, key, attempts: 0, status: 'pending' });
      added += 1;
    }
  });
  broadcastState();
  processQueue().catch((err) => addLog('error', `Сбой очереди: ${err instanceof Error ? err.message : err}`));
  return added;
}

export async function cancelQueue() {
  cancelRequested = true;
  cancelController.abort();
  cancelController = new AbortController();
  await chrome.alarms.clear(QUEUE_ALARM);
  await updateQueue((q) => {
    for (const j of q.jobs) {
      if (j.status === 'pending' || j.status === 'running') {
        j.status = 'cancelled';
        j.outcome = undefined;
      }
    }
    if (q.run) q.run.cancelled = true;
  });
  setProgress('');
  await cancelTabRequests().catch(() => {});
  await updateBadge().catch(() => {});
  await addLog('info', 'Выгрузка отменена пользователем');
  if (!processing) await finishCancelledRun(await loadSettings());
}

/** Puts jobs interrupted by a worker restart back into the queue and resumes. */
export async function resumeQueue() {
  let resumable = false;
  await updateQueue((q) => {
    for (const j of q.jobs) {
      if (j.status === 'running') j.status = 'pending';
    }
    resumable = q.jobs.some((j) => j.status === 'pending');
  });
  if (resumable) processQueue().catch((err) => addLog('error', `Сбой очереди: ${err instanceof Error ? err.message : err}`));
}

/** @param {string} text */
function setProgress(text) {
  currentProgress = text;
  broadcastState();
}

async function updateBadge() {
  const q = await getQueue();
  const s = summarize(q);
  if (s.pending + s.running > 0) {
    await chrome.action.setBadgeBackgroundColor({ color: '#2457c5' });
    await chrome.action.setBadgeText({ text: `${s.done + s.failed}/${s.total}` });
  } else if (s.failed > 0 || s.incomplete > 0) {
    await chrome.action.setBadgeBackgroundColor({ color: '#c53a24' });
    await chrome.action.setBadgeText({ text: '!' });
  } else {
    const schedule = (await getKey('schedule')) || {};
    await chrome.action.setBadgeText({ text: schedule.missedPending ? '!' : '' });
  }
}

export async function processQueue() {
  if (processing) return;
  processing = true;
  cancelRequested = false;
  // Calling an extension API resets the service worker idle timer (Chrome 110+).
  const keepAlive = setInterval(() => {
    chrome.runtime.getPlatformInfo().catch(() => {});
  }, 20_000);
  try {
    for (;;) {
      const settings = await loadSettings();
      const q = await getQueue();
      if (cancelRequested) break;
      const now = Date.now();
      const job = q.jobs.find((j) => j.status === 'pending' && (!j.notBefore || j.notBefore <= now));
      if (!job) {
        const waiting = q.jobs.filter((j) => j.status === 'pending');
        if (waiting.length === 0) {
          await finishRun(settings);
          break;
        }
        const wake = Math.min(...waiting.map((j) => j.notBefore || now));
        if (wake - now <= 90_000) {
          setProgress(`Пауза перед повтором: ${Math.ceil((wake - now) / 1000)} с`);
          await sleepUnlessCancelled(Math.max(1000, wake - now), cancellationSignal());
          continue;
        }
        await chrome.alarms.create(QUEUE_ALARM, { when: wake });
        setProgress(`Повтор запланирован на ${new Date(wake).toLocaleTimeString('ru-RU')}`);
        break;
      }

      await updateQueue((state) => {
        const j = state.jobs.find((x) => x.key === job.key && x.status === 'pending');
        if (j) {
          j.status = 'running';
          j.attempts += 1;
        }
      });
      await updateBadge();
      setProgress(`${job.title || job.id}: начало`);

      const maxAttempts = job.reason === 'auto' ? settings.auto.maxAttempts : Math.min(2, settings.auto.maxAttempts);
      const signal = cancellationSignal();
      try {
        const result = await exportChat(job, settings, (text) => { if (!signal.aborted) setProgress(`${job.title || job.id}: ${text}`); }, signal);
        await updateQueue((state) => {
          const j = state.jobs.find((x) => x.key === job.key && x.status === 'running');
          if (j) {
            j.status = 'done';
            j.outcome = result.outcome;
            j.completeness = result.status;
            j.warnings = result.warnings.slice(0, 20);
            j.title = result.title || j.title;
          }
        });
        await setKey('lastResult', { at: Date.now(), key: job.key, ...result });
      } catch (err) {
        const e = serializeError(err);
        if (e.code === ErrorCode.CANCELLED) break; // cancelQueue already marked the job
        const transient = /** @type {any} */ (err)?.transient === true || e.code === ErrorCode.INCOMPLETE;
        const attempts = job.attempts + 1;
        const retry = transient && attempts < maxAttempts && !cancelRequested;
        const delay = e.retryAfterMs || backoffDelay(attempts, 30_000, 15 * 60_000);
        await updateQueue((state) => {
          const j = state.jobs.find((x) => x.key === job.key && x.status === 'running');
          if (!j) return;
          j.error = { code: e.code, message: e.message };
          if (retry) {
            j.status = 'pending';
            j.notBefore = Date.now() + delay;
          } else {
            j.status = 'failed';
          }
          // A rate limit pauses the whole queue, not just this job.
          if (e.code === ErrorCode.RATE_LIMITED) {
            for (const other of state.jobs) {
              if (other.status === 'pending') other.notBefore = Math.max(other.notBefore || 0, Date.now() + delay);
            }
          }
        });
        if (!retry) {
          await updateIndexEntry(job.key, (entry) => ({
            ...(entry || { service: job.service, id: job.id, title: job.title, updatedAt: job.expectedUpdatedAt || null }),
            failures: transient ? (entry?.failures || 0) + 1 : settings.auto.maxAttempts,
            lastError: { code: e.code, message: e.message, at: Date.now() },
            nextRetryAt: transient ? Date.now() + backoffDelay(entry?.failures || 0, 3600_000, 24 * 3600_000) : undefined,
          }));
        }
        await addLog(retry ? 'warn' : 'error', `${retry ? 'Повтор позже' : 'Ошибка'}: ${e.message}`, { chat: job.key, title: job.title, code: e.code });
        await setKey('lastResult', { at: Date.now(), key: job.key, outcome: 'error', error: e, title: job.title });
      }
      await updateBadge();
      broadcastState();

      const remaining = (await getQueue()).jobs.some((j) => j.status === 'pending');
      if (remaining && !cancelRequested) {
        await sleepUnlessCancelled(settings.auto.delayMs + Math.round(Math.random() * 1000), cancellationSignal());
      }
    }
    if (cancelRequested) await finishCancelledRun(await loadSettings());
  } finally {
    clearInterval(keepAlive);
    processing = false;
    currentProgress = '';
    await updateBadge().catch(() => {});
    broadcastState();
  }
}

/** @param {import('./settings.js').Settings} settings */
async function finishCancelledRun(settings) {
  let backup = false;
  await updateQueue((q) => {
    if (q.run && !q.run.finishedAt) {
      q.run.finishedAt = Date.now();
      backup = q.run.reason === 'auto';
    }
  });
  // A cancelled backup did not save its chats: the next run lists as far back as this one did.
  await updateKey('schedule', (sch) => ({
    ...(sch || {}),
    ...(backup && sch?.previousRunStartedAt !== undefined ? { lastRunStartedAt: sch.previousRunStartedAt } : {}),
    lastRunFinishedAt: Date.now(),
    lastRunSummary: 'Отменено пользователем',
  }));
  if (settings.auto.closeOpenedTabs) await closeOpenedTabs();
}

/** @param {import('./settings.js').Settings} settings */
async function finishRun(settings) {
  /** @type {QueueState | null} */
  let finished = null;
  await updateQueue((q) => {
    if (q.run && !q.run.finishedAt) {
      q.run.finishedAt = Date.now();
      finished = q;
    }
  });
  if (!finished) return;
  const q = /** @type {QueueState} */ (finished);
  const s = summarize(q);
  const text = `Готово: сохранено ${s.done - s.unchanged}, без изменений ${s.unchanged}, ошибок ${s.failed}, неполных ${s.incomplete}`;
  await addLog(s.failed || s.incomplete ? 'warn' : 'info', `${q.run?.reason === 'auto' ? 'Автоматическое резервное копирование' : 'Экспорт'}. ${text}`);
  await updateKey('schedule', (sch) => ({ ...(sch || {}), lastRunFinishedAt: Date.now(), lastRunSummary: text }));
  if (settings.auto.closeOpenedTabs) await closeOpenedTabs();
}
