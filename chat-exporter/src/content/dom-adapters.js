// Service-specific DOM knowledge for the fallback extractor and the activity tracker.
//
// IMPORTANT: page markup is not an API. The selectors below were collected from public
// userscripts/exporters (ChatGPT: [data-message-author-role], data-message-id,
// [data-testid^="conversation-turn-"]; Claude: [data-testid="user-message"], .font-claude-response)
// and must be re-checked after site updates. Each adapter lists several candidates.
import { fnv1a } from '../core/snapshot-merge.js';

/**
 * @typedef {Object} DomMessage
 * @property {string} key  stable key for snapshot alignment
 * @property {string | null} id service message id if the page exposes one
 * @property {'user' | 'assistant'} role
 * @property {Element} el  content element to convert
 * @property {string} text plain text (for keys and length comparison)
 */

/**
 * @typedef {Object} DomAdapter
 * @property {() => DomMessage[]} messages ordered messages currently in the DOM
 * @property {() => boolean} isGenerating
 * @property {() => string[]} messageIds ids visible in the DOM (for the API cross-check)
 * @property {() => number} userMessageCount
 */

/** @param {Element[]} list */
function outermost(list) {
  return list.filter((el) => !list.some((other) => other !== el && other.contains(el)));
}

/** @param {Element} el */
function textOf(el) {
  return (/** @type {HTMLElement} */ (el).innerText ?? el.textContent ?? '').trim();
}

/** @type {DomAdapter} */
export const chatGptDom = {
  messages() {
    const nodes = outermost(Array.from(document.querySelectorAll('[data-message-author-role]')));
    if (nodes.length === 0) {
      const units = chatGptSearchUnits();
      return units.length ? units : chatGptTurns();
    }
    return nodes.map((node) => {
      const roleAttr = node.getAttribute('data-message-author-role');
      const role = roleAttr === 'user' ? 'user' : 'assistant';
      const id = node.getAttribute('data-message-id');
      const content = node.querySelector('.markdown') || node.querySelector('.whitespace-pre-wrap') || node;
      const text = textOf(content);
      return { key: id ? `id:${id}` : `${role}:${fnv1a(text)}`, id, role, el: content, text };
    });
  },
  isGenerating() {
    return Boolean(document.querySelector('[data-testid="stop-button"], button[aria-label*="Stop" i], .result-streaming'));
  },
  messageIds() {
    const ids = Array.from(document.querySelectorAll('[data-message-id]')).map((el) => el.getAttribute('data-message-id') || '');
    for (const el of document.querySelectorAll('[data-chatgpt-search-message-ids]')) ids.push(...splitIds(el.getAttribute('data-chatgpt-search-message-ids')));
    return [...new Set(ids.filter(Boolean))];
  },
  userMessageCount() {
    const direct = document.querySelectorAll('[data-message-author-role="user"]').length;
    if (direct) return direct;
    const units = chatGptSearchUnits();
    return (units.length ? units : chatGptTurns()).filter((m) => m.role === 'user').length;
  },
};

/** @param {string | null} value */
function splitIds(value) {
  return String(value || '').split(/[\s,]+/).filter(Boolean);
}

/**
 * ChatGPT markup seen in October 2026 (diagnostics of a real chat): each message is wrapped in
 * an element with data-chatgpt-search-unit-key="<turn>:<n>:<role>" and
 * data-chatgpt-search-message-ids="<message ids>". Verified for the user role; the "assistant"
 * suffix is assumed by analogy.
 * @returns {DomMessage[]}
 */
function chatGptSearchUnits() {
  const units = outermost(Array.from(document.querySelectorAll('[data-chatgpt-search-unit-key]')));
  return units.flatMap((unit) => {
    const roleAttr = String(unit.getAttribute('data-chatgpt-search-unit-key') || '').split(':').pop();
    if (roleAttr !== 'user' && roleAttr !== 'assistant') return [];
    const role = /** @type {'user' | 'assistant'} */ (roleAttr);
    const ids = splitIds(unit.getAttribute('data-chatgpt-search-message-ids'));
    const content = role === 'user'
      ? unit.querySelector('.whitespace-pre-wrap') || unit
      : unit.querySelector('.markdown') || unit;
    const text = textOf(content);
    return [{ key: ids.length ? `id:${ids.join(',')}` : `${role}:${fnv1a(text)}`, id: ids[ids.length - 1] || null, role, el: content, text }];
  });
}

/**
 * Fallback for ChatGPT markup without data-message-author-role: conversation turns marked with
 * data-testid="conversation-turn-N" and the role in data-turn. Unverified on the current site.
 * @returns {DomMessage[]}
 */
function chatGptTurns() {
  return Array.from(document.querySelectorAll('[data-testid^="conversation-turn-"]')).flatMap((turn) => {
    const roleAttr = turn.getAttribute('data-turn') || turn.querySelector('[data-turn]')?.getAttribute('data-turn');
    if (roleAttr !== 'user' && roleAttr !== 'assistant') return [];
    const role = /** @type {'user' | 'assistant'} */ (roleAttr);
    const content = turn.querySelector('.markdown') || turn.querySelector('.whitespace-pre-wrap') || turn;
    const text = textOf(content);
    return [{ key: `${role}:${fnv1a(text)}`, id: null, role, el: content, text }];
  });
}

const PROBE_SELECTORS = [
  '[data-message-author-role]', '[data-message-id]', '[data-chatgpt-search-unit-key]', '[data-chatgpt-search-message-ids]',
  '[data-content-search-turn-key]', '[data-user-message-bubble]', '[data-talvt-turn-state]', '[data-testid^="conversation-turn-"]', '[data-turn]',
  'article', '.markdown', '[data-testid="user-message"]', '.font-claude-response', '[data-is-streaming]',
];

/** @param {Element} el */
function describeElement(el) {
  const attrs = Array.from(el.attributes)
    .filter((a) => a.name !== 'style')
    .map((a) => `${a.name}="${a.value.slice(0, a.name === 'class' ? 80 : 50)}"`);
  return `<${el.tagName.toLowerCase()} ${attrs.join(' ')}>`.replace(/ >$/, '>');
}

/**
 * Structure snapshot for diagnostics: how many elements each known selector finds, and the tags
 * and attributes (never the text) of the elements around the first user message.
 * @param {string} sampleText text of the first user message, used only to locate it
 */
export function domProbe(sampleText) {
  /** @type {Record<string, number>} */
  const counts = {};
  for (const sel of PROBE_SELECTORS) counts[sel] = document.querySelectorAll(sel).length;
  /** @type {string[]} */
  const ancestors = [];
  const needle = String(sampleText || '').trim().slice(0, 40);
  if (needle && document.body) {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node && !(node.textContent || '').includes(needle)) node = walker.nextNode();
    let el = node ? node.parentElement : null;
    for (let i = 0; el && el !== document.body && i < 14; i++, el = el.parentElement) ancestors.push(describeElement(el));
    if (!node) ancestors.push('(первое сообщение пользователя на странице не найдено)');
  }
  return { counts, ancestors };
}

const CLAUDE_USER = '[data-testid="user-message"]';
const CLAUDE_ASSISTANT = '.font-claude-response, .font-claude-message';

/** @type {DomAdapter} */
export const claudeDom = {
  messages() {
    const nodes = outermost(Array.from(document.querySelectorAll(`${CLAUDE_USER}, ${CLAUDE_ASSISTANT}`)));
    return nodes.map((el) => {
      const role = el.matches(CLAUDE_USER) ? 'user' : 'assistant';
      const text = textOf(el);
      return { key: `${role}:${fnv1a(text)}`, id: null, role, el, text };
    });
  },
  isGenerating() {
    return Boolean(document.querySelector('[data-is-streaming="true"]'));
  },
  messageIds() {
    return [];
  },
  userMessageCount() {
    return document.querySelectorAll(CLAUDE_USER).length;
  },
};

/** @param {string} service */
export function domAdapterFor(service) {
  return service === 'chatgpt' ? chatGptDom : claudeDom;
}
