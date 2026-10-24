import { call, fmtDate, formatCheckboxes, h, readFormats } from './ui.js';
import { buildChatFolder, buildFilePaths, MASK_TOKENS } from '../core/filenames.js';

const form = /** @type {HTMLFormElement} */ (document.getElementById('form'));
const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
const field = (/** @type {string} */ name) => /** @type {HTMLInputElement} */ (form.elements.namedItem(name));
const DAYS = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб'];
/** @type {any} */
let settings = null;

function setRadio(/** @type {string} */ name, /** @type {string} */ value) {
  form.querySelectorAll(`input[name="${name}"]`).forEach((el) => {
    /** @type {HTMLInputElement} */ (el).checked = /** @type {HTMLInputElement} */ (el).value === value;
  });
}
function getRadio(/** @type {string} */ name) {
  const el = /** @type {HTMLInputElement | null} */ (form.querySelector(`input[name="${name}"]:checked`));
  return el ? el.value : '';
}

function fill() {
  const s = settings;
  $('formats').replaceChildren(formatCheckboxes(s.formats, 'formats'));
  $('auto-formats').replaceChildren(formatCheckboxes(s.auto.formats, 'auto-formats'));
  field('rootFolder').value = s.rootFolder;
  field('folderMask').value = s.folderMask;
  field('fileMask').value = s.fileMask;
  setRadio('onConflict', s.onConflict);
  field('writeMeta').checked = s.writeMeta;
  updatePreview();
  field('eraseFromDownloadList').checked = s.eraseFromDownloadList;
  field('includeImages').checked = s.includeImages;
  field('maxImageMb').value = String(Math.round((s.maxImageBytes / 1024 / 1024) * 10) / 10);
  field('mdEmbed').checked = s.mdImages === 'embed';
  field('includeTools').checked = s.includeTools;
  field('includeThinking').checked = s.includeThinking;
  field('domFallback').checked = s.domFallback;
  setRadio('pdfMethod', s.pdfMethod);
  field('svc-chatgpt').checked = s.services.chatgpt;
  field('svc-claude').checked = s.services.claude;
  field('includeChatGptProjects').checked = s.includeChatGptProjects;
  field('trackActivity').checked = s.trackActivity;
  field('auto-enabled').checked = s.auto.enabled;
  field('auto-time').value = s.auto.time.padStart(5, '0');
  $('days').replaceChildren(...DAYS.map((d, i) => h('label', { class: 'chip' }, h('input', { type: 'checkbox', 'data-day': String(i), checked: s.auto.days.includes(i) }), ` ${d}`)));
  /** @type {HTMLSelectElement} */ (form.elements.namedItem('auto-missedRun')).value = s.auto.missedRun;
  field('auto-maxChatsPerRun').value = String(s.auto.maxChatsPerRun);
  field('auto-delaySec').value = String(s.auto.delayMs / 1000);
  field('auto-maxAttempts').value = String(s.auto.maxAttempts);
  field('auto-backfill').checked = s.auto.backfill;
  field('auto-openTabs').checked = s.auto.openTabs;
  field('auto-closeOpenedTabs').checked = s.auto.closeOpenedTabs;
  /** @type {HTMLInputElement} */ ($('journal-enabled')).checked = s.journal.enabled;
  renderExcluded();
}

/** Shows where a chat would be saved with the masks currently typed in. */
function updatePreview() {
  const chat = { service: /** @type {const} */ ('chatgpt'), id: '6ad9ca9b-c008-83eb-a4a9-d534310d6a17', title: 'Показ промта улучшателя', updatedAt: new Date().toISOString() };
  const folder = buildChatFolder({ rootFolder: field('rootFolder').value, folderMask: field('folderMask').value }, chat, new Date());
  const format = readFormats($('formats'))[0] || 'pdf';
  const path = buildFilePaths(folder, { fileMask: field('fileMask').value }, chat, [/** @type {any} */ (format)], new Date()).files[format];
  $('mask-preview').textContent = `Загрузки/${path}`;
}

$('mask-tokens').replaceChildren('Подстановки: ', ...Object.entries(MASK_TOKENS).flatMap(([k, v], i) => [i ? ', ' : '', h('code', {}, `%${k}%`), ` — ${v}`]));
for (const name of ['rootFolder', 'folderMask', 'fileMask']) field(name).addEventListener('input', updatePreview);
$('formats').addEventListener('change', updatePreview);

function renderExcluded() {
  const entries = Object.entries(settings.excluded || {});
  if (!entries.length) {
    $('excluded').replaceChildren(h('p', { class: 'muted small' }, 'Нет исключённых чатов.'));
    return;
  }
  $('excluded').replaceChildren(h('table', {}, h('tbody', {}, ...entries.map(([key, v]) => h('tr', {},
    h('td', {}, /** @type {any} */ (v).service === 'claude' ? 'Claude' : 'ChatGPT'),
    h('td', {}, /** @type {any} */ (v).title || key),
    h('td', {}, h('button', { type: 'button', class: 'small', onclick: async () => {
      settings = await call('setExcluded', { key, excluded: false });
      renderExcluded();
    } }, 'Вернуть')))))));
}

function collect() {
  const days = Array.from($('days').querySelectorAll('input[data-day]:checked')).map((i) => Number(/** @type {HTMLElement} */ (i).dataset.day));
  return {
    ...settings,
    formats: readFormats($('formats')),
    rootFolder: field('rootFolder').value,
    folderMask: field('folderMask').value,
    fileMask: field('fileMask').value,
    onConflict: getRadio('onConflict'),
    writeMeta: field('writeMeta').checked,
    eraseFromDownloadList: field('eraseFromDownloadList').checked,
    includeImages: field('includeImages').checked,
    maxImageBytes: Math.round(Number(field('maxImageMb').value) * 1024 * 1024),
    mdImages: field('mdEmbed').checked ? 'embed' : 'omit',
    includeTools: field('includeTools').checked,
    includeThinking: field('includeThinking').checked,
    domFallback: field('domFallback').checked,
    pdfMethod: getRadio('pdfMethod'),
    services: { chatgpt: field('svc-chatgpt').checked, claude: field('svc-claude').checked },
    includeChatGptProjects: field('includeChatGptProjects').checked,
    trackActivity: field('trackActivity').checked,
    auto: {
      ...settings.auto,
      enabled: field('auto-enabled').checked,
      time: field('auto-time').value || '23:00',
      days,
      formats: readFormats($('auto-formats')),
      missedRun: /** @type {HTMLSelectElement} */ (form.elements.namedItem('auto-missedRun')).value,
      maxChatsPerRun: Number(field('auto-maxChatsPerRun').value),
      delayMs: Math.round(Number(field('auto-delaySec').value) * 1000),
      maxAttempts: Number(field('auto-maxAttempts').value),
      backfill: field('auto-backfill').checked,
      openTabs: field('auto-openTabs').checked,
      closeOpenedTabs: field('auto-closeOpenedTabs').checked,
    },
    journal: { ...settings.journal, enabled: /** @type {HTMLInputElement} */ ($('journal-enabled')).checked },
  };
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    const next = collect();
    if (!next.formats.length || !next.auto.formats.length) throw new Error('Выберите хотя бы один формат');
    settings = await call('saveSettings', { settings: next });
    fill();
    $('save-status').textContent = `Сохранено ${new Date().toLocaleTimeString('ru-RU')}`;
  } catch (err) {
    $('save-status').textContent = `Ошибка: ${err instanceof Error ? err.message : err}`;
  }
});

$('reset').addEventListener('click', async () => {
  if (!confirm('Сбросить все настройки к значениям по умолчанию?')) return;
  settings = await call('resetSettings');
  fill();
});

$('journal-enabled').addEventListener('change', async () => {
  settings = await call('saveSettings', { settings: { ...settings, journal: { ...settings.journal, enabled: /** @type {HTMLInputElement} */ ($('journal-enabled')).checked } } });
});

$('clear-log').addEventListener('click', async () => {
  await call('clearLog');
  await renderLog();
});

$('clear-index').addEventListener('click', async () => {
  if (!confirm('Очистить метаданные экспортов? Файлы на диске останутся.')) return;
  await call('clearIndex');
  await renderStats();
});

async function renderLog() {
  const log = await call('getLog');
  if (!log.length) {
    $('log-table').replaceChildren(h('p', { class: 'muted small' }, 'Журнал пуст.'));
    return;
  }
  $('log-table').replaceChildren(h('table', {},
    h('thead', {}, h('tr', {}, h('th', {}, 'Время'), h('th', {}, 'Чат'), h('th', {}, 'Событие'))),
    h('tbody', {}, ...log.slice(0, 300).map((/** @type {any} */ e) => h('tr', { class: `lvl-${e.level}` },
      h('td', { class: 'small' }, fmtDate(e.at)),
      h('td', { class: 'small' }, e.title || e.chat || ''),
      h('td', {}, e.message + (e.code ? ` [${e.code}]` : '')))))));
}

async function renderStats() {
  const s = await call('getIndexStats');
  $('index-stats').textContent = `Чатов в индексе: ${s.total}, сохранено: ${s.exported}, с ошибками: ${s.failed}, исчезло из списка: ${s.missing}.`;
}

(async () => {
  const state = await call('getState');
  settings = state.settings;
  $('debugger-warning').hidden = state.debuggerAvailable;
  fill();
  await renderLog();
  await renderStats();
  if (location.hash === '#log') $('log').scrollIntoView();
})();
