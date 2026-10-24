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
        reject(new ExportError(ErrorCode.DOWNLOAD_FAILED, `Сохранение файла прервано: ${item.error || 'неизвестная причина'}`));
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
    const item = await waitForDownload(downloadId, options.timeoutMs || 120_000);
    const written = item.fileSize > 0 ? item.fileSize : item.bytesReceived;
    if (written !== bytes.length) {
      throw new ExportError(ErrorCode.DOWNLOAD_FAILED, `Размер сохранённого файла (${written}) не совпадает с ожидаемым (${bytes.length})`);
    }
    if (options.eraseFromList) await chrome.downloads.erase({ id: downloadId });
    return { path: finalPath, absolutePath: item.filename, bytes: bytes.length, sha256 };
  } finally {
    chrome.runtime.sendMessage({ target: 'offscreen', type: 'revokeBlobUrl', url }).catch(() => {});
  }
}
