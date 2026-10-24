import { call, COMPLETENESS_TEXT, fmtDate, formatCheckboxes, h, readFormats } from './ui.js';

const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
/** @type {any[]} */
let items = [];
/** Selected keys "service:id" -> item. Survives switching services. */
/** @type {Map<string, any>} */
const selected = new Map();

const keyOf = (/** @type {any} */ it) => `${it.service}:${it.id}`;
const service = () => /** @type {HTMLInputElement} */ (document.querySelector('input[name="service"]:checked')).value;

/** @param {any} it */
function statusOf(it) {
  if (it.excluded) return { text: 'исключён', cls: '' };
  if (it.lastError && (!it.lastExport || it.lastError.at > it.lastExport.at)) return { text: `ошибка: ${it.lastError.message}`, cls: 'err' };
  if (!it.lastExport) return { text: 'не сохранён', cls: '' };
  if (it.lastExport.status === 'incomplete') return { text: 'сохранён НЕПОЛНЫМ', cls: 'err' };
  const changed = it.updatedAt && it.lastExport.updatedAt && Date.parse(it.updatedAt) > Date.parse(it.lastExport.updatedAt);
  if (changed || it.dirty) return { text: `изменён после сохранения (${fmtDate(it.lastExport.at)})`, cls: 'warn' };
  return { text: `сохранён ${fmtDate(it.lastExport.at)} — ${COMPLETENESS_TEXT[/** @type {keyof typeof COMPLETENESS_TEXT} */ (it.lastExport.status)] || ''}`, cls: 'ok' };
}

/** @param {any} it */
function matchesFilter(it) {
  const q = /** @type {HTMLInputElement} */ ($('search')).value.trim().toLowerCase();
  if (q && !String(it.title).toLowerCase().includes(q)) return false;
  const f = /** @type {HTMLSelectElement} */ ($('filter')).value;
  const st = statusOf(it);
  if (f === 'changed') return !it.excluded && (!it.lastExport || st.cls === 'warn');
  if (f === 'never') return !it.lastExport;
  if (f === 'problems') return st.cls === 'err';
  if (f === 'excluded') return it.excluded;
  return true;
}

function visible() {
  return items.filter(matchesFilter);
}

function render() {
  const rows = visible();
  if (!items.length) {
    $('table').replaceChildren(h('p', { class: 'muted', style: 'padding:12px' }, 'Список не загружен. Нажмите «Загрузить список».'));
  } else {
    $('table').replaceChildren(h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, ''), h('th', {}, 'Название'), h('th', {}, 'Изменён'), h('th', {}, 'Резервная копия'), h('th', {}, 'Авто'))),
      h('tbody', {}, ...rows.map((it) => {
        const st = statusOf(it);
        return h('tr', { class: it.excluded ? 'excluded' : '' },
          h('td', {}, h('input', { type: 'checkbox', checked: selected.has(keyOf(it)), onchange: (/** @type {Event} */ e) => {
            if (/** @type {HTMLInputElement} */ (e.target).checked) selected.set(keyOf(it), it);
            else selected.delete(keyOf(it));
            saveSelection();
          } })),
          h('td', { class: 'title' }, it.title, it.project ? h('span', { class: 'muted small' }, ' (проект)') : null),
          h('td', { class: 'small' }, fmtDate(it.updatedAt)),
          h('td', { class: 'small' }, h('span', { class: `badge ${st.cls}` }, st.text)),
          h('td', {}, h('button', { class: 'small', onclick: () => toggleExcluded(it) }, it.excluded ? 'вернуть' : 'исключить')));
      }))));
  }
  $('selected-count').textContent = `Выбрано: ${selected.size}`;
}

async function saveSelection() {
  $('selected-count').textContent = `Выбрано: ${selected.size}`;
  const selection = [...selected.values()].map((it) => ({ service: it.service, id: it.id, title: it.title, updatedAt: it.updatedAt }));
  await chrome.storage.local.set({ selection });
}

/** @param {any} it */
async function toggleExcluded(it) {
  await call('setExcluded', { key: keyOf(it), excluded: !it.excluded, title: it.title, service: it.service });
  it.excluded = !it.excluded;
  render();
}

/** @param {boolean} cached */
async function load(cached) {
  const btn = /** @type {HTMLButtonElement} */ ($('load'));
  btn.disabled = true;
  $('list-info').textContent = cached ? '' : 'Загрузка списка… (для большого числа чатов это может занять минуту)';
  try {
    const res = await call('listChats', { service: service(), cached, allowOpen: /** @type {HTMLInputElement} */ ($('allow-open')).checked });
    items = res.items.sort((/** @type {any} */ a, /** @type {any} */ b) => Date.parse(b.updatedAt || 0) - Date.parse(a.updatedAt || 0));
    if (items.length || !cached) {
      $('list-info').textContent = `Чатов: ${items.length}${res.complete ? '' : ' (список может быть неполным)'} · обновлено ${fmtDate(res.fetchedAt)}${res.warnings?.length ? ` · ${res.warnings.join('; ')}` : ''}`;
    }
  } catch (err) {
    $('list-info').textContent = `Ошибка: ${err instanceof Error ? err.message : err}`;
  } finally {
    btn.disabled = false;
  }
  render();
}

/**
 * @param {'ok' | 'warn' | 'err'} kind
 * @param {string} text
 */
function message(kind, text) {
  $('message').hidden = false;
  $('message').className = `notice ${kind}`;
  $('message').textContent = text;
}

$('load').addEventListener('click', () => load(false));
document.querySelectorAll('input[name="service"]').forEach((el) => el.addEventListener('change', () => load(true)));
$('search').addEventListener('input', render);
$('filter').addEventListener('change', render);
$('select-visible').addEventListener('click', () => {
  for (const it of visible()) selected.set(keyOf(it), it);
  saveSelection();
  render();
});
$('select-none').addEventListener('click', () => {
  selected.clear();
  saveSelection();
  render();
});
$('export').addEventListener('click', async () => {
  const formats = readFormats($('formats'));
  if (!formats.length) return message('err', 'Выберите хотя бы один формат.');
  if (!selected.size) return message('err', 'Не выбрано ни одного чата.');
  try {
    const list = [...selected.values()];
    const res = await call('exportSelected', { items: list, formats, force: /** @type {HTMLInputElement} */ ($('force')).checked });
    message('ok', `В очередь добавлено: ${res.added}. Чаты сохраняются по одному; прогресс — в окне расширения. Эту страницу можно закрыть.`);
  } catch (err) {
    message('err', err instanceof Error ? err.message : String(err));
  }
});

(async () => {
  const state = await call('getState');
  $('formats').replaceChildren(formatCheckboxes(state.settings.formats, 'formats'));
  const { selection } = await chrome.storage.local.get('selection');
  for (const it of Array.isArray(selection) ? selection : []) selected.set(keyOf(it), it);
  await load(true);
})();
