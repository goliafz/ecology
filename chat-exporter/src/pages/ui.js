// Helpers shared by the extension pages (popup, options, chat list).

/**
 * Calls the service-worker router.
 * @param {string} type
 * @param {Record<string, unknown>} [payload]
 * @returns {Promise<any>}
 */
export async function call(type, payload = {}) {
  const res = await chrome.runtime.sendMessage({ type, ...payload });
  if (!res) throw new Error('Фоновый процесс расширения не ответил');
  if (!res.ok) {
    const err = new Error(res.error?.message || 'Ошибка');
    /** @type {any} */ (err).code = res.error?.code;
    throw err;
  }
  return res.data;
}

/**
 * Tiny element builder. Text children are inserted as text (never as HTML).
 * @param {string} tag
 * @param {Record<string, any>} [attrs]
 * @param {...(Node | string | null | undefined | false)} children
 * @returns {HTMLElement}
 */
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'checked' || k === 'disabled' || k === 'value' || k === 'selected') /** @type {any} */ (el)[k] = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  for (const c of children) {
    if (c === null || c === undefined || c === false) continue;
    el.append(typeof c === 'string' ? document.createTextNode(c) : c);
  }
  return el;
}

/** @param {string | number | null | undefined} value */
export function fmtDate(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export const COMPLETENESS_TEXT = {
  complete: 'полный',
  complete_with_warnings: 'полный, с предупреждениями',
  incomplete: 'НЕПОЛНЫЙ',
};

export const FORMAT_LABELS = { pdf: 'PDF', html: 'HTML', md: 'Markdown' };

/**
 * Reads checked format checkboxes inside a container.
 * @param {ParentNode} root
 * @returns {string[]}
 */
export function readFormats(root) {
  return Array.from(root.querySelectorAll('input[data-format]:checked')).map((i) => /** @type {HTMLElement} */ (i).dataset.format || '');
}

/**
 * Renders format checkboxes.
 * @param {string[]} selected
 * @param {string} name
 */
export function formatCheckboxes(selected, name) {
  return h('div', { class: 'formats' }, ...Object.entries(FORMAT_LABELS).map(([f, label]) =>
    h('label', { class: 'chip' }, h('input', { type: 'checkbox', 'data-format': f, name, checked: selected.includes(f) }), ` ${label}`)));
}
