// PDF generation in a REAL background tab. Unlike Playwright (which emulates focus for every
// page it controls), this test talks raw CDP to the extension service worker only, so the
// print tab is throttled exactly as in a user's browser.
// Run: xvfb-run -a node tests/e2e/pdf-background.e2e.mjs
import { createRequire } from 'node:module';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const EXT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const port = 9800 + Math.floor(Math.random() * 100);
const proc = spawn(chromium.executablePath(), [
  `--user-data-dir=${mkdtempSync(join(tmpdir(), 'ce-pdf-'))}`, `--remote-debugging-port=${port}`, '--no-first-run',
  '--no-default-browser-check', ...(process.getuid?.() === 0 ? ['--no-sandbox'] : []),
  `--disable-extensions-except=${EXT}`, `--load-extension=${EXT}`, 'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  let targets = [];
  for (let i = 0; i < 60; i++) {
    try {
      targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      if (targets.some((t) => t.url.endsWith('/src/background/service-worker.js'))) break;
    } catch { /* starting */ }
    await sleep(300);
  }
  const sw = targets.find((t) => t.url.endsWith('/src/background/service-worker.js'));
  if (!sw) throw new Error('service worker not found');
  const ws = new WebSocket(sw.webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const send = (method, params) => new Promise((r) => { pending.set(++id, r); ws.send(JSON.stringify({ id, method, params })); });
  for (let i = 0; i < 50; i++) {
    const r = await send('Runtime.evaluate', { expression: 'typeof globalThis.chatExporterDebug', returnByValue: true });
    if (r.result?.result?.value === 'object') break;
    await sleep(200);
  }
  const html = '<!DOCTYPE html><html><head><meta charset="utf-8"><title>t</title><style>body{font:14px sans-serif}</style></head><body><h1>Проверка PDF</h1>' + '<p>Строка текста для нескольких страниц.</p>'.repeat(400) + '</body></html>';
  const started = Date.now();
  const res = await send('Runtime.evaluate', {
    expression: `globalThis.chatExporterDebug.htmlToPdf(${JSON.stringify(html)}, 't').then((b) => ({ ok: true, bytes: b.length, head: String.fromCharCode(...b.slice(0, 5)) }), (e) => ({ ok: false, error: e.message }))`,
    awaitPromise: true, returnByValue: true,
  });
  const value = res.result?.result?.value;
  if (!value) console.log('raw:', JSON.stringify(res).slice(0, 500));
  console.log('result:', JSON.stringify(value), `${Math.round((Date.now() - started) / 1000)} s`);
  ws.close();
  if (!value?.ok || value.head !== '%PDF-') throw new Error('PDF generation failed in a background tab');
  console.log('PDF-BACKGROUND: OK');
}
main().catch((e) => { console.error('PDF-BACKGROUND FAILED:', e.message); process.exitCode = 1; }).finally(() => proc.kill());
