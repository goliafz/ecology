// Date arithmetic for the daily backup schedule (local time, DST-safe).

/**
 * Parses "HH:MM".
 * @param {string} hhmm
 * @returns {{ h: number, m: number }}
 */
export function parseTime(hhmm) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(hhmm || '').trim());
  if (!m) return { h: 23, m: 0 };
  return { h: Math.min(23, Number(m[1])), m: Math.min(59, Number(m[2])) };
}

/**
 * Next run strictly after `now` at local time `hhmm` on one of `days` (0 = Sunday).
 * Built with the Date(y, m, d, h, min) constructor, so DST shifts keep the wall-clock time.
 * @param {Date} now
 * @param {string} hhmm
 * @param {number[]} [days] empty / undefined = every day
 * @returns {Date}
 */
export function computeNextRun(now, hhmm, days) {
  const { h, m } = parseTime(hhmm);
  const allowed = Array.isArray(days) && days.length ? new Set(days) : null;
  for (let i = 0; i <= 8; i++) {
    const c = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i, h, m, 0, 0);
    if (c.getTime() <= now.getTime()) continue;
    if (allowed && !allowed.has(c.getDay())) continue;
    return c;
  }
  // Unreachable with a non-empty day set; fall back to tomorrow.
  return new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, h, m, 0, 0);
}

/**
 * Was a scheduled run missed (browser closed, computer asleep)?
 * @param {{ nextRunAt?: number | null, lastRunStartedAt?: number | null }} state
 * @param {number} nowMs
 * @param {number} [graceMs] tolerance for alarm delays
 */
export function isRunMissed(state, nowMs, graceMs = 5 * 60_000) {
  if (!state.nextRunAt) return false;
  if (nowMs < state.nextRunAt + graceMs) return false;
  return !state.lastRunStartedAt || state.lastRunStartedAt < state.nextRunAt;
}
