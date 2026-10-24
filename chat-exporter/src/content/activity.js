// Activity tracker (Scenario C): marks a chat as "changed" while you work, so the evening
// backup includes it even if the chat list is unavailable.
//
// Streaming answers produce hundreds of DOM mutations per second, so nothing is sent per
// mutation. A chat is reported only after the page has been quiet for QUIET_MS, no answer is
// being generated, and the visible message count or last message actually changed. At most
// one report per chat per MIN_INTERVAL_MS. Only the chat id and title are sent, never text.
import { fnv1a } from '../core/snapshot-merge.js';

const QUIET_MS = 15_000;
const MIN_INTERVAL_MS = 60_000;
const URL_POLL_MS = 1500;

/**
 * @param {{ service: string, dom: import('./dom-adapters.js').DomAdapter, currentChatId: () => string | null, pageTitle: () => string }} ctx
 */
export function startActivityTracker(ctx) {
  /** @type {string | null} */
  let chatId = ctx.currentChatId();
  /** @type {string} */
  let baseline = '';
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let quietTimer;
  /** @type {Map<string, number>} */
  const lastSent = new Map();

  const fingerprint = () => {
    const msgs = ctx.dom.messages();
    const last = msgs[msgs.length - 1];
    return `${msgs.length}:${last ? fnv1a(last.text) : ''}`;
  };

  const resetBaseline = () => {
    chatId = ctx.currentChatId();
    baseline = chatId ? fingerprint() : '';
  };

  const check = () => {
    if (!chatId || ctx.dom.isGenerating()) {
      schedule();
      return;
    }
    const fp = fingerprint();
    if (fp === baseline) return;
    const prev = lastSent.get(chatId) || 0;
    if (Date.now() - prev < MIN_INTERVAL_MS) {
      schedule();
      return;
    }
    baseline = fp;
    lastSent.set(chatId, Date.now());
    chrome.runtime.sendMessage({ type: 'activity', chatId, title: ctx.pageTitle() }).catch(() => {
      // Extension reloaded: this content script is orphaned and stops reporting.
      observer.disconnect();
    });
  };

  const schedule = () => {
    clearTimeout(quietTimer);
    quietTimer = setTimeout(check, QUIET_MS);
  };

  const observer = new MutationObserver(() => {
    if (chatId) schedule();
  });

  const start = () => {
    resetBaseline();
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
  };

  // Single-page app: detect chat switches by polling the URL (cheap, no page hooks needed).
  let lastUrl = location.href;
  setInterval(() => {
    if (location.href !== lastUrl) {
      lastUrl = location.href;
      clearTimeout(quietTimer);
      // Give the new chat time to render before taking the baseline.
      setTimeout(resetBaseline, 3000);
    }
  }, URL_POLL_MS);

  chrome.storage.local.get('settings').then((/** @type {any} */ { settings }) => {
    if (settings && settings.trackActivity === false) return;
    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start, { once: true });
  });
}
