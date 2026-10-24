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
 * Expands a template like "{service}/{title}__{shortId}" into a safe relative path.
 * Each "/"-separated segment is sanitized individually; empty segments and ".." are dropped.
 * @param {string} template
 * @param {Record<string, string>} vars
 * @returns {string}
 */
export function expandTemplate(template, vars) {
  const raw = String(template || '').replace(/\{(\w+)\}/g, (_, key) => {
    const value = vars[key];
    // Slashes inside values must not create folders.
    return value === undefined ? '' : String(value).replace(/[/\\]/g, ' ');
  });
  return raw
    .split(/[/\\]+/)
    .map((seg) => seg.trim())
    .filter((seg) => seg && seg !== '.' && seg !== '..')
    .map((seg) => sanitizeSegment(seg))
    .join('/');
}

/**
 * Template variables for a chat.
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string }} chat
 * @param {Date} now
 */
export function templateVars(chat, now) {
  const { date, time } = localStamp(now);
  return {
    service: SERVICE_LABELS[chat.service] || chat.service,
    title: chat.title || 'Без названия',
    id: chat.id,
    shortId: shortId(chat.id),
    date,
    time,
  };
}

/** @param {string} id */
export function shortId(id) {
  return String(id).replace(/[^a-zA-Z0-9]/g, '').slice(0, 8) || 'noid';
}

/**
 * Builds the chat folder (relative to Downloads). Folders are fixed at the first export
 * (stored in the index) so that renaming a chat does not create a second folder.
 * The template is forced to contain a unique id, otherwise chats with equal titles collide.
 * @param {{ rootFolder: string, folderTemplate: string }} settings
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string }} chat
 * @param {Date} now
 */
export function buildChatFolder(settings, chat, now) {
  let template = settings.folderTemplate || '{service}/{title}__{shortId}';
  if (!/\{(id|shortId)\}/.test(template)) template += '__{shortId}';
  const vars = templateVars(chat, now);
  const root = expandTemplate(settings.rootFolder || 'ChatBackups', {});
  const sub = expandTemplate(template, vars);
  return [root, sub].filter(Boolean).join('/');
}

/**
 * File paths for one export.
 * @param {string} folder
 * @param {{ fileTemplate: string, versioning: 'overwrite' | 'versions' }} settings
 * @param {{ service: import('./model.js').ServiceId, id: string, title: string }} chat
 * @param {import('./model.js').ExportFormat[]} formats
 * @param {Date} now
 * @returns {{ files: Partial<Record<import('./model.js').ExportFormat, string>>, meta: string }}
 */
export function buildFilePaths(folder, settings, chat, formats, now) {
  const vars = templateVars(chat, now);
  let base = expandTemplate(settings.fileTemplate || 'chat', vars).replace(/\//g, ' ');
  if (!base) base = 'chat';
  const suffix = settings.versioning === 'versions' ? `_${vars.date}_${vars.time}` : '';
  /** @type {Partial<Record<import('./model.js').ExportFormat, string>>} */
  const files = {};
  for (const f of formats) files[f] = `${folder}/${base}${suffix}.${f}`;
  // The metadata file is always "latest": it describes the newest successful export.
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

