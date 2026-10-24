// chrome.storage wrappers. All state that must survive a service-worker restart or a browser
// restart lives in chrome.storage.local; per-session scratch data lives in chrome.storage.session.
//
// chrome.storage has no transactions, so read-modify-write updates of one key are serialized
// through a per-key promise chain (the service worker is the only writer of these keys).

/** @type {Map<string, Promise<unknown>>} */
const chains = new Map();

/**
 * @param {string} key
 * @param {'local' | 'session'} [area]
 * @returns {Promise<any>}
 */
export async function getKey(key, area = 'local') {
  const result = await chrome.storage[area].get(key);
  return result[key];
}

/**
 * @param {string} key
 * @param {any} value
 * @param {'local' | 'session'} [area]
 */
export async function setKey(key, value, area = 'local') {
  await chrome.storage[area].set({ [key]: value });
}

/**
 * Serialized read-modify-write of one key.
 * @template T
 * @param {string} key
 * @param {(current: any) => T | Promise<T>} fn returns the new value
 * @param {'local' | 'session'} [area]
 * @returns {Promise<T>}
 */
export function updateKey(key, fn, area = 'local') {
  const chainKey = `${area}:${key}`;
  const prev = chains.get(chainKey) || Promise.resolve();
  const next = prev.catch(() => {}).then(async () => {
    const current = await getKey(key, area);
    const value = await fn(current);
    await setKey(key, value, area);
    return value;
  });
  chains.set(chainKey, next);
  return next;
}

// ---- Chat index -------------------------------------------------------------------------

/**
 * @typedef {Object} ExportedFile
 * @property {string} path relative to Downloads
 * @property {string} sha256
 * @property {number} bytes
 * @property {string} [absolutePath] reported by chrome.downloads
 */

/**
 * @typedef {Object} LastExport
 * @property {number} at
 * @property {string | null} updatedAt chat update time at export
 * @property {string} contentHash
 * @property {import('../core/model.js').CompletenessStatus} status
 * @property {import('../core/model.js').ExportFormat[]} formats
 * @property {Record<string, ExportedFile>} files
 * @property {number} messages
 * @property {'api' | 'dom'} source
 */

/**
 * @typedef {Object} ChatIndexEntry
 * @property {import('../core/model.js').ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {string | null} updatedAt latest update time known from the chat list
 * @property {string} [folder] fixed at the first export
 * @property {string} [folderKey] root folder + folder mask the folder was built with
 * @property {LastExport} [lastExport]
 * @property {boolean} [dirty] activity seen while working (Scenario C)
 * @property {number} [dirtyAt]
 * @property {number} [seenAt] last time the chat appeared in a list
 * @property {number | null} [missingSince] absent from a complete list (deleted/archived)
 * @property {boolean} [archived]
 * @property {number} [failures]
 * @property {{ code: string, message: string, at: number }} [lastError]
 * @property {number} [nextRetryAt]
 */

/** @returns {Promise<Record<string, ChatIndexEntry>>} */
export async function getIndex() {
  return (await getKey('chatIndex')) || {};
}

/**
 * @param {string} key
 * @param {(entry: ChatIndexEntry | undefined) => ChatIndexEntry | undefined} fn
 */
export function updateIndexEntry(key, fn) {
  return updateKey('chatIndex', (index) => {
    const next = { ...(index || {}) };
    const value = fn(next[key]);
    if (value) next[key] = value;
    else delete next[key];
    return next;
  });
}

/**
 * Applies many entry updates in one write.
 * @param {(index: Record<string, ChatIndexEntry>) => void} mutate
 */
export function mutateIndex(mutate) {
  return updateKey('chatIndex', (index) => {
    const next = { ...(index || {}) };
    mutate(next);
    return next;
  });
}
