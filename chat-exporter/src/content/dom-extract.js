// DOM fallback extractor (option C of the analysis): scrolls the conversation from the top to
// the bottom, collects messages from every position and aligns the snapshots, so virtualized
// lists that drop off-screen messages are still exported completely. Completeness signals
// (reached top/bottom, snapshot gaps) go into `integrity` and are judged by completeness.js.
import { SCHEMA_VERSION } from '../core/model.js';
import { htmlToMarkdown } from '../core/html-to-markdown.js';
import { mergeSnapshot } from '../core/snapshot-merge.js';
import { ErrorCode, ExportError, sleep } from '../core/util.js';

/**
 * Nearest scrollable ancestor.
 * @param {Element} el
 * @returns {HTMLElement}
 */
export function findScrollContainer(el) {
  /** @type {Element | null} */
  let cur = el.parentElement;
  while (cur && cur !== document.body) {
    const style = getComputedStyle(cur);
    if (/(auto|scroll|overlay)/.test(style.overflowY) && cur.scrollHeight > cur.clientHeight + 4) return /** @type {HTMLElement} */ (cur);
    cur = cur.parentElement;
  }
  return /** @type {HTMLElement} */ (document.scrollingElement || document.documentElement);
}

/**
 * Resolves when the subtree stops changing for `quietMs` (or after `maxMs`).
 * @param {Element} root
 * @param {number} quietMs
 * @param {number} maxMs
 */
export function waitForQuiet(root, quietMs = 400, maxMs = 4000) {
  return new Promise((resolve) => {
    /** @type {ReturnType<typeof setTimeout>} */
    let quietTimer;
    const done = () => {
      observer.disconnect();
      clearTimeout(quietTimer);
      clearTimeout(maxTimer);
      resolve(undefined);
    };
    const observer = new MutationObserver(() => {
      clearTimeout(quietTimer);
      quietTimer = setTimeout(done, quietMs);
    });
    observer.observe(root, { childList: true, subtree: true, characterData: true });
    quietTimer = setTimeout(done, quietMs);
    const maxTimer = setTimeout(done, maxMs);
  });
}

/**
 * Converts <img> to something an exported file can show: data: URLs as-is, same-origin
 * loaded images via canvas; everything else becomes a placeholder.
 * @param {HTMLImageElement} img
 */
function imageSrc(img) {
  const src = img.currentSrc || img.src || '';
  if (src.startsWith('data:image/')) return src;
  if (!img.complete || !img.naturalWidth) return null;
  try {
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;
    ctx.drawImage(img, 0, 0);
    return canvas.toDataURL('image/png'); // throws for cross-origin (tainted) images
  } catch {
    return null;
  }
}

/**
 * @param {import('./dom-adapters.js').DomAdapter} adapter
 * @param {{ service: import('../core/model.js').ServiceId, chatId: string, title: string, url: string, timeoutMs?: number, onProgress?: (text: string) => void }} ctx
 * @returns {Promise<import('../core/model.js').NormalizedChat>}
 */
export async function extractViaDom(adapter, ctx) {
  const deadline = Date.now() + (ctx.timeoutMs || 4 * 60_000);
  const first = adapter.messages()[0];
  if (!first) throw new ExportError(ErrorCode.DOM_CHANGED, 'На странице не найдены сообщения: вёрстка сайта могла измениться (обновите расширение).');
  if (adapter.isGenerating()) throw new ExportError(ErrorCode.INCOMPLETE, 'Ответ ещё генерируется — дождитесь окончания и повторите экспорт.');
  const scroller = findScrollContainer(first.el);
  const original = scroller.scrollTop;
  const progress = ctx.onProgress || (() => {});

  // Phase 1: reach the top. Older messages may load lazily when the top is reached.
  let reachedTop = false;
  let stable = 0;
  let lastFirstKey = '';
  for (let i = 0; i < 200 && Date.now() < deadline; i++) {
    scroller.scrollTop = 0;
    await waitForQuiet(scroller, 500, 4000);
    const firstKey = adapter.messages()[0]?.key || '';
    if (scroller.scrollTop <= 1 && firstKey === lastFirstKey) {
      stable += 1;
      if (stable >= 2) {
        reachedTop = true;
        break;
      }
    } else {
      stable = 0;
    }
    lastFirstKey = firstKey;
    progress(`Поиск начала переписки… (${i + 1})`);
  }

  // Phase 2: walk down, collecting and aligning snapshots.
  /** @type {Array<import('./dom-adapters.js').DomMessage & { markdown: string }>} */
  let collected = [];
  let gaps = 0;
  let reachedBottom = false;
  for (let step = 0; step < 5000 && Date.now() < deadline; step++) {
    const snapshot = adapter.messages().map((m) => ({ ...m, markdown: htmlToMarkdown(m.el, { imageSrc }) }));
    const res = mergeSnapshot(collected, snapshot);
    if (res.gap) gaps += 1;
    collected = res.merged;
    progress(`Собрано сообщений: ${collected.length}`);
    const atBottom = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2;
    if (atBottom) {
      await waitForQuiet(scroller, 600, 4000);
      if (scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2) {
        const last = adapter.messages().map((m) => ({ ...m, markdown: htmlToMarkdown(m.el, { imageSrc }) }));
        const fin = mergeSnapshot(collected, last);
        if (fin.gap) gaps += 1;
        collected = fin.merged;
        reachedBottom = true;
        break;
      }
    }
    scroller.scrollTop += Math.max(200, Math.floor(scroller.clientHeight * 0.7));
    await waitForQuiet(scroller, 350, 3000);
    await sleep(50);
  }
  scroller.scrollTop = original;

  const messages = collected.map((m, i) => ({
    id: m.id || `dom-${i + 1}-${m.key}`,
    parentId: null,
    role: m.role,
    createdAt: null,
    model: null,
    blocks: m.markdown ? [{ type: /** @type {const} */ ('markdown'), text: m.markdown }] : [],
    meta: {},
  })).filter((m) => m.blocks.length > 0);

  return {
    schemaVersion: SCHEMA_VERSION,
    service: ctx.service,
    id: ctx.chatId,
    title: ctx.title,
    url: ctx.url,
    createdAt: null,
    updatedAt: null,
    model: null,
    source: 'dom',
    extractedAt: new Date().toISOString(),
    messages,
    integrity: {
      totalNodes: collected.length,
      pathLength: collected.length,
      brokenLinks: [],
      leafId: null,
      generating: adapter.isGenerating(),
      domMessageCount: collected.length,
      snapshotGaps: gaps,
      reachedTop,
      reachedBottom,
    },
    notes: ['Переписка извлечена со страницы: время сообщений и альтернативные версии недоступны.'],
  };
}
