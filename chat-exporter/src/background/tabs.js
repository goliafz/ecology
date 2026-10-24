// Tabs of ChatGPT / Claude used as the execution context for data requests.
// Requests are made by the content script inside the user's own signed-in tab, exactly
// like the web app itself does; the extension never sees or stores passwords or tokens.
import { SERVICES } from '../adapters/registry.js';
import { ErrorCode, ExportError, deserializeError, sleep, withTimeout } from '../core/util.js';
import { getKey, updateKey } from './storage.js';

const LOADER = 'src/content/loader.js';

// Operations currently using service tabs. Tabs opened by the extension are closed only when
// no operation holds a lease, so finishing one run never closes a tab another one just opened.
let leases = 0;

/**
 * Runs `fn` while holding a tab lease.
 * @template T
 * @param {() => Promise<T>} fn
 * @returns {Promise<T>}
 */
export async function withTabLease(fn) {
  leases += 1;
  try {
    return await fn();
  } finally {
    leases -= 1;
  }
}

/**
 * Sends a command to the content script and unwraps { ok, data | error }.
 * @param {number} tabId
 * @param {Record<string, unknown>} message
 * @param {number} [timeoutMs]
 * @returns {Promise<any>}
 */
export async function sendToTab(tabId, message, timeoutMs = 60_000) {
  let response;
  try {
    response = await withTimeout(chrome.tabs.sendMessage(tabId, message), timeoutMs, `ответ вкладки (${message.cmd})`);
  } catch (err) {
    if (err instanceof ExportError) throw err;
    throw new ExportError(ErrorCode.NO_TAB, `Вкладка недоступна: ${err instanceof Error ? err.message : err}`);
  }
  if (!response) throw new ExportError(ErrorCode.NO_TAB, 'Content script не ответил (вкладка перезагружается?)');
  if (!response.ok) throw deserializeError(response.error || {});
  return response.data;
}

/**
 * Makes sure the content script is alive in the tab; injects it if the tab was opened
 * before the extension was installed or reloaded.
 * @param {number} tabId
 */
export async function ensureContentScript(tabId) {
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      return await sendToTab(tabId, { cmd: 'ping' }, 3000);
    } catch {
      if (attempt === 0) {
        try {
          await chrome.scripting.executeScript({ target: { tabId }, files: [LOADER] });
        } catch (err) {
          throw new ExportError(ErrorCode.NO_TAB, `Не удалось подключиться к вкладке: ${err instanceof Error ? err.message : err}`);
        }
      }
      await sleep(700 * (attempt + 1));
    }
  }
  throw new ExportError(ErrorCode.NO_TAB, 'Content script не отвечает. Обновите вкладку сервиса.');
}

/**
 * @param {number} tabId
 * @param {number} timeoutMs
 */
function waitForTabComplete(tabId, timeoutMs) {
  /** @type {() => void} */
  let cleanup = () => {};
  return withTimeout(new Promise((resolve, reject) => {
    const onUpdated = (/** @type {number} */ id, /** @type {chrome.tabs.OnUpdatedInfo} */ info) => {
      if (id === tabId && info.status === 'complete') {
        cleanup();
        resolve(undefined);
      }
    };
    const onRemoved = (/** @type {number} */ id) => {
      if (id === tabId) {
        cleanup();
        reject(new ExportError(ErrorCode.NO_TAB, 'Вкладка сервиса была закрыта во время загрузки'));
      }
    };
    cleanup = () => {
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
    };
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
    chrome.tabs.get(tabId).then((tab) => {
      if (tab.status === 'complete') {
        cleanup();
        resolve(undefined);
      }
    }, (err) => {
      cleanup();
      reject(new ExportError(ErrorCode.NO_TAB, `Вкладка закрыта: ${err.message}`));
    });
  }), timeoutMs, 'загрузка вкладки').finally(() => cleanup());
}

/**
 * Finds an existing tab of the service. Prefers loaded, non-discarded tabs.
 * @param {import('../core/model.js').ServiceId} service
 * @returns {Promise<chrome.tabs.Tab | null>}
 */
export async function findServiceTab(service) {
  const tabs = await chrome.tabs.query({ url: SERVICES[service].tabPatterns });
  if (tabs.length === 0) return null;
  const score = (/** @type {chrome.tabs.Tab} */ t) => (t.discarded ? 0 : 2) + (t.status === 'complete' ? 1 : 0) + (t.active ? 0.5 : 0);
  return tabs.sort((a, b) => score(b) - score(a))[0];
}

/**
 * Returns a tab with a live content script for the service, opening a background tab if allowed.
 * @param {import('../core/model.js').ServiceId} service
 * @param {{ allowOpen: boolean }} options
 * @returns {Promise<number>}
 */
export async function getServiceTab(service, { allowOpen }) {
  const existing = await findServiceTab(service);
  if (existing?.id !== undefined) {
    if (existing.discarded) {
      await chrome.tabs.reload(existing.id);
    }
    await waitForTabComplete(existing.id, 60_000);
    await ensureContentScript(existing.id);
    return existing.id;
  }
  if (!allowOpen) {
    throw new ExportError(ErrorCode.NO_TAB, `Нет открытой вкладки ${SERVICES[service].label}. Откройте сайт и войдите в аккаунт.`);
  }
  const tabId = await openBackgroundTab(SERVICES[service].homeUrl);
  await waitForTabComplete(tabId, 90_000);
  await ensureContentScript(tabId);
  return tabId;
}

/**
 * Opens a URL without stealing focus. Uses a minimized window when no normal window exists.
 * Remembers the tab so it can be closed after the run.
 * @param {string} url
 * @returns {Promise<number>}
 */
export async function openBackgroundTab(url) {
  const windows = await chrome.windows.getAll({ windowTypes: ['normal'] });
  let tabId;
  if (windows.length > 0) {
    const target = windows.find((w) => w.focused) || windows[0];
    const tab = await chrome.tabs.create({ url, active: false, windowId: target.id });
    tabId = tab.id;
  } else {
    const win = await chrome.windows.create({ url, focused: false, state: 'minimized' });
    tabId = win.tabs?.[0]?.id;
  }
  if (tabId === undefined) throw new ExportError(ErrorCode.NO_TAB, 'Не удалось открыть вкладку');
  await updateKey('openedTabs', (list) => [...new Set([...(Array.isArray(list) ? list : []), tabId])], 'session');
  return tabId;
}

/**
 * Navigates a tab the extension opened itself (never the user's tabs) and waits for it.
 * @param {number} tabId
 * @param {string} url
 */
export async function navigateOwnTab(tabId, url) {
  const opened = (await getKey('openedTabs', 'session')) || [];
  if (!opened.includes(tabId)) {
    throw new ExportError(ErrorCode.NO_TAB, 'Чат не открыт во вкладке, а переходить в вашей вкладке расширение не будет.');
  }
  await chrome.tabs.update(tabId, { url });
  await sleep(300);
  await waitForTabComplete(tabId, 90_000);
  await ensureContentScript(tabId);
}

/** Closes tabs opened by the extension during a run (unless another operation uses them). */
export async function closeOpenedTabs() {
  if (leases > 0) return;
  const opened = (await getKey('openedTabs', 'session')) || [];
  for (const id of opened) {
    try {
      await chrome.tabs.remove(id);
    } catch {
      // already closed by the user
    }
  }
  await updateKey('openedTabs', () => [], 'session');
}

/** @param {number} tabId */
export async function isOwnTab(tabId) {
  const opened = (await getKey('openedTabs', 'session')) || [];
  return opened.includes(tabId);
}
