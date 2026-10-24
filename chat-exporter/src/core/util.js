// Small helpers without browser dependencies (usable in Node tests).

/** Error codes shared across the extension. `transient` codes are retried. */
export const ErrorCode = /** @type {const} */ ({
  NOT_AUTHENTICATED: 'NOT_AUTHENTICATED',
  NOT_FOUND: 'NOT_FOUND',
  FORBIDDEN: 'FORBIDDEN',
  RATE_LIMITED: 'RATE_LIMITED',
  NETWORK: 'NETWORK',
  SERVER: 'SERVER',
  TIMEOUT: 'TIMEOUT',
  BAD_RESPONSE: 'BAD_RESPONSE',
  NO_TAB: 'NO_TAB',
  NOT_A_CHAT: 'NOT_A_CHAT',
  DOM_CHANGED: 'DOM_CHANGED',
  INCOMPLETE: 'INCOMPLETE',
  DOWNLOAD_FAILED: 'DOWNLOAD_FAILED',
  PDF_FAILED: 'PDF_FAILED',
  CANCELLED: 'CANCELLED',
  UNKNOWN: 'UNKNOWN',
});

const TRANSIENT = /** @type {Set<string>} */ (new Set([
  ErrorCode.RATE_LIMITED, ErrorCode.NETWORK, ErrorCode.SERVER, ErrorCode.TIMEOUT,
  ErrorCode.NO_TAB, ErrorCode.DOWNLOAD_FAILED, ErrorCode.PDF_FAILED,
]));

export class ExportError extends Error {
  /**
   * @param {string} code One of ErrorCode.
   * @param {string} message Human readable (Russian) message.
   * @param {{ retryAfterMs?: number, cause?: unknown }} [extra]
   */
  constructor(code, message, extra = {}) {
    super(message);
    this.name = 'ExportError';
    this.code = code;
    this.retryAfterMs = extra.retryAfterMs;
    if (extra.cause !== undefined) this.cause = extra.cause;
  }

  get transient() {
    return TRANSIENT.has(this.code);
  }
}

/**
 * Serializes an error so it survives chrome.runtime messaging.
 * @param {unknown} err
 * @returns {{ code: string, message: string, retryAfterMs?: number }}
 */
export function serializeError(err) {
  if (err instanceof ExportError) {
    return { code: err.code, message: err.message, retryAfterMs: err.retryAfterMs };
  }
  if (err && typeof err === 'object' && 'code' in err && 'message' in err) {
    const e = /** @type {any} */ (err);
    return { code: String(e.code), message: String(e.message), retryAfterMs: e.retryAfterMs };
  }
  const message = err instanceof Error ? err.message : String(err);
  return { code: ErrorCode.UNKNOWN, message };
}

/**
 * @param {{ code: string, message: string, retryAfterMs?: number }} data
 */
export function deserializeError(data) {
  return new ExportError(data.code || ErrorCode.UNKNOWN, data.message || 'Неизвестная ошибка', {
    retryAfterMs: data.retryAfterMs,
  });
}

/** @param {number} ms */
export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** @param {AbortSignal | undefined} signal */
export function throwIfCancelled(signal) {
  if (signal?.aborted) throw new ExportError(ErrorCode.CANCELLED, 'Отменено пользователем');
}

/**
 * Settles with `promise`, or rejects with CANCELLED as soon as `signal` aborts. The work behind
 * the promise is not stopped by this; callers check the signal before any lasting effect.
 * @template T
 * @param {Promise<T>} promise
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<T>}
 */
export function abortable(promise, signal) {
  if (!signal) return promise;
  throwIfCancelled(signal);
  /** @type {() => void} */
  let onAbort = () => {};
  const aborted = new Promise((_, reject) => {
    onAbort = () => reject(new ExportError(ErrorCode.CANCELLED, 'Отменено пользователем'));
    signal.addEventListener('abort', onAbort, { once: true });
  });
  return /** @type {Promise<T>} */ (Promise.race([promise, aborted]).finally(() => signal.removeEventListener('abort', onAbort)));
}

/**
 * sleep() that ends early when `signal` aborts (resolves, does not throw).
 * @param {number} ms
 * @param {AbortSignal | undefined} signal
 */
export function sleepUnlessCancelled(ms, signal) {
  return abortable(sleep(ms), signal).catch(() => {});
}

/**
 * Rejects with a TIMEOUT ExportError when `promise` does not settle in time.
 * @template T
 * @param {Promise<T>} promise
 * @param {number} ms
 * @param {string} what Used in the error message.
 * @returns {Promise<T>}
 */
export function withTimeout(promise, ms, what) {
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(
      () => reject(new ExportError(ErrorCode.TIMEOUT, `Превышено время ожидания: ${what} (${Math.round(ms / 1000)} с)`)),
      ms,
    );
  });
  return /** @type {Promise<T>} */ (Promise.race([promise, timeout]).finally(() => clearTimeout(timer)));
}

/**
 * Exponential backoff with full jitter.
 * @param {number} attempt 0-based
 * @param {number} baseMs
 * @param {number} maxMs
 * @param {() => number} [random]
 */
export function backoffDelay(attempt, baseMs, maxMs, random = Math.random) {
  const cap = Math.min(maxMs, baseMs * 2 ** attempt);
  return Math.round(cap / 2 + random() * (cap / 2));
}

/**
 * Maps an HTTP status code to an ExportError.
 * @param {number} status
 * @param {string} what
 * @param {string | null} [retryAfter] Retry-After header value.
 */
export function httpError(status, what, retryAfter = null) {
  if (status === 401) return new ExportError(ErrorCode.NOT_AUTHENTICATED, `Нет авторизации (${what}): войдите в аккаунт во вкладке сервиса`);
  if (status === 403) return new ExportError(ErrorCode.FORBIDDEN, `Доступ запрещён (${what}, HTTP 403)`);
  if (status === 404) return new ExportError(ErrorCode.NOT_FOUND, `Не найдено (${what}, HTTP 404): чат удалён или недоступен`);
  if (status === 429) {
    const seconds = retryAfter ? Number.parseInt(retryAfter, 10) : NaN;
    return new ExportError(ErrorCode.RATE_LIMITED, `Сервис ограничил частоту запросов (${what}, HTTP 429)`, {
      retryAfterMs: Number.isFinite(seconds) ? seconds * 1000 : 60_000,
    });
  }
  if (status >= 500) return new ExportError(ErrorCode.SERVER, `Ошибка сервера (${what}, HTTP ${status})`);
  return new ExportError(ErrorCode.BAD_RESPONSE, `Неожиданный ответ (${what}, HTTP ${status})`);
}

/**
 * Formats a date in local time as YYYY-MM-DD and HH-MM (safe for file names).
 * @param {Date} date
 */
export function localStamp(date) {
  const p = (/** @type {number} */ n) => String(n).padStart(2, '0');
  return {
    date: `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}`,
    time: `${p(date.getHours())}-${p(date.getMinutes())}`,
  };
}

/**
 * Human readable local date-time, e.g. "24.10.2026, 23:00".
 * @param {string | number | Date | null | undefined} value
 */
export function formatDateTime(value) {
  if (value === null || value === undefined || value === '') return '';
  const d = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString('ru-RU', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

/** @param {number} bytes */
export function formatBytes(bytes) {
  if (!Number.isFinite(bytes)) return '';
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
  return `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
}

/**
 * Truncates long strings (tool inputs etc.) for display.
 * @param {string} text
 * @param {number} max
 */
export function truncate(text, max) {
  if (text.length <= max) return text;
  return `${text.slice(0, max)}\n… [обрезано: ${text.length - max} симв.]`;
}
