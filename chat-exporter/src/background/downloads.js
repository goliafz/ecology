// Writing files through chrome.downloads.
//
// A service worker cannot call URL.createObjectURL, and data: URLs are limited in size, so the
// bytes are handed to an offscreen document (reason BLOBS) which returns a blob: URL that
// chrome.downloads can fetch. Chrome writes to a temporary file and renames it on completion,
// so an interrupted download never replaces an existing good copy.
import { sha256Hex } from '../core/hash.js';
import { asciiPath, isSafeRelativePath } from '../core/filenames.js';
import { ErrorCode, ExportError, withTimeout } from '../core/util.js';

const OFFSCREEN_URL = 'src/offscreen/offscreen.html';
/** @type {Promise<void> | null} */
let creating = null;

async function hasOffscreenDocument() {
  const contexts = await chrome.runtime.getContexts({
    contextTypes: [/** @type {chrome.runtime.ContextType} */ ('OFFSCREEN_DOCUMENT')],
    documentUrls: [chrome.runtime.getURL(OFFSCREEN_URL)],
  });
  return contexts.length > 0;
}

export async function ensureOffscreen() {
  if (await hasOffscreenDocument()) return;
  if (!creating) {
    creating = chrome.offscreen.createDocument({
      url: OFFSCREEN_URL,
      reasons: [/** @type {chrome.offscreen.Reason} */ ('BLOBS')],
      justification: 'Создание blob: URL для сохранения экспортированных файлов через chrome.downloads',
    }).finally(() => {
      creating = null;
    });
  }
  await creating;
}

/** @param {Uint8Array} bytes */
export function bytesToBase64(bytes) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

/** @param {string} b64 */
export function base64ToBytes(b64) {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Human readable text for chrome.downloads InterruptReason values.
 * FILE_ACCESS_DENIED is what Chrome's download list shows as "Для скачивания нужно разрешение":
 * the file could not be written (e.g. the old file is open in a PDF viewer, or the folder is protected).
 * @param {string | undefined} reason
 */
export function interruptReasonText(reason) {
  const map = {
    FILE_ACCESS_DENIED: 'нет доступа к файлу — вероятно, файл с таким именем открыт в другой программе (например, в просмотрщике PDF) или папка защищена от записи (FILE_ACCESS_DENIED)',
    FILE_NO_SPACE: 'на диске нет места (FILE_NO_SPACE)',
    FILE_NAME_TOO_LONG: 'слишком длинный путь к файлу — сократите маску имени или папки (FILE_NAME_TOO_LONG)',
    FILE_TOO_LARGE: 'файл слишком большой для файловой системы (FILE_TOO_LARGE)',
    FILE_VIRUS_INFECTED: 'антивирус заблокировал файл (FILE_VIRUS_INFECTED)',
    FILE_BLOCKED: 'сохранение заблокировано политикой безопасности (FILE_BLOCKED)',
    FILE_SECURITY_CHECK_FAILED: 'проверка безопасности не пройдена (FILE_SECURITY_CHECK_FAILED)',
    FILE_TRANSIENT_ERROR: 'временная ошибка файловой системы (FILE_TRANSIENT_ERROR)',
    FILE_FAILED: 'ошибка записи файла (FILE_FAILED)',
    USER_CANCELED: 'сохранение отменено (USER_CANCELED)',
  };
  return /** @type {Record<string, string>} */ (map)[reason || ''] || `ошибка ${reason || 'без описания'}`;
}

/** Reasons worth one retry under a new name instead of replacing the existing file. */
const RETRY_AS_NEW_NAME = new Set(['FILE_ACCESS_DENIED', 'FILE_TRANSIENT_ERROR', 'FILE_FAILED']);

/**
 * @param {number} id
 * @param {number} timeoutMs
 * @returns {Promise<chrome.downloads.DownloadItem>}
 */
function waitForDownload(id, timeoutMs) {
  return withTimeout(new Promise((resolve, reject) => {
    const check = async () => {
      const [item] = await chrome.downloads.search({ id });
      if (!item) return false;
      if (item.state === 'complete') {
        resolve(item);
        return true;
      }
      if (item.state === 'interrupted') {
        const err = new ExportError(ErrorCode.DOWNLOAD_FAILED, `Файл не сохранён: ${interruptReasonText(item.error)}`);
        /** @type {any} */ (err).reason = item.error || '';
        reject(err);
        return true;
      }
      return false;
    };
    const listener = (/** @type {chrome.downloads.DownloadDelta} */ delta) => {
      if (delta.id !== id || !delta.state) return;
      check().then((done) => {
        if (done) chrome.downloads.onChanged.removeListener(listener);
      });
    };
    chrome.downloads.onChanged.addListener(listener);
    check().then((done) => {
      if (done) chrome.downloads.onChanged.removeListener(listener);
    }, reject);
  }), timeoutMs, 'сохранение файла (если в Chrome включено «Всегда указывать место для скачивания», отключите его для автоматического режима)');
}

/**
 * @typedef {Object} WriteResult
 * @property {string} path relative path requested
 * @property {string} absolutePath where Chrome saved it
 * @property {number} bytes
 * @property {string} sha256
 * @property {boolean} [renamed] saved under a new name because the existing file could not be replaced
 */

/**
 * Saves `data` to Downloads/<path>.
 * @param {string} path relative path with subfolders
 * @param {string | Uint8Array} data
 * @param {string} mime
 * @param {{ conflictAction?: 'overwrite' | 'uniquify', eraseFromList?: boolean, timeoutMs?: number }} [options]
 * @returns {Promise<WriteResult>}
 */
export async function writeFile(path, data, mime, options = {}) {
  if (!isSafeRelativePath(path)) throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `Недопустимый путь файла: ${path}`);
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : data;
  const sha256 = await sha256Hex(bytes);
  await ensureOffscreen();
  const created = await chrome.runtime.sendMessage({
    target: 'offscreen',
    type: 'createBlobUrl',
    base64: bytesToBase64(bytes),
    mime,
  });
  if (!created?.ok) throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `Не удалось подготовить файл: ${created?.error || 'offscreen недоступен'}`);
  const url = created.url;
  try {
    let downloadId;
    let finalPath = path;
    const start = (/** @type {string} */ filename) => chrome.downloads.download({
      url,
      filename,
      conflictAction: options.conflictAction || 'overwrite',
      saveAs: false,
    });
    try {
      downloadId = await start(path);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      const ascii = asciiPath(path);
      if (!/invalid filename/i.test(message) || ascii === path) {
        throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `chrome.downloads отклонил файл «${path}»: ${message}`);
      }
      // Non-ASCII names can be rejected (e.g. Linux with a non-UTF-8 locale): retry transliterated.
      try {
        downloadId = await start(ascii);
        finalPath = ascii;
      } catch (err2) {
        throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `chrome.downloads отклонил файл «${ascii}»: ${err2 instanceof Error ? err2.message : err2}`);
      }
    }
    let item;
    let renamed = false;
    try {
      item = await waitForDownload(downloadId, options.timeoutMs || 120_000);
    } catch (err) {
      const reason = /** @type {any} */ (err)?.reason;
      // Remove the failed entry so Chrome's download list does not keep a red item.
      await chrome.downloads.erase({ id: downloadId }).catch(() => {});
      if (!RETRY_AS_NEW_NAME.has(reason)) throw err;
      // The existing file could not be replaced (often: it is open in another program).
      // Save next to it as "<name> (1).pdf" instead of failing.
      const retryId = await chrome.downloads.download({ url, filename: finalPath, conflictAction: 'uniquify', saveAs: false });
      try {
        item = await waitForDownload(retryId, options.timeoutMs || 120_000);
      } catch (err2) {
        await chrome.downloads.erase({ id: retryId }).catch(() => {});
        throw err2;
      }
      downloadId = retryId;
      renamed = true;
    }
    const written = item.fileSize > 0 ? item.fileSize : item.bytesReceived;
    if (written !== bytes.length) {
      throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `Размер сохранённого файла (${written}) не совпадает с ожидаемым (${bytes.length})`);
    }
    if (options.eraseFromList) await chrome.downloads.erase({ id: downloadId });
    return { path: finalPath, absolutePath: item.filename, bytes: bytes.length, sha256, renamed };
  } finally {
    chrome.runtime.sendMessage({ target: 'offscreen', type: 'revokeBlobUrl', url }).catch(() => {});
  }
}
