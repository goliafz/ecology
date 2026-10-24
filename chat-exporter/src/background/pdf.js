// PDF generation from the prepared HTML.
//
// "debugger" method: the HTML is loaded into an extension page (src/pages/print.html) in a
// background tab, then Page.printToPDF of the Chrome DevTools Protocol is called through
// chrome.debugger. While attached, Chrome shows the bar "<extension> started debugging this
// browser" — this is a Chrome safety feature and cannot be hidden.
// "dialog" method: the same page opens the regular print dialog; the user saves manually.
import { ErrorCode, ExportError, sleep, withTimeout } from '../core/util.js';
import { base64ToBytes } from './downloads.js';
import { openBackgroundTab } from './tabs.js';
import { updateKey } from './storage.js';

/** @type {Map<string, { html: string, title: string, ready: (tabId: number) => void }>} */
const jobs = new Map();

/** Called by the router for messages from the print page. */
export const printMessageHandlers = {
  /** @param {{ job: string }} msg */
  getHtml(msg) {
    const job = jobs.get(msg.job);
    if (!job) throw new ExportError(ErrorCode.PDF_FAILED, 'Задание печати не найдено (истекло?)');
    return { html: job.html, title: job.title };
  },
  /** @param {{ job: string }} msg @param {chrome.runtime.MessageSender} sender */
  ready(msg, sender) {
    const job = jobs.get(msg.job);
    if (job && sender.tab?.id !== undefined) job.ready(sender.tab.id);
    return { ok: true };
  },
};

export function isDebuggerAvailable() {
  return typeof chrome.debugger?.attach === 'function';
}

/**
 * Waits for the print page's "ready" message; if it does not arrive, asks the page directly
 * through the debugger (the flag is set right before the message is sent).
 * @param {number} tabId
 * @param {Promise<unknown>} readyMessage
 */
async function waitUntilReady(tabId, readyMessage) {
  const deadline = Date.now() + 60_000;
  const gotMessage = await Promise.race([readyMessage.then(() => true), sleep(15_000).then(() => false)]);
  if (gotMessage) return;
  while (Date.now() < deadline) {
    try {
      const res = /** @type {any} */ (await chrome.debugger.sendCommand({ tabId }, 'Runtime.evaluate', {
        expression: "document.documentElement.dataset.ceReady === '1'",
        returnByValue: true,
      }));
      if (res?.result?.value === true) return;
    } catch {
      // the page may still be navigating
    }
    const done = await Promise.race([readyMessage.then(() => true), sleep(1000).then(() => false)]);
    if (done) return;
  }
  throw new ExportError(ErrorCode.PDF_FAILED, 'Страница для PDF не подготовилась за 60 с. Попробуйте ещё раз; если повторится — выберите в настройках печать через окно.');
}

const FOOTER = '<div style="width:100%;font-size:8px;color:#888;text-align:center;font-family:sans-serif"><span class="pageNumber"></span> / <span class="totalPages"></span></div>';

/**
 * @param {string} html complete document (print variant)
 * @param {string} title
 * @returns {Promise<Uint8Array>}
 */
export async function htmlToPdf(html, title) {
  if (!isDebuggerAvailable()) {
    throw new ExportError(ErrorCode.PDF_FAILED, 'Разрешение "debugger" недоступно: автоматический PDF невозможен. Выберите в настройках печать через диалог.');
  }
  const job = crypto.randomUUID();
  /** @type {(tabId: number) => void} */
  let onReady = () => {};
  const readyPromise = new Promise((resolve) => {
    onReady = resolve;
  });
  jobs.set(job, { html, title, ready: onReady });
  let tabId;
  let attached = false;
  try {
    // The tab is opened blank and the debugger attached first: with focus emulation the
    // background tab behaves like a visible one (rendering is not paused).
    tabId = await openBackgroundTab('about:blank');
    await chrome.debugger.attach({ tabId }, '1.3');
    attached = true;
    await chrome.debugger.sendCommand({ tabId }, 'Emulation.setFocusEmulationEnabled', { enabled: true }).catch(() => {});
    await chrome.tabs.update(tabId, { url: chrome.runtime.getURL(`src/pages/print.html?job=${job}`) });
    await waitUntilReady(tabId, readyPromise);
    const result = /** @type {{ data: string }} */ (await withTimeout(chrome.debugger.sendCommand({ tabId }, 'Page.printToPDF', {
      printBackground: true,
      preferCSSPageSize: true,
      displayHeaderFooter: true,
      headerTemplate: '<span></span>',
      footerTemplate: FOOTER,
      generateDocumentOutline: true,
      generateTaggedPDF: true,
    }), 240_000, 'генерация PDF'));
    if (!result?.data) throw new ExportError(ErrorCode.PDF_FAILED, 'Chrome вернул пустой PDF');
    return base64ToBytes(result.data);
  } catch (err) {
    if (err instanceof ExportError) throw err;
    throw new ExportError(ErrorCode.PDF_FAILED, `Ошибка генерации PDF: ${err instanceof Error ? err.message : err}`);
  } finally {
    jobs.delete(job);
    if (attached && tabId !== undefined) await chrome.debugger.detach({ tabId }).catch(() => {});
    if (tabId !== undefined) {
      await chrome.tabs.remove(tabId).catch(() => {});
      const closed = tabId;
      await updateKey('openedTabs', (list) => (Array.isArray(list) ? list.filter((x) => x !== closed) : []), 'session');
    }
  }
}

/**
 * Opens the print dialog for manual saving (no debugger needed). Resolves when the page is shown.
 * @param {string} html
 * @param {string} title
 */
export async function openPrintDialog(html, title) {
  const job = crypto.randomUUID();
  jobs.set(job, { html, title, ready: () => {} });
  // The page keeps the HTML only until it has fetched it.
  setTimeout(() => jobs.delete(job), 5 * 60_000);
  const tab = await chrome.tabs.create({ url: chrome.runtime.getURL(`src/pages/print.html?job=${job}&dialog=1`), active: true });
  return tab.id;
}
