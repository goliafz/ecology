// Background service worker (Manifest V3). Entry point: event listeners and message router.
// All listeners are registered synchronously at the top level, as MV3 requires, so events
// that wake the worker are delivered.
import { chatKey, serviceFromUrl, SERVICES } from '../adapters/registry.js';
import { serializeError } from '../core/util.js';
import { getCachedList, listChats } from './changes.js';
import { addLog, clearLog } from './log.js';
import { isDebuggerAvailable, printMessageHandlers } from './pdf.js';
import { broadcastState, cancelQueue, enqueue, getQueue, processQueue, QUEUE_ALARM, resumeQueue, summarize } from './queue.js';
import { BACKUP_ALARM, CATCHUP_ALARM, checkMissedRun, getSchedule, onScheduleAlarm, runBackup, syncSchedule } from './scheduler.js';
import { loadSettings, saveSettings, DEFAULT_SETTINGS } from './settings.js';
import { getIndex, getKey, setKey, updateIndexEntry, updateKey } from './storage.js';

// ---- Lifecycle ----------------------------------------------------------------------------

chrome.runtime.onInstalled.addListener(async (details) => {
  if (details.reason === 'install') {
    await setKey('settings', await loadSettings());
    await addLog('info', 'Расширение установлено');
  }
  await init();
});

chrome.runtime.onStartup.addListener(() => {
  init().catch((err) => console.error(err));
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === QUEUE_ALARM) {
    processQueue().catch((err) => addLog('error', `Сбой очереди: ${err instanceof Error ? err.message : err}`));
  } else if (alarm.name === BACKUP_ALARM || alarm.name === CATCHUP_ALARM) {
    onScheduleAlarm(alarm).catch((err) => addLog('error', `Сбой расписания: ${err instanceof Error ? err.message : err}`));
  }
});

let initialized = false;
async function init() {
  if (initialized) return;
  initialized = true;
  await resumeQueue();
  await checkMissedRun();
}

// The worker also starts for messages and alarms; make sure state is restored then too.
init().catch((err) => console.error('[Chat Exporter] init failed', err));

// Handles for the DevTools console of the service worker (diagnostics and e2e tests).
/** @type {any} */ (globalThis).chatExporterDebug = { checkMissedRun, runBackup, syncSchedule, processQueue };

// ---- Message router -----------------------------------------------------------------------

/** @type {Record<string, (msg: any, sender: chrome.runtime.MessageSender) => Promise<any> | any>} */
const handlers = {
  async getState() {
    const [settings, queue, schedule, lastResult, tab] = await Promise.all([
      loadSettings(), getQueue(), getSchedule(), getKey('lastResult'), activeTabInfo(),
    ]);
    return { settings, queue: summarize(queue), jobs: queue.jobs.slice(-50), schedule, lastResult, tab, debuggerAvailable: isDebuggerAvailable() };
  },

  async exportCurrent(msg) {
    const tab = await activeTabInfo();
    if (!tab.service) throw Object.assign(new Error('Откройте чат ChatGPT или Claude в активной вкладке'), { code: 'NOT_A_CHAT' });
    if (!tab.chatId) throw Object.assign(new Error('В активной вкладке не открыт конкретный чат'), { code: 'NOT_A_CHAT' });
    const settings = await loadSettings();
    const added = await enqueue([{
      service: tab.service,
      id: tab.chatId,
      title: tab.title || tab.chatId,
      formats: Array.isArray(msg.formats) && msg.formats.length ? msg.formats : settings.formats,
      reason: 'manual',
      force: true,
      preferTabId: tab.tabId,
    }], 'manual');
    return { added };
  },

  async exportSelected(msg) {
    const settings = await loadSettings();
    const items = Array.isArray(msg.items) ? msg.items : [];
    const added = await enqueue(items.map((/** @type {any} */ it) => ({
      service: it.service,
      id: it.id,
      title: it.title || it.id,
      formats: Array.isArray(msg.formats) && msg.formats.length ? msg.formats : settings.formats,
      reason: 'batch',
      force: Boolean(msg.force),
      expectedUpdatedAt: it.updatedAt || null,
    })), 'batch');
    return { added };
  },

  async listChats(msg) {
    const settings = await loadSettings();
    const service = /** @type {import('../core/model.js').ServiceId} */ (msg.service);
    if (!SERVICES[service]) throw new Error('Неизвестный сервис');
    const result = msg.cached ? await getCachedList(service) : await listChats(service, settings, { allowOpen: Boolean(msg.allowOpen) });
    const index = await getIndex();
    const items = (result?.items || []).map((/** @type {any} */ it) => {
      const entry = index[chatKey(service, it.id)];
      return {
        ...it,
        excluded: Boolean(settings.excluded[chatKey(service, it.id)]),
        lastExport: entry?.lastExport ? { at: entry.lastExport.at, status: entry.lastExport.status, updatedAt: entry.lastExport.updatedAt, formats: entry.lastExport.formats } : null,
        lastError: entry?.lastError || null,
        dirty: Boolean(entry?.dirty),
      };
    });
    return { items, complete: Boolean(result?.complete), fetchedAt: result?.fetchedAt || Date.now(), warnings: result?.warnings || [] };
  },

  async saveSettings(msg) {
    const saved = await saveSettings(msg.settings);
    await syncSchedule();
    broadcastState();
    return saved;
  },

  async setAuto(msg) {
    const settings = await loadSettings();
    const saved = await saveSettings({ ...settings, auto: { ...settings.auto, enabled: Boolean(msg.enabled) } });
    await syncSchedule();
    await addLog('info', saved.auto.enabled ? `Автоматический режим включён (${saved.auto.time})` : 'Автоматический режим выключен');
    broadcastState();
    return saved;
  },

  async setExcluded(msg) {
    const settings = await loadSettings();
    const excluded = { ...settings.excluded };
    if (msg.excluded) excluded[msg.key] = { title: msg.title || msg.key, service: msg.service || msg.key.split(':')[0] };
    else delete excluded[msg.key];
    return saveSettings({ ...settings, excluded });
  },

  async runBackupNow() {
    return runBackup('manual');
  },

  async runMissed() {
    return runBackup('catch-up');
  },

  async dismissMissed() {
    await updateKey('schedule', (s) => ({ ...(s || {}), missedPending: null }));
    await chrome.action.setBadgeText({ text: '' });
    broadcastState();
    return true;
  },

  async cancel() {
    await cancelQueue();
    return true;
  },

  async getLog() {
    return (await getKey('log')) || [];
  },

  async clearLog() {
    await clearLog();
    return true;
  },

  async clearIndex() {
    await setKey('chatIndex', {});
    await addLog('info', 'Метаданные экспортов очищены: следующий запуск будет считать все чаты несохранёнными');
    return true;
  },

  async getIndexStats() {
    const index = await getIndex();
    const entries = Object.values(index);
    return {
      total: entries.length,
      exported: entries.filter((e) => e.lastExport).length,
      failed: entries.filter((e) => (e.failures || 0) > 0).length,
      missing: entries.filter((e) => e.missingSince).length,
    };
  },

  resetSettings() {
    return saveSettings(structuredClone(DEFAULT_SETTINGS));
  },

  // Content script: activity tracking (Scenario C).
  async activity(msg, sender) {
    const service = serviceFromUrl(sender.tab?.url || sender.url);
    if (!service || !msg.chatId) return false;
    const settings = await loadSettings();
    if (!settings.trackActivity) return false;
    await updateIndexEntry(chatKey(service, msg.chatId), (e) => ({
      ...(e || { service, id: msg.chatId, updatedAt: null }),
      service,
      id: msg.chatId,
      title: msg.title || e?.title || msg.chatId,
      dirty: true,
      dirtyAt: Date.now(),
    }));
    return true;
  },

  // Print page.
  'print:getHtml': (msg) => printMessageHandlers.getHtml(msg),
  'print:ready': (msg, sender) => printMessageHandlers.ready(msg, sender),
};

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  // Messages addressed to the offscreen document or broadcasts are not for the router.
  if (!msg || typeof msg.type !== 'string' || msg.target === 'offscreen' || !(msg.type in handlers)) return false;
  // Only our own pages and our content scripts may call the router.
  if (sender.id !== chrome.runtime.id) return false;
  Promise.resolve()
    .then(() => handlers[msg.type](msg, sender))
    .then((data) => sendResponse({ ok: true, data }), (err) => sendResponse({ ok: false, error: serializeError(err) }));
  return true;
});

/**
 * Info about the active tab of the last focused window.
 * @returns {Promise<{ tabId?: number, service: import('../core/model.js').ServiceId | null, chatId: string | null, title?: string, url?: string }>}
 */
async function activeTabInfo() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const service = serviceFromUrl(tab?.url);
  if (!tab || !service) return { service: null, chatId: null };
  const title = (tab.title || '').replace(/\s*[-|–]\s*(ChatGPT|Claude)\s*$/i, '').trim();
  return { tabId: tab.id, service, chatId: SERVICES[service].idFromUrl(tab.url || ''), title, url: tab.url };
}
