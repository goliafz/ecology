// Safe file and folder names for chrome.downloads (paths relative to the Downloads folder).
import { SERVICE_LABELS } from './model.js';
import { localStamp } from './util.js';

const WINDOWS_RESERVED = /^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/i;
// Characters forbidden on Windows/macOS/Linux plus a few Chrome rejects in download names.
// eslint-disable-next-line no-control-regex
const FORBIDDEN = /[<>:"/\\|?*\u0000-\u001f\u007f\u200B-\u200F\u202A-\u202E\u2066-\u2069]/g;

/**
 * Makes one path segment safe: removes forbidden characters, trailing dots and spaces,
 * Windows reserved names and limits the length (in UTF-16 units, without splitting surrogates).
 * @param {string} input
 * @param {number} [maxLength]
 * @returns {string}
 */
export function sanitizeSegment(input, maxLength = 80) {
  let s = String(input ?? '')
    .normalize('NFC')
    .replace(FORBIDDEN, ' ')
    .replace(/[\u{E000}-\u{F8FF}]/gu, '') // private-use glyphs (e.g. ChatGPT citation markers)
    .replace(/\s+/g, ' ')
    .trim();
  s = s.replace(/^[.\s~]+/, ''); // no hidden files / home shortcuts
  if (s.length > maxLength) {
    s = s.slice(0, maxLength);
    // Do not leave a lone high surrogate at the end.
    if (/[\uD800-\uDBFF]$/.test(s)) s = s.slice(0, -1);
  }
  s = s.replace(/[.\s]+$/, '');
  if (!s) s = 'untitled';
  if (WINDOWS_RESERVED.test(s) || WINDOWS_RESERVED.test(s.split('.')[0])) s = `_${s}`;
  return s;
}

/**
 * Expands a mask like "%name%_%date%" into a safe relative path. "{name}" is accepted too.
 * Each "/"-separated segment is sanitized individually; empty segments and ".." are dropped.
 * @param {string} template
 * @param {Record<string, string>} vars
 * @returns {string}
 */
export function expandTemplate(template, vars) {
  const raw = String(template || '').replace(/%(\w+)%|\{(\w+)\}/g, (match, a, b) => {
    const value = vars[a || b];
    // Unknown tokens stay visible so a typo in the mask is noticed; slashes inside values must
    // not create folders.
    return value === undefined ? match.replace(/[%{}]/g, '') : String(value).replace(/[/\\]/g, ' ');
  });
  return raw
    .split(/[/\\]+/)
    .map((seg) => seg.trim())
    .filter((seg) => seg && seg !== '.' && seg !== '..')
    .map((seg) => sanitizeSegment(seg))
    .join('/');
}

/** Tokens available in folder and file masks (shown in the settings). */
export const MASK_TOKENS = /** @type {const} */ ({
  name: 'название чата',
  date: 'дата сохранения, ГГГГ-ММ-ДД',
  time: 'время сохранения, ЧЧ-ММ',
  updated: 'дата последнего изменения чата, ГГГГ-ММ-ДД',
  service: 'ChatGPT или Claude',
  id: 'идентификатор чата',
  shortId: 'первые 8 символов идентификатора',
});

/**
 * Mask variables for a chat.
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string, updatedAt?: string | null }} chat
 * @param {Date} now
 */
export function templateVars(chat, now) {
  const { date, time } = localStamp(now);
  const updated = chat.updatedAt ? localStamp(new Date(chat.updatedAt)).date : date;
  const name = chat.title || 'Без названия';
  return {
    name,
    title: name,
    service: SERVICE_LABELS[chat.service] || chat.service,
    id: chat.id,
    shortId: shortId(chat.id),
    date,
    time,
    updated,
  };
}

/** @param {string} id */
export function shortId(id) {
  return String(id).replace(/[^a-zA-Z0-9]/g, '').slice(0, 8) || 'noid';
}

/**
 * Builds the chat folder (relative to Downloads). The folder is remembered at the first export
 * so that all dated files of a chat stay together even if the chat is renamed later.
 * If another chat already uses the same folder (equal titles), the short id is appended.
 * @param {{ rootFolder: string, folderMask: string }} settings
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string, updatedAt?: string | null }} chat
 * @param {Date} now
 * @param {Set<string>} [taken] folders used by other chats
 */
export function buildChatFolder(settings, chat, now, taken = new Set()) {
  const vars = templateVars(chat, now);
  const root = expandTemplate(settings.rootFolder || 'ChatBackups', {});
  const sub = expandTemplate(settings.folderMask ?? '%service%/%name%', vars);
  let folder = [root, sub].filter(Boolean).join('/') || 'ChatBackups';
  if (taken.has(folder)) folder = `${folder} (${vars.shortId})`;
  return folder;
}

/**
 * File paths for one export.
 * @param {string} folder
 * @param {{ fileMask: string }} settings
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string, updatedAt?: string | null }} chat
 * @param {import('./model.js').ExportFormat[]} formats
 * @param {Date} now
 * @returns {{ files: Partial<Record<import('./model.js').ExportFormat, string>>, meta: string }}
 */
export function buildFilePaths(folder, settings, chat, formats, now) {
  const vars = templateVars(chat, now);
  const base = expandTemplate(settings.fileMask || '%name%_%date%', vars).replace(/\//g, ' ') || 'chat';
  /** @type {Partial<Record<import('./model.js').ExportFormat, string>>} */
  const files = {};
  for (const f of formats) files[f] = `${folder}/${base}.${f}`;
  return { files, meta: `${folder}/${base}.meta.json` };
}

/**
 * Validates a path for chrome.downloads (relative, no "..", not empty).
 * @param {string} path
 */
export function isSafeRelativePath(path) {
  if (!path || path.startsWith('/') || /^[a-zA-Z]:/.test(path)) return false;
  return path.split('/').every((seg) => seg && seg !== '..' && seg !== '.' && sanitizeSegment(seg, 1000) === seg);
}

const TRANSLIT = /** @type {Record<string, string>} */ ({
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l', м: 'm',
  н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'shch',
  ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya', є: 'ye', і: 'i', ї: 'yi', ґ: 'g',
});

/**
 * ASCII-only variant of a relative path (Cyrillic transliterated, other characters replaced).
 * Used when Chrome rejects a non-ASCII file name: on Linux with a non-UTF-8 locale Chrome cannot
 * convert such names and chrome.downloads reports "Invalid filename".
 * @param {string} path
 */
export function asciiPath(path) {
  return path.split('/').map((seg) => {
    const t = Array.from(seg.normalize('NFC'), (ch) => {
      const lower = ch.toLowerCase();
      const rep = TRANSLIT[lower];
      if (rep === undefined) return /[\x20-\x7e]/.test(ch) ? ch : '_';
      return ch === lower ? rep : rep.charAt(0).toUpperCase() + rep.slice(1);
    }).join('');
    return sanitizeSegment(t);
  }).join('/');
}

