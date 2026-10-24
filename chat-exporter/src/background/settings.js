// User settings: defaults, validation and persistence (chrome.storage.local "settings").
import { getKey, setKey } from './storage.js';

/** @typedef {import('../core/model.js').ExportFormat} ExportFormat */

/**
 * @typedef {Object} AutoSettings
 * @property {boolean} enabled
 * @property {'tracked' | 'all'} scope tracked: only chats marked "Сохранять"; all: every changed chat
 * @property {string} time "HH:MM"
 * @property {number[]} days
 * @property {ExportFormat[]} formats
 * @property {'auto' | 'ask' | 'skip'} missedRun
 * @property {number} maxChatsPerRun
 * @property {number} delayMs
 * @property {number} maxAttempts
 * @property {boolean} backfill
 * @property {number | null} baselineAt
 * @property {boolean} openTabs
 * @property {boolean} closeOpenedTabs
 */

/**
 * @typedef {Object} Settings
 * @property {number} schema
 * @property {ExportFormat[]} formats
 * @property {string} rootFolder
 * @property {string} folderMask
 * @property {string} fileMask
 * @property {'overwrite' | 'uniquify'} onConflict
 * @property {boolean} writeMeta
 * @property {boolean} includeImages
 * @property {number} maxImageBytes
 * @property {'omit' | 'embed'} mdImages
 * @property {boolean} includeThinking
 * @property {boolean} includeTools
 * @property {'debugger' | 'dialog'} pdfMethod
 * @property {boolean} domFallback
 * @property {{ chatgpt: boolean, claude: boolean }} services
 * @property {boolean} includeChatGptProjects
 * @property {boolean} trackActivity
 * @property {boolean} eraseFromDownloadList
 * @property {AutoSettings} auto
 * @property {{ enabled: boolean, maxEntries: number }} journal
 * @property {Record<string, { title: string, service: string }>} excluded
 */

/** @type {Readonly<Settings>} */
export const DEFAULT_SETTINGS = Object.freeze({
  schema: 3,
  /** Formats for manual exports. */
  formats: /** @type {ExportFormat[]} */ (['pdf']),
  rootFolder: 'ChatBackups',
  /** Folder of a chat inside rootFolder; %tokens% as in core/filenames.js MASK_TOKENS. */
  folderMask: '%service%/%name%',
  /** File name without extension. With %date% every day gets its own file: a dated history. */
  fileMask: '%name%_%date%',
  /** A file with the same name already exists: replace it or add a number. */
  onConflict: /** @type {'overwrite' | 'uniquify'} */ ('overwrite'),
  /** Write <file>.meta.json with the completeness report and checksums next to the files. */
  writeMeta: false,
  includeImages: true,
  maxImageBytes: 8 * 1024 * 1024,
  /** Markdown: embed images as data URIs (large files) or leave a placeholder. */
  mdImages: /** @type {'omit' | 'embed'} */ ('omit'),
  includeThinking: false,
  includeTools: false,
  /** debugger: automatic PDF via chrome.debugger; dialog: open the print dialog (manual saving). */
  pdfMethod: /** @type {'debugger' | 'dialog'} */ ('debugger'),
  /** Use the page DOM when the data endpoint fails (only for a chat open in a tab). */
  domFallback: true,
  services: { chatgpt: true, claude: true },
  includeChatGptProjects: true,
  /** Mark chats as changed while you work (Scenario C). */
  trackActivity: true,
  /** Remove finished backup downloads from Chrome's download list (files stay on disk). */
  eraseFromDownloadList: false,
  auto: {
    enabled: false,
    /** Back up only chats marked "Сохранять" (popup checkbox) or every changed chat. */
    scope: /** @type {'tracked' | 'all'} */ ('tracked'),
    time: '23:00',
    /** 0 = Sunday … 6 = Saturday; empty = every day. */
    days: /** @type {number[]} */ ([]),
    formats: /** @type {ExportFormat[]} */ (['pdf']),
    /** What to do after a missed run (browser closed or computer asleep). */
    missedRun: /** @type {'auto' | 'ask' | 'skip'} */ ('ask'),
    maxChatsPerRun: 100,
    delayMs: 2500,
    maxAttempts: 3,
    /** Back up chats that changed before auto mode was enabled, gradually. */
    backfill: false,
    /** Set when auto mode is enabled: chats changed after this moment are backed up. */
    baselineAt: /** @type {number | null} */ (null),
    openTabs: true,
    closeOpenedTabs: true,
  },
  journal: { enabled: true, maxEntries: 500 },
  /** chatKey -> { title, service } */
  excluded: /** @type {Record<string, { title: string, service: string }>} */ ({}),
});

const FORMATS = new Set(['md', 'html', 'pdf']);

/**
 * Deep-merges stored settings over defaults and validates every field.
 * @param {any} stored
 * @returns {Settings}
 */
export function normalizeSettings(stored) {
  const d = DEFAULT_SETTINGS;
  const s = stored && typeof stored === 'object' ? stored : {};
  const a = s.auto && typeof s.auto === 'object' ? s.auto : {};
  const formats = (/** @type {any} */ v, /** @type {ExportFormat[]} */ fb) => {
    const list = Array.isArray(v) ? v.filter((f) => FORMATS.has(f)) : [];
    return /** @type {ExportFormat[]} */ (list.length ? [...new Set(list)] : [...fb]);
  };
  const int = (/** @type {any} */ v, /** @type {number} */ fb, /** @type {number} */ min, /** @type {number} */ max) => {
    const n = Number(v);
    return Number.isFinite(n) ? Math.min(max, Math.max(min, Math.round(n))) : fb;
  };
  const str = (/** @type {any} */ v, /** @type {string} */ fb) => (typeof v === 'string' && v.trim() ? v.trim() : fb);
  const oneOf = (/** @type {any} */ v, /** @type {readonly string[]} */ list, /** @type {string} */ fb) => (list.includes(v) ? v : fb);
  const bool = (/** @type {any} */ v, /** @type {boolean} */ fb) => (typeof v === 'boolean' ? v : fb);

  // Schema 1 (version 1.0.0) saved HTML + Markdown as the default formats; they were never an
  // explicit choice, so they are replaced by the new default (PDF).
  const legacy = typeof s.schema === 'number' && s.schema < 2;
  // Schema 2 (1.1.0–1.2.1) defaulted to one folder per chat without the service level;
  // that default is replaced so ChatGPT and Claude chats go to separate folders.
  const oldFolderDefault = typeof s.schema === 'number' && s.schema < 3 && (s.folderMask === undefined || String(s.folderMask).trim() === '%name%');
  return {
    schema: 3,
    formats: legacy ? [...d.formats] : formats(s.formats, d.formats),
    rootFolder: str(s.rootFolder, d.rootFolder),
    folderMask: oldFolderDefault ? d.folderMask : typeof s.folderMask === 'string' ? s.folderMask.trim() : d.folderMask,
    fileMask: str(s.fileMask, d.fileMask),
    onConflict: /** @type {any} */ (oneOf(s.onConflict, ['overwrite', 'uniquify'], d.onConflict)),
    writeMeta: bool(s.writeMeta, d.writeMeta),
    includeImages: bool(s.includeImages, d.includeImages),
    maxImageBytes: int(s.maxImageBytes, d.maxImageBytes, 64 * 1024, 50 * 1024 * 1024),
    mdImages: /** @type {any} */ (oneOf(s.mdImages, ['omit', 'embed'], d.mdImages)),
    includeThinking: bool(s.includeThinking, d.includeThinking),
    includeTools: bool(s.includeTools, d.includeTools),
    pdfMethod: /** @type {any} */ (oneOf(s.pdfMethod, ['debugger', 'dialog'], d.pdfMethod)),
    domFallback: bool(s.domFallback, d.domFallback),
    services: {
      chatgpt: bool(s.services?.chatgpt, d.services.chatgpt),
      claude: bool(s.services?.claude, d.services.claude),
    },
    includeChatGptProjects: bool(s.includeChatGptProjects, d.includeChatGptProjects),
    trackActivity: bool(s.trackActivity, d.trackActivity),
    eraseFromDownloadList: bool(s.eraseFromDownloadList, d.eraseFromDownloadList),
    auto: {
      enabled: bool(a.enabled, d.auto.enabled),
      scope: /** @type {any} */ (oneOf(a.scope, ['tracked', 'all'], d.auto.scope)),
      time: /^\d{1,2}:\d{2}$/.test(String(a.time)) ? String(a.time) : d.auto.time,
      days: Array.isArray(a.days) ? [...new Set(a.days.map(Number).filter((n) => Number.isInteger(n) && n >= 0 && n <= 6))] : [],
      formats: legacy ? [...d.auto.formats] : formats(a.formats, d.auto.formats),
      missedRun: /** @type {any} */ (oneOf(a.missedRun, ['auto', 'ask', 'skip'], d.auto.missedRun)),
      maxChatsPerRun: int(a.maxChatsPerRun, d.auto.maxChatsPerRun, 1, 1000),
      delayMs: int(a.delayMs, d.auto.delayMs, 500, 60_000),
      maxAttempts: int(a.maxAttempts, d.auto.maxAttempts, 1, 10),
      backfill: bool(a.backfill, d.auto.backfill),
      baselineAt: typeof a.baselineAt === 'number' ? a.baselineAt : null,
      openTabs: bool(a.openTabs, d.auto.openTabs),
      closeOpenedTabs: bool(a.closeOpenedTabs, d.auto.closeOpenedTabs),
    },
    journal: {
      enabled: bool(s.journal?.enabled, d.journal.enabled),
      maxEntries: int(s.journal?.maxEntries, d.journal.maxEntries, 50, 5000),
    },
    excluded: s.excluded && typeof s.excluded === 'object' ? { ...s.excluded } : {},
  };
}

/** @returns {Promise<Settings>} */
export async function loadSettings() {
  return normalizeSettings(await getKey('settings'));
}

/**
 * @param {any} next
 * @returns {Promise<Settings>}
 */
export async function saveSettings(next) {
  const prev = await loadSettings();
  const normalized = normalizeSettings(next);
  // Enabling auto mode starts the "changed since" window now (no surprise mass export).
  if (normalized.auto.enabled && !prev.auto.enabled) {
    normalized.auto.baselineAt = Date.now();
  } else if (normalized.auto.enabled && !normalized.auto.baselineAt) {
    normalized.auto.baselineAt = prev.auto.baselineAt || Date.now();
  }
  await setKey('settings', normalized);
  return normalized;
}
