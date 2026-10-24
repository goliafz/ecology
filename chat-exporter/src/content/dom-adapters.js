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
    return Array.from(document.querySelectorAll('[data-message-id]'))
      .map((el) => el.getAttribute('data-message-id') || '')
      .filter(Boolean);
  },
  userMessageCount() {
    return document.querySelectorAll('[data-message-author-role="user"]').length;
  },
};

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
