// Marked ("Сохранять") chats: the database of chats to watch and back up automatically, and
// the toolbar icon colour that shows whether the chat in a tab is marked.
//
// Chats are keyed by "service:id" — the id comes from the chat URL (ChatGPT /c/<uuid>,
// Claude /chat/<uuid>) and matches the id in the service's data, so renaming does not matter.
import { chatKey, serviceFromUrl, SERVICES } from '../adapters/registry.js';
import { getKey, updateKey } from './storage.js';
import { isUppercaseTitle } from '../core/auto-track.js';

/**
 * @typedef {Object} TrackedChat
 * @property {import('../core/model.js').ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {number} addedAt
 * @property {boolean} [auto] marked automatically (title in capital letters)
 */

/** @returns {Promise<Record<string, TrackedChat>>} */
export async function getTracked() {
  return (await getKey('tracked')) || {};
}

/**
 * Marks or unmarks a chat.
 * @param {{ service: import('../core/model.js').ServiceId, id: string, title?: string }} chat
 * @param {boolean} tracked
 */
export async function setTracked(chat, tracked) {
  const key = chatKey(chat.service, chat.id);
  await updateKey('tracked', (map) => {
    const next = { ...(map || {}) };
    if (tracked) {
      next[key] = { service: chat.service, id: chat.id, title: chat.title || next[key]?.title || chat.id, addedAt: next[key]?.addedAt || Date.now() };
    } else {
      delete next[key];
    }
    return next;
  });
  // A manual choice wins over the capital-letters rule: an unmarked chat is not re-marked.
  await updateKey('trackOptOut', (map) => {
    const next = { ...(map || {}) };
    if (tracked) delete next[key];
    else next[key] = Date.now();
    return next;
  });
  await refreshAllIcons();
}

/**
 * Marks chats whose title is written in capital letters (unless the user unmarked them before).
 * @param {Array<{ service: import('../core/model.js').ServiceId, id: string, title: string }>} chats
 * @returns {Promise<string[]>} keys of newly marked chats
 */
export async function autoTrackUppercase(chats) {
  const candidates = chats.filter((c) => c && c.id && isUppercaseTitle(c.title));
  if (!candidates.length) return [];
  const [tracked, optOut] = await Promise.all([getTracked(), getKey('trackOptOut').then((m) => m || {})]);
  const fresh = candidates.filter((c) => !tracked[chatKey(c.service, c.id)] && !optOut[chatKey(c.service, c.id)]);
  if (!fresh.length) return [];
  await updateKey('tracked', (map) => {
    const next = { ...(map || {}) };
    for (const c of fresh) {
      const key = chatKey(c.service, c.id);
      if (!next[key]) next[key] = { service: c.service, id: c.id, title: c.title, addedAt: Date.now(), auto: true };
    }
    return next;
  });
  return fresh.map((c) => chatKey(c.service, c.id));
}

/**
 * Keeps stored titles up to date (titles change, ids do not).
 * @param {Array<{ service: string, id: string, title: string }>} chats
 */
export async function updateTrackedTitles(chats) {
  const tracked = await getTracked();
  const changed = chats.filter((c) => tracked[chatKey(c.service, c.id)] && tracked[chatKey(c.service, c.id)].title !== c.title);
  if (!changed.length) return;
  await updateKey('tracked', (map) => {
    const next = { ...(map || {}) };
    for (const c of changed) {
      const key = chatKey(c.service, c.id);
      if (next[key]) next[key] = { ...next[key], title: c.title };
    }
    return next;
  });
}

// Absolute paths: relative ones would resolve against the service worker's folder.
const ICONS = {
  default: { 16: '/icons/icon16.png', 32: '/icons/icon32.png' },
  tracked: { 16: '/icons/icon-green16.png', 32: '/icons/icon-green32.png' },
  untracked: { 16: '/icons/icon-red16.png', 32: '/icons/icon-red32.png' },
};

/**
 * Icon state for a URL: green = marked chat, red = chat not marked, default = anything else.
 * @param {string | undefined} url
 * @param {Record<string, TrackedChat>} tracked
 * @returns {'default' | 'tracked' | 'untracked'}
 */
export function iconStateFor(url, tracked) {
  const service = serviceFromUrl(url);
  if (!service) return 'default';
  const id = SERVICES[service].idFromUrl(url || '');
  if (!id) return 'default';
  return tracked[chatKey(service, id)] ? 'tracked' : 'untracked';
}

/**
 * Sets the toolbar icon of one tab (each tab, pinned ones included, has its own icon).
 * @param {number} tabId
 * @param {string | undefined} url
 * @param {Record<string, TrackedChat>} [tracked]
 */
export async function updateTabIcon(tabId, url, tracked) {
  const state = iconStateFor(url, tracked || await getTracked());
  const title = { default: 'Chat Exporter', tracked: 'Chat Exporter — чат помечен «Сохранять»', untracked: 'Chat Exporter — чат не помечен' }[state];
  await chrome.action.setTitle({ tabId, title }).catch(() => {}); // fails only if the tab is gone
  await chrome.action.setIcon({ tabId, path: ICONS[state] }).catch((err) => {
    console.warn('[Chat Exporter] setIcon failed', err);
  });
}

/** Updates the icons of all ChatGPT and Claude tabs. */
export async function refreshAllIcons() {
  const tracked = await getTracked();
  const tabs = await chrome.tabs.query({ url: [...SERVICES.chatgpt.tabPatterns, ...SERVICES.claude.tabPatterns] });
  await Promise.all(tabs.map((t) => (t.id !== undefined ? updateTabIcon(t.id, t.url, tracked) : null)));
}
