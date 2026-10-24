// Operation journal: a bounded list in chrome.storage.local ("log"), newest first.
// Contains titles and statuses only, never message text. Can be disabled or cleared.
import { loadSettings } from './settings.js';
import { setKey, updateKey } from './storage.js';

/**
 * @typedef {Object} LogEntry
 * @property {number} at
 * @property {'info' | 'warn' | 'error'} level
 * @property {string} message
 * @property {string} [chat] "service:id"
 * @property {string} [title]
 * @property {string} [code]
 */

/**
 * @param {'info' | 'warn' | 'error'} level
 * @param {string} message
 * @param {{ chat?: string, title?: string, code?: string }} [extra]
 */
export async function addLog(level, message, extra = {}) {
  const fn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.info;
  fn('[Chat Exporter]', message, extra.chat || '');
  try {
    const settings = await loadSettings();
    if (!settings.journal.enabled) return;
    /** @type {LogEntry} */
    const entry = { at: Date.now(), level, message, ...extra };
    await updateKey('log', (list) => [entry, ...(Array.isArray(list) ? list : [])].slice(0, settings.journal.maxEntries));
  } catch (err) {
    console.error('[Chat Exporter] journal write failed', err);
  }
}

export async function clearLog() {
  await setKey('log', []);
}
