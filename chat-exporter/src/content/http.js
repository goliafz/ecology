// Same-origin requests from the content script. They run in the user's signed-in tab with the
// site's own cookies, exactly as the web app's own requests; no credentials leave the page.
import { ErrorCode, ExportError, httpError, withTimeout } from '../core/util.js';

/**
 * @param {string} url same-origin path or URL
 * @param {RequestInit & { what: string, timeoutMs?: number }} init
 * @returns {Promise<Response>}
 */
export async function request(url, init) {
  const { what, timeoutMs = 60_000, ...rest } = init;
  let res;
  try {
    res = await withTimeout(fetch(url, { credentials: 'include', ...rest }), timeoutMs, what);
  } catch (err) {
    if (err instanceof ExportError) throw err;
    throw new ExportError(ErrorCode.NETWORK, `Сетевая ошибка (${what}): ${err instanceof Error ? err.message : err}`);
  }
  if (!res.ok) throw httpError(res.status, what, res.headers.get('retry-after'));
  return res;
}

/**
 * @param {string} url
 * @param {RequestInit & { what: string, timeoutMs?: number }} init
 * @returns {Promise<any>}
 */
export async function requestJson(url, init) {
  const res = await request(url, init);
  const type = res.headers.get('content-type') || '';
  if (!type.includes('json')) {
    // Login pages and bot checks answer with HTML.
    throw new ExportError(ErrorCode.NOT_AUTHENTICATED, `Сервис вернул не JSON (${init.what}): возможно, требуется вход или проверка в браузере`);
  }
  try {
    return await res.json();
  } catch {
    throw new ExportError(ErrorCode.BAD_RESPONSE, `Некорректный JSON (${init.what})`);
  }
}

/**
 * Reads a cookie of the current site (non-HttpOnly cookies only).
 * @param {string} name
 */
export function getCookie(name) {
  const prefix = `${name}=`;
  for (const part of document.cookie.split(';')) {
    const p = part.trim();
    if (p.startsWith(prefix)) return decodeURIComponent(p.slice(prefix.length));
  }
  return null;
}

/**
 * Downloads an image and returns a data: URL, enforcing a size limit.
 * @param {string} url
 * @param {number} maxBytes
 * @param {Record<string, string>} [headers]
 */
export async function fetchImageAsDataUrl(url, maxBytes, headers) {
  const res = await request(url, { what: 'изображение', timeoutMs: 60_000, headers });
  const blob = await res.blob();
  if (blob.size > maxBytes) throw new Error(`изображение больше лимита (${Math.round(blob.size / 1024)} КБ)`);
  if (!blob.type.startsWith('image/')) throw new Error(`не изображение (${blob.type || 'неизвестный тип'})`);
  return /** @type {Promise<string>} */ (new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  }));
}
