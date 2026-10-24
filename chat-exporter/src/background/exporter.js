// Export pipeline for one chat:
//   fetch (content script, API) -> normalize -> [DOM fallback] -> completeness -> change check
//   -> render md/html/pdf -> write files -> write meta.json last -> update index.
import { chatKey, SERVICES } from '../adapters/registry.js';
import { evaluateCompleteness, STATUS_LABELS } from '../core/completeness.js';
import { buildChatFolder, buildFilePaths } from '../core/filenames.js';
import { chatContentHash } from '../core/hash.js';
import { chatToHtml } from '../core/html.js';
import { chatToMarkdown } from '../core/markdown.js';
import { ErrorCode, ExportError } from '../core/util.js';
import { addLog } from './log.js';
import { htmlToPdf, openPrintDialog } from './pdf.js';
import { getIndex, updateIndexEntry } from './storage.js';
import { ensureContentScript, getServiceTab, isOwnTab, navigateOwnTab, sendToTab, withTabLease } from './tabs.js';
import { writeFile } from './downloads.js';

const VERSION = chrome.runtime.getManifest().version;

/**
 * @typedef {Object} ExportRequest
 * @property {import('../core/model.js').ServiceId} service
 * @property {string} id
 * @property {string} [title]
 * @property {import('../core/model.js').ExportFormat[]} formats
 * @property {'manual' | 'batch' | 'auto'} reason
 * @property {string | null} [expectedUpdatedAt]
 * @property {boolean} [force] write even if unchanged
 * @property {number} [preferTabId] tab to use (manual export of the active tab)
 */

/**
 * @typedef {Object} ExportResult
 * @property {'saved' | 'unchanged' | 'skipped' | 'dialog'} outcome
 * @property {import('../core/model.js').CompletenessStatus} [status]
 * @property {string[]} warnings
 * @property {Record<string, string>} files format -> absolute path
 * @property {string} title
 * @property {number} [messages]
 */

/**
 * Fetches and normalizes a chat. Uses the data endpoint; falls back to the DOM if allowed.
 * @param {ExportRequest} req
 * @param {import('./settings.js').Settings} settings
 * @param {(text: string) => void} progress
 * @returns {Promise<import('../core/model.js').NormalizedChat>}
 */
export async function fetchNormalizedChat(req, settings, progress) {
  const service = SERVICES[req.service];
  const allowOpen = req.reason !== 'manual' ? settings.auto.openTabs : true;
  let tabId;
  if (req.preferTabId !== undefined) {
    // The active tab of a manual export; it may predate the extension (no content script yet)
    // or be gone after a browser restart.
    try {
      await ensureContentScript(req.preferTabId);
      tabId = req.preferTabId;
    } catch {
      tabId = await getServiceTab(req.service, { allowOpen });
    }
  } else {
    tabId = await getServiceTab(req.service, { allowOpen });
  }

  progress('Загрузка переписки…');
  /** @type {any} */
  let fetched;
  /** @type {ExportError | null} */
  let apiError = null;
  try {
    fetched = await sendToTab(tabId, {
      cmd: 'fetchChat',
      id: req.id,
      includeImages: settings.includeImages,
      maxImageBytes: settings.maxImageBytes,
    }, 4 * 60_000);
  } catch (err) {
    apiError = err instanceof ExportError ? err : new ExportError(ErrorCode.UNKNOWN, String(err));
  }

  if (fetched) {
    let chat;
    try {
      chat = service.normalize(fetched.raw, {
        includeThinking: settings.includeThinking,
        includeTools: settings.includeTools,
        images: fetched.images,
        imageErrors: fetched.imageErrors,
        pageGenerating: fetched.page?.chatId === req.id && fetched.page?.generating,
        fallbackId: req.id,
      });
    } catch (err) {
      apiError = new ExportError(ErrorCode.BAD_RESPONSE, `Формат данных изменился: ${err instanceof Error ? err.message : err}`);
    }
    if (chat) {
      if (fetched.page?.chatId === req.id) crossCheck(chat, fetched.page);
      return chat;
    }
  }

  // Authentication, missing chat and rate limits are not DOM problems.
  const err = /** @type {ExportError} */ (apiError);
  const domUseful = settings.domFallback && ![ErrorCode.NOT_AUTHENTICATED, ErrorCode.NOT_FOUND, ErrorCode.RATE_LIMITED, ErrorCode.FORBIDDEN].includes(/** @type {any} */ (err.code));
  if (!domUseful) throw err;

  const page = await sendToTab(tabId, { cmd: 'ping' }, 5000).catch(() => null);
  if (page?.chatId !== req.id) {
    if (await isOwnTab(tabId)) {
      progress('Открытие чата во вкладке для резервного извлечения…');
      await navigateOwnTab(tabId, service.chatUrl(req.id));
    } else {
      throw new ExportError(err.code, `${err.message}. Резервное извлечение со страницы возможно, только если чат открыт во вкладке.`);
    }
  }
  progress('Резервный режим: прокрутка и извлечение со страницы…');
  await addLog('warn', `Данные API недоступны (${err.message}); используется извлечение со страницы`, { chat: chatKey(req.service, req.id) });
  const dom = await sendToTab(tabId, { cmd: 'extractDom' }, 4.5 * 60_000);
  return /** @type {import('../core/model.js').NormalizedChat} */ (dom);
}

/**
 * Compares the message ids visible in the open tab with the exported branch.
 * @param {import('../core/model.js').NormalizedChat} chat
 * @param {{ messageIds?: string[], userMessageCount?: number }} page
 */
function crossCheck(chat, page) {
  if (Array.isArray(page.messageIds) && page.messageIds.length > 0 && Array.isArray(chat.integrity.pathIds)) {
    const known = new Set(chat.integrity.pathIds);
    chat.integrity.crossCheckMissing = page.messageIds.filter((id) => !known.has(id));
  } else if (typeof page.userMessageCount === 'number' && page.userMessageCount > 0) {
    const apiUsers = chat.messages.filter((m) => m.role === 'user').length;
    chat.integrity.crossCheckMissing = page.userMessageCount > apiUsers ? [`page-user-messages:${page.userMessageCount}>${apiUsers}`] : [];
  }
}

/**
 * Exports one chat and records the result in the index.
 * @param {ExportRequest} req
 * @param {import('./settings.js').Settings} settings
 * @param {(text: string) => void} [progress]
 * @returns {Promise<ExportResult>}
 */
export function exportChat(req, settings, progress = () => {}) {
  return withTabLease(() => exportChatInner(req, settings, progress));
}

/**
 * @param {ExportRequest} req
 * @param {import('./settings.js').Settings} settings
 * @param {(text: string) => void} progress
 * @returns {Promise<ExportResult>}
 */
async function exportChatInner(req, settings, progress) {
  const key = chatKey(req.service, req.id);
  const index = await getIndex();
  const entry = index[key];

  const chat = await fetchNormalizedChat(req, settings, progress);
  chat.completeness = evaluateCompleteness(chat, { expectedUpdatedAt: req.expectedUpdatedAt || entry?.updatedAt || null });
  const report = chat.completeness;
  const contentHash = await chatContentHash(chat);
  const warnings = [...report.warnings, ...chat.notes];

  // Never let an incomplete automatic export replace anything; retry later instead.
  if (report.status === 'incomplete' && req.reason === 'auto') {
    throw new ExportError(ErrorCode.INCOMPLETE, `Экспорт неполный, файлы не записаны: ${report.warnings.join('; ')}`);
  }

  const last = entry?.lastExport;
  // Same content as the last export: write only formats that do not exist yet.
  const sameContent = Boolean(last && last.contentHash === contentHash && last.status !== 'incomplete');
  const formats = !req.force && sameContent ? req.formats.filter((f) => !last.formats.includes(f)) : req.formats;
  if (formats.length === 0) {
    await updateIndexEntry(key, (e) => e && { ...e, title: chat.title, dirty: false, failures: 0, lastError: undefined, nextRetryAt: undefined });
    return { outcome: 'unchanged', status: last.status, warnings: [], files: {}, title: chat.title, messages: chat.messages.length };
  }
  // Protection against overwriting a newer copy with an older snapshot.
  if (!req.force && last?.updatedAt && chat.updatedAt && Date.parse(chat.updatedAt) < Date.parse(last.updatedAt)) {
    await addLog('warn', 'Получена более старая версия чата, чем уже сохранённая — файлы не перезаписаны', { chat: key, title: chat.title });
    return { outcome: 'skipped', status: report.status, warnings: ['Получена более старая версия, чем сохранённая.'], files: {}, title: chat.title };
  }

  const now = new Date();
  // The folder is remembered per chat (dated files stay together after a rename) and recomputed
  // only when the folder settings change. Folders of other chats are avoided.
  const folderKey = `${settings.rootFolder}|${settings.folderMask}`;
  const taken = new Set(Object.values(index).filter((e) => e !== entry && e.folder).map((e) => /** @type {string} */ (e.folder)));
  const folder = entry?.folder && entry.folderKey === folderKey ? entry.folder : buildChatFolder(settings, chat, now, taken);
  const paths = buildFilePaths(folder, { fileMask: settings.fileMask }, chat, formats, now);
  const incompleteSuffix = report.status === 'incomplete' ? '_НЕПОЛНЫЙ' : '';
  const writeOpts = { conflictAction: settings.onConflict, eraseFromList: settings.eraseFromDownloadList };
  /** @type {Record<string, import('./storage.js').ExportedFile>} */
  const written = {};
  /** @type {Record<string, string>} */
  const absolute = {};
  let outcome = /** @type {ExportResult['outcome']} */ ('saved');

  for (const format of formats) {
    const path = /** @type {string} */ (paths.files[format]).replace(/(\.\w+)$/, `${incompleteSuffix}$1`);
    progress(`Сохранение ${format.toUpperCase()}…`);
    let res;
    if (format === 'md') {
      res = await writeFile(path, chatToMarkdown(chat, { images: settings.mdImages }), 'text/markdown;charset=utf-8', writeOpts);
    } else if (format === 'html') {
      res = await writeFile(path, chatToHtml(chat, { generator: `Chat Exporter ${VERSION}` }), 'text/html;charset=utf-8', writeOpts);
    } else {
      const printable = chatToHtml(chat, { forPrint: true, generator: `Chat Exporter ${VERSION}` });
      if (settings.pdfMethod === 'dialog') {
        if (req.reason === 'auto') {
          warnings.push('PDF пропущен: в настройках выбрана печать через диалог, она невозможна в автоматическом режиме.');
          continue;
        }
        await openPrintDialog(printable, chat.title);
        outcome = 'dialog';
        continue;
      }
      progress('Генерация PDF…');
      res = await writeFile(path, await htmlToPdf(printable, chat.title), 'application/pdf', { ...writeOpts, timeoutMs: 180_000 });
    }
    written[format] = { path: res.path, sha256: res.sha256, bytes: res.bytes, absolutePath: res.absolutePath };
    absolute[format] = res.absolutePath;
  }

  // Optional meta.json, written last: its presence marks a finished export set.
  const allFiles = { ...(sameContent ? last.files : {}), ...written };
  if (settings.writeMeta && Object.keys(written).length > 0) {
    const meta = {
      generator: `Chat Exporter ${VERSION}`,
      schemaVersion: chat.schemaVersion,
      service: chat.service,
      id: chat.id,
      title: chat.title,
      url: chat.url,
      createdAt: chat.createdAt,
      updatedAt: chat.updatedAt,
      exportedAt: chat.extractedAt,
      source: chat.source,
      messages: chat.messages.length,
      contentHash,
      completeness: report,
      notes: chat.notes,
      integrity: { ...chat.integrity, pathIds: undefined },
      files: Object.fromEntries(Object.entries(allFiles).map(([f, w]) => [f, { file: w.path.split('/').pop(), sha256: w.sha256, bytes: w.bytes }])),
    };
    const metaRes = await writeFile(paths.meta.replace(/\.meta\.json$/, `${incompleteSuffix}.meta.json`), `${JSON.stringify(meta, null, 2)}\n`, 'application/json;charset=utf-8', { ...writeOpts, conflictAction: 'overwrite' });
    absolute.meta = metaRes.absolutePath;
  }

  await updateIndexEntry(key, (e) => ({
    ...(e || { service: req.service, id: req.id, updatedAt: chat.updatedAt }),
    service: req.service,
    id: req.id,
    title: chat.title,
    folder,
    folderKey,
    updatedAt: chat.updatedAt || e?.updatedAt || null,
    dirty: false,
    failures: 0,
    lastError: undefined,
    nextRetryAt: undefined,
    // An incomplete export never replaces the record of the last good copy.
    lastExport: Object.keys(written).length > 0 && (report.status !== 'incomplete' || !e?.lastExport) ? {
      at: Date.now(),
      updatedAt: chat.updatedAt,
      contentHash,
      status: report.status,
      formats: /** @type {import('../core/model.js').ExportFormat[]} */ ([...new Set([...(sameContent ? last.formats : []), ...Object.keys(written)])]),
      files: allFiles,
      messages: chat.messages.length,
      source: chat.source,
    } : e?.lastExport,
  }));

  await addLog(report.status === 'complete' ? 'info' : 'warn',
    `Экспорт (${Object.keys(written).join(', ') || 'нет файлов'}): ${STATUS_LABELS[report.status]}, сообщений ${chat.messages.length}`,
    { chat: key, title: chat.title });
  return { outcome, status: report.status, warnings, files: absolute, title: chat.title, messages: chat.messages.length };
}
