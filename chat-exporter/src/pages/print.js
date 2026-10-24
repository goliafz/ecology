// Print page: receives the prepared export HTML from the service worker, shows it, waits for
// images and fonts, then either reports "ready" (automatic PDF via chrome.debugger) or opens
// the print dialog (manual mode). The exported HTML contains no scripts; only its <style>,
// <title> and <body> are adopted.

const params = new URLSearchParams(location.search);
const job = params.get('job') || '';
const dialog = params.get('dialog') === '1';

async function main() {
  const res = await chrome.runtime.sendMessage({ type: 'print:getHtml', job });
  if (!res?.ok) throw new Error(res?.error?.message || 'Документ не получен');
  const doc = new DOMParser().parseFromString(res.data.html, 'text/html');
  doc.querySelectorAll('script, iframe, object, embed, link, meta[http-equiv]').forEach((el) => el.remove());

  document.title = doc.title || res.data.title || 'Chat export';
  for (const style of Array.from(doc.querySelectorAll('style'))) {
    document.head.append(document.importNode(style, true));
  }
  document.documentElement.lang = doc.documentElement.lang || 'ru';
  document.body.replaceWith(document.importNode(doc.body, true));

  await Promise.all(Array.from(document.images, (img) => img.decode().catch(() => {})));
  if (document.fonts?.ready) await document.fonts.ready;
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

  if (dialog) {
    window.print();
  } else {
    await chrome.runtime.sendMessage({ type: 'print:ready', job });
  }
}

main().catch((err) => {
  const p = document.getElementById('print-status') || document.body.appendChild(document.createElement('p'));
  p.textContent = `Ошибка подготовки PDF: ${err instanceof Error ? err.message : err}`;
});
