import { call, COMPLETENESS_TEXT, fmtDate, FORMAT_LABELS, h } from './ui.js';

const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
const DAYS = ['вс', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'];
/** @type {any} */
let state = null;

async function refresh() {
  try {
    state = await call('getState');
  } catch (err) {
    showResult('err', `Нет связи с фоновым процессом: ${err instanceof Error ? err.message : err}`);
    return;
  }
  render();
}

function render() {
  const { settings, queue, schedule, lastResult, tab } = state;

  // Current chat
  const cur = $('current-chat');
  cur.replaceChildren();
  if (tab?.service && tab.chatId) {
    cur.append(h('span', { class: 'muted' }, `${tab.service === 'chatgpt' ? 'ChatGPT' : 'Claude'}: `), h('strong', {}, tab.title || tab.chatId));
  } else if (tab?.service) {
    cur.append(h('span', { class: 'muted' }, 'Откройте конкретный чат, чтобы экспортировать его.'));
  } else {
    cur.append(h('span', { class: 'muted' }, 'Активная вкладка — не ChatGPT и не Claude.'));
  }
  /** @type {HTMLButtonElement} */ ($('export-current')).disabled = !(tab?.service && tab.chatId);
  $('track-row').hidden = !(tab?.service && tab.chatId);
  /** @type {HTMLInputElement} */ ($('track-toggle')).checked = Boolean(tab?.tracked);
  $('track-auto').hidden = !(tab?.tracked && tab.trackedAuto);

  // Formats are chosen in the settings only.
  const names = settings.formats.map((/** @type {keyof typeof FORMAT_LABELS} */ f) => FORMAT_LABELS[f]).join(', ');
  $('formats').replaceChildren(`Формат: ${names} · `, h('button', { class: 'link small', onclick: () => chrome.runtime.openOptionsPage() }, 'изменить'));
  /** @type {HTMLButtonElement} */ ($('save-raw')).disabled = !(tab?.service && tab.chatId);

  // Auto mode
  /** @type {HTMLInputElement} */ ($('auto-toggle')).checked = settings.auto.enabled;
  $('auto-scope').textContent = settings.auto.scope === 'tracked' ? `помеченные чаты (${state.trackedCount})` : 'все изменившиеся чаты';
  const days = settings.auto.days.length ? settings.auto.days.map((/** @type {number} */ d) => DAYS[d]).join(', ') : 'ежедневно';
  const parts = [`${days} в ${settings.auto.time}`];
  if (settings.auto.enabled && schedule.nextRunAt) parts.push(`следующий запуск: ${fmtDate(schedule.nextRunAt)}`);
  if (schedule.lastRunStartedAt) parts.push(`последний: ${fmtDate(schedule.lastRunStartedAt)}${schedule.lastRunSummary ? ` — ${schedule.lastRunSummary}` : ''}`);
  $('auto-info').textContent = parts.join(' · ');

  // Missed run
  const missed = schedule.missedPending;
  $('missed').hidden = !missed;
  if (missed) $('missed-text').textContent = `Резервное копирование ${fmtDate(missed.dueAt)} не выполнено: браузер был закрыт или компьютер спал.`;

  // Queue progress
  const active = queue.pending + queue.running > 0;
  $('status').hidden = !active && !queue.listing;
  if (!active && queue.listing) {
    $('status-title').textContent = 'Резервное копирование: поиск изменившихся чатов';
    $('progress-bar').style.width = '0%';
    $('progress-text').textContent = queue.listing;
  }
  if (active) {
    const finished = queue.done + queue.failed;
    $('status-title').textContent = `${queue.run?.reason === 'auto' ? 'Резервное копирование' : 'Экспорт'}: ${finished} из ${queue.total}`;
    $('progress-bar').style.width = `${queue.total ? Math.round((finished / queue.total) * 100) : 0}%`;
    $('progress-text').textContent = queue.progress || (queue.current ? queue.current.title : 'Ожидание…');
  }
  /** @type {HTMLButtonElement} */ ($('backup-now')).disabled = active || Boolean(queue.listing);

  // Result
  if (!active && queue.run?.finishedAt && queue.total > 1) {
    const kind = queue.failed || queue.incomplete ? 'warn' : 'ok';
    const failedJobs = state.jobs.filter((/** @type {any} */ j) => j.status === 'failed');
    showResult(kind, `Готово: сохранено ${queue.done - queue.unchanged}, без изменений ${queue.unchanged}, ошибок ${queue.failed}, неполных ${queue.incomplete}.`,
      failedJobs.slice(0, 5).map((/** @type {any} */ j) => `${j.title}: ${j.error?.message || 'ошибка'}`));
  } else if (!active && lastResult) {
    renderLastResult(lastResult);
  } else if (active) {
    $('result').hidden = true;
  }
}

/** @param {any} r */
function renderLastResult(r) {
  if (r.outcome === 'error') {
    showResult('err', `«${r.title}»: ${r.error?.message || 'ошибка'}`);
  } else if (r.outcome === 'unchanged') {
    showResult('ok', `«${r.title}»: изменений нет, файлы не перезаписаны.`);
  } else if (r.outcome === 'dialog') {
    showResult('ok', `«${r.title}»: открыт диалог печати — сохраните PDF вручную.`);
  } else {
    const kind = r.status === 'incomplete' ? 'err' : r.status === 'complete_with_warnings' ? 'warn' : 'ok';
    const files = Object.entries(r.files || {}).filter(([k]) => k !== 'meta').map(([, p]) => String(p).split(/[\\/]/).pop());
    showResult(kind, `«${r.title}» — ${COMPLETENESS_TEXT[/** @type {keyof typeof COMPLETENESS_TEXT} */ (r.status)] || ''}, сообщений: ${r.messages ?? '?'}. Файлы: ${files.join(', ') || '—'}`,
      (r.warnings || []).slice(0, 6));
  }
}

/**
 * @param {'ok' | 'warn' | 'err'} kind
 * @param {string} text
 * @param {string[]} [list]
 */
function showResult(kind, text, list = []) {
  const box = $('result');
  box.hidden = false;
  box.className = `notice ${kind}`;
  box.replaceChildren(h('div', {}, text));
  if (list.length) box.append(h('ul', {}, ...list.map((t) => h('li', {}, t))));
}

/** @param {() => Promise<any>} fn */
async function guarded(fn) {
  try {
    await fn();
  } catch (err) {
    showResult('err', err instanceof Error ? err.message : String(err));
  }
  await refresh();
}

$('export-current').addEventListener('click', () => guarded(async () => {
  await call('exportCurrent');
}));

$('choose-chats').addEventListener('click', () => {
  chrome.tabs.create({ url: chrome.runtime.getURL('src/pages/chats.html') });
});

$('export-selected').addEventListener('click', () => guarded(async () => {
  const { selection } = await chrome.storage.local.get('selection');
  const items = Array.isArray(selection) ? selection : [];
  if (!items.length) {
    chrome.tabs.create({ url: chrome.runtime.getURL('src/pages/chats.html') });
    throw new Error('Чаты не выбраны: отметьте их на открывшейся странице.');
  }
  await call('exportSelected', { items });
}));

$('auto-toggle').addEventListener('change', (e) => guarded(async () => {
  await call('setAuto', { enabled: /** @type {HTMLInputElement} */ (e.target).checked });
}));

$('backup-now').addEventListener('click', () => guarded(async () => {
  const res = await call('runBackupNow');
  if (res.queued === 0) showResult(res.errors.length ? 'err' : 'ok', res.errors.length ? res.errors.join('; ') : res.note || 'Изменившихся чатов нет.');
}));

$('save-raw').addEventListener('click', () => guarded(async () => {
  const res = await call('saveRawCurrent');
  showResult('ok', `Данные чата сохранены для диагностики: ${res.path}. Файл содержит текст переписки — передавайте его только тому, кому доверяете.`);
}));

$('track-toggle').addEventListener('change', (e) => guarded(async () => {
  const tracked = /** @type {HTMLInputElement} */ (e.target).checked;
  await call('setTracked', { tracked });
  showResult('ok', tracked
    ? 'Чат помечен «Сохранять»: значок стал зелёным, чат будет сохраняться автоматически при изменениях.'
    : 'Пометка снята: значок стал красным, чат больше не сохраняется автоматически.');
}));

$('cancel').addEventListener('click', () => guarded(() => call('cancel')));
$('run-missed').addEventListener('click', () => guarded(() => call('runMissed')));
$('dismiss-missed').addEventListener('click', () => guarded(() => call('dismissMissed')));
$('open-options').addEventListener('click', () => chrome.runtime.openOptionsPage());
$('open-log').addEventListener('click', () => chrome.tabs.create({ url: chrome.runtime.getURL('src/pages/options.html#log') }));
$('version').textContent = `v${chrome.runtime.getManifest().version}`;

chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === 'state-changed') refresh();
  return false;
});
setInterval(refresh, 2000);
refresh();
