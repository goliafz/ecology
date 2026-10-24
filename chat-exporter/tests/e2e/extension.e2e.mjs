// End-to-end test of the real extension in Chromium with MOCKED ChatGPT and Claude sites.
//
// chatgpt.com and claude.ai are served by Playwright request interception from the synthetic
// fixtures, so this verifies the extension's own machinery (content script injection, messaging,
// normalization, completeness, HTML/Markdown/PDF generation, offscreen blobs, chrome.downloads,
// change detection) — NOT compatibility with the live sites, which needs the manual test plan.
//
// Run: xvfb-run -a node tests/e2e/extension.e2e.mjs   (Playwright and Chromium must be installed)
import { createRequire } from 'node:module';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { chatGptFixture } from '../fixtures/chatgpt-conversation.js';
import { claudeFixture } from '../fixtures/claude-conversation.js';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const EXT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');
const CHATGPT_ID = '11111111-2222-3333-4444-555555555555';
const CLAUDE_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const ORG = '99999999-8888-7777-6666-555555555555';
const CLAUDE_DOM_ID = 'cccccccc-1111-2222-3333-444444444444';
const CANVAS_ID = '6ad9ca9b-c008-83eb-a4a9-d534310d6a17';
const CANVAS_DOC = '# Промт-улучшатель\n\n### Шаг 4. Сделай промпт проверяемым\n\nЗаменяй абстрактные требования конкретными.';

// Shape of the reported chat (from its diagnostics file, text replaced): the answer is a
// ```markdown block that content_references also marks as a client_defined_widget.
function canvasConversation() {
  return {
    title: 'Показ промта улучшателя', create_time: 1792658076, update_time: 1792658164, current_node: 'a', conversation_id: CANVAS_ID,
    mapping: {
      root: { id: 'root', parent: null, children: ['q'], message: null },
      q: { id: 'q', parent: 'root', children: ['a'], message: { id: 'q', author: { role: 'user' }, create_time: 1792658085, content: { content_type: 'text', parts: ['Покажи как составлен промт для улучшения промтов.'] }, status: 'finished_successfully', recipient: 'all' } },
      a: { id: 'a', parent: 'q', children: [], message: { id: 'a', author: { role: 'assistant' }, create_time: 1792658160, recipient: 'all', channel: 'final', status: 'finished_successfully',
        content: { content_type: 'text', parts: [`\`\`\`markdown\n${CANVAS_DOC}\n\`\`\`\n\n`] },
        metadata: { model_slug: 'gpt-6', content_references: [{ type: 'client_defined_widget', category: 'code_block', data: { content: CANVAS_DOC, language: 'markdown', widget_type: 'code_block' }, matched_text: `\`\`\`markdown\n${CANVAS_DOC}\n\`\`\``, start_idx: 0, alt: null }], model_dil_v2: { fallbackMarkdown: `\`\`\`markdown\n${CANVAS_DOC}\n\`\`\`` } } } },
    },
  };
}

// A virtualized message list: only messages near the viewport exist in the DOM.
const VIRTUAL_LIST = `<main id="scroller" style="height:500px;overflow:auto"><div id="spacer" style="position:relative"></div></main>
<script>
const N = 60, H = 120;
const msgs = Array.from({ length: N }, (_, i) => ({ role: i % 2 ? 'assistant' : 'user', text: i % 10 < 2 ? 'Ок' : (i % 2 ? 'Ответ номер ' : 'Вопрос номер ') + i }));
spacer.style.height = N * H + 'px';
function render() {
  const top = scroller.scrollTop;
  const first = Math.max(0, Math.floor(top / H) - 2), last = Math.min(N, Math.ceil((top + 500) / H) + 2);
  spacer.innerHTML = '';
  for (let i = first; i < last; i++) {
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;left:0;right:0;top:' + i * H + 'px;height:' + H + 'px';
    d.innerHTML = msgs[i].role === 'user' ? '<div data-testid="user-message"><p></p></div>' : '<div class="font-claude-response"><p></p></div>';
    d.querySelector('p').textContent = msgs[i].text;
    spacer.appendChild(d);
  }
}
scroller.addEventListener('scroll', render);
render();
</script>`;

const state = { chatgptUpdate: 1761300600, chatgptAnswer: 'Ок' };

function chatGptConversation() {
  const raw = chatGptFixture();
  raw.update_time = state.chatgptUpdate;
  raw.mapping.a3.message.content.parts = [state.chatgptAnswer];
  return raw;
}

const chatPage = (title, body) => `<!DOCTYPE html><html><head><title>${title}</title></head><body><main style="height:400px;overflow:auto">${body}</main></body></html>`;

async function routeSites(context) {
  await context.route('https://chatgpt.com/**', async (route) => {
    const url = new URL(route.request().url());
    const json = (obj) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(obj) });
    if (url.pathname === '/api/auth/session') return json({ accessToken: 'test-token' });
    if (url.pathname === '/backend-api/conversations') {
      return json({ items: [{ id: CHATGPT_ID, title: 'Тестовый чат: код и таблицы', create_time: '2025-10-24T10:00:00Z', update_time: new Date(state.chatgptUpdate * 1000).toISOString() }], total: 1, limit: 100, offset: 0 });
    }
    if (url.pathname.startsWith('/backend-api/gizmos/snorlax/sidebar')) return json({ items: [], cursor: null });
    if (url.pathname === `/backend-api/conversation/${CHATGPT_ID}`) {
      if (route.request().headers().authorization !== 'Bearer test-token') return route.fulfill({ status: 401, body: '{}' });
      return json(chatGptConversation());
    }
    if (url.pathname === `/backend-api/conversation/${CANVAS_ID}`) return json(canvasConversation());
    if (url.pathname.startsWith('/backend-api/files/download/')) return json({ status: 'success', download_url: 'https://chatgpt.com/files/img.png' });
    if (url.pathname === '/files/img.png') return route.fulfill({ status: 200, contentType: 'image/png', body: PNG });
    if (url.pathname === `/c/${CHATGPT_ID}`) {
      return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: chatPage('Тестовый чат: код и таблицы - ChatGPT',
        '<div data-message-author-role="user" data-message-id="u1"><div class="whitespace-pre-wrap">Привет! Покажи пример кода на Python.</div></div>'
        + '<div data-message-author-role="assistant" data-message-id="a1"><div class="markdown"><p>Вот пример:</p></div></div>') });
    }
    return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: chatPage('ChatGPT', '<p>home</p>') });
  });

  await context.route('https://claude.ai/**', async (route) => {
    const url = new URL(route.request().url());
    const json = (obj) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(obj) });
    if (url.pathname === '/api/organizations') return json([{ uuid: ORG, capabilities: ['chat'] }]);
    if (url.pathname === `/api/organizations/${ORG}/chat_conversations`) {
      const offset = Number(url.searchParams.get('offset') || 0);
      return json(offset === 0 ? [{ uuid: CLAUDE_ID, name: 'Артефакты и вложения', updated_at: '2026-10-20T10:10:00.000Z', created_at: '2026-10-20T10:00:00.000Z' }] : []);
    }
    if (url.pathname === `/api/organizations/${ORG}/chat_conversations/${CLAUDE_ID}`) {
      assert.equal(url.searchParams.get('tree'), 'True');
      return json(claudeFixture());
    }
    if (url.pathname === `/api/organizations/${ORG}/chat_conversations/${CLAUDE_DOM_ID}`) {
      return route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
    }
    if (url.pathname === `/chat/${CLAUDE_DOM_ID}`) {
      return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: `<!DOCTYPE html><html><head><title>Длинный чат - Claude</title></head><body>${VIRTUAL_LIST}</body></html>` });
    }
    if (url.pathname === '/api/org/files/1/preview') return route.fulfill({ status: 200, contentType: 'image/png', body: PNG });
    return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: chatPage('Claude', '<p>home</p>') });
  });
}

function listFiles(dir) {
  const out = [];
  const walk = (d) => {
    for (const name of readdirSync(d)) {
      const p = join(d, name);
      if (statSync(p).isDirectory()) walk(p);
      else out.push(p);
    }
  };
  if (existsSync(dir)) walk(dir);
  return out;
}

async function main() {
  const profile = mkdtempSync(join(tmpdir(), 'ce-profile-'));
  const downloads = mkdtempSync(join(tmpdir(), 'ce-downloads-'));
  // Chromium is started directly (not by Playwright) so that chrome.downloads keeps the real
  // file names; Playwright's own launch would redirect downloads to GUID-named files.
  mkdirSync(join(profile, 'Default'), { recursive: true });
  writeFileSync(join(profile, 'Default', 'Preferences'), JSON.stringify({ download: { default_directory: downloads, prompt_for_download: false } }));
  const port = 9300 + Math.floor(Math.random() * 500);
  const proc = spawn(chromium.executablePath(), [
    `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`, '--no-first-run', '--no-default-browser-check', ...(process.getuid?.() === 0 ? ['--no-sandbox'] : []),
    `--disable-extensions-except=${EXT}`, `--load-extension=${EXT}`, 'about:blank',
  ], { stdio: 'ignore' });
  let browser;
  for (let i = 0; i < 50 && !browser; i++) {
    try {
      browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
    } catch {
      await new Promise((r) => setTimeout(r, 300));
    }
  }
  if (!browser) throw new Error('Chromium did not start');
  const context = browser.contexts()[0];
  // Playwright redirects downloads to its artifacts folder; restore Chrome's default behaviour.
  const cdp = await browser.newBrowserCDPSession();
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'default' });
  const errors = [];
  try {
    await routeSites(context);
    const isOurs = (w) => w.url().endsWith('/src/background/service-worker.js');
    let sw = context.serviceWorkers().find(isOurs);
    if (!sw) sw = await context.waitForEvent('serviceworker', { predicate: isOurs, timeout: 15_000 });
    sw.on?.('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
    const extId = new URL(sw.url()).host;
    console.log('extension id', extId);

    // Use a predictable download folder for chrome.downloads.
    const ui = await context.newPage();
    ui.on('console', (m) => { if (m.type() === 'error') errors.push(`[page] ${m.text()}`); });
    await ui.goto(`chrome-extension://${extId}/src/pages/options.html`);
    const call = (type, payload = {}) => ui.evaluate(([t, p]) => chrome.runtime.sendMessage({ type: t, ...p }), [type, payload]);

    // Settings: all formats, tools on.
    const st0 = await call('getState');
    assert.ok(st0.ok, JSON.stringify(st0));
    // Defaults of a fresh install: PDF only, masks %name% / %name%_%date%, no meta.json.
    assert.deepEqual(st0.data.settings.formats, ['pdf']);
    assert.equal(st0.data.settings.writeMeta, false);
    await call('saveSettings', { settings: { ...st0.data.settings, formats: ['md', 'html', 'pdf'], folderMask: '%service%/%name%', writeMeta: true, auto: { ...st0.data.settings.auto, formats: ['md', 'html'], delayMs: 500 } } });

    // Open the ChatGPT chat in a tab (the user's tab).
    const chatTab = await context.newPage();
    await chatTab.goto(`https://chatgpt.com/c/${CHATGPT_ID}`);
    await chatTab.waitForTimeout(1500);

    const waitIdle = async (label) => {
      for (let i = 0; i < 120; i++) {
        const s = await call('getState');
        const q = s.data.queue;
        if (q.pending + q.running === 0 && !q.processing) return s.data;
        await ui.waitForTimeout(500);
      }
      throw new Error(`queue did not finish: ${label}`);
    };

    // 1. Batch export of both chats, all formats.
    let r = await call('exportSelected', { items: [
      { service: 'chatgpt', id: CHATGPT_ID, title: 'ChatGPT test' },
      { service: 'claude', id: CLAUDE_ID, title: 'Claude test' },
    ], formats: ['md', 'html', 'pdf'] });
    assert.ok(r.ok, JSON.stringify(r));
    let s = await waitIdle('batch');
    console.log('jobs:', JSON.stringify(s.jobs.map((j) => ({ key: j.key, status: j.status, outcome: j.outcome, completeness: j.completeness, error: j.error })), null, 1));
    assert.equal(s.queue.failed, 0, 'no failed jobs');
    assert.equal(s.queue.done, 2);

    const items = await sw.evaluate(() => chrome.downloads.search({}));
    const complete = items.filter((i) => i.state === 'complete');
    console.log('downloads:', complete.map((i) => `${i.filename} (${i.fileSize} B)`).join('\n'));
    assert.ok(complete.length >= 8, `expected 8 files (3 formats + meta per chat), got ${complete.length}`);

    // Files on disk (Playwright may store them under GUID names in downloadsPath).
    const disk = listFiles(downloads);
    console.log('files on disk:', disk.length);

    // 2. Content of generated files, located through the download items.
    const byName = (suffix) => complete.find((i) => i.filename.endsWith(suffix) || decodeURIComponent(i.finalUrl || '').endsWith(suffix));
    const readItem = (item) => readFileSync(existsSync(item.filename) ? item.filename : disk.find((p) => statSync(p).size === item.fileSize));
    const metaItems = complete.filter((i) => i.filename.endsWith('.meta.json'));
    assert.equal(metaItems.length, 2, 'two meta files');
    for (const m of metaItems) {
      const meta = JSON.parse(readItem(m).toString('utf8'));
      console.log('meta', meta.service, meta.completeness.status, meta.messages, Object.keys(meta.files));
      assert.ok(['complete', 'complete_with_warnings'].includes(meta.completeness.status));
      assert.deepEqual(Object.keys(meta.files).sort(), ['html', 'md', 'pdf']);
    }
    assert.ok(complete.every((i) => /_\d{4}-\d{2}-\d{2}\.(md|html|pdf|meta\.json)$/.test(i.filename)), 'file names follow %name%_%date%');
    const md = complete.find((i) => i.filename.includes('ChatGPT') && i.filename.endsWith('.md'));
    assert.ok(md, 'ChatGPT markdown saved');
    const mdText = readItem(md).toString('utf8');
    assert.match(mdText, /```python/);
    const pdf = complete.find((i) => i.filename.endsWith('.pdf'));
    assert.ok(pdf && readItem(pdf).subarray(0, 5).toString() === '%PDF-', 'valid PDF header');
    const html = complete.find((i) => i.filename.includes('Claude') && i.filename.endsWith('.html'));
    const htmlText = readItem(html).toString('utf8');
    assert.match(htmlText, /Артефакт: Страница/);
    assert.match(htmlText, /data:image\/png;base64/);

    // 2b. "Export current chat": the active tab is the ChatGPT chat; force-writes even if unchanged.
    await chatTab.bringToFront();
    r = await call('exportCurrent', { formats: ['html'] });
    assert.ok(r.ok, JSON.stringify(r));
    s = await waitIdle('current');
    assert.equal(s.jobs[0].key, `chatgpt:${CHATGPT_ID}`);
    assert.equal(s.jobs[0].outcome, 'saved', 'manual export of the current chat always writes');
    assert.equal(s.lastResult.status, 'complete');

    // 3. Re-export without changes -> unchanged, nothing new written.
    const before = (await sw.evaluate(() => chrome.downloads.search({ state: 'complete' }))).length;
    r = await call('exportSelected', { items: [{ service: 'chatgpt', id: CHATGPT_ID, title: 'ChatGPT test' }], formats: ['md', 'html', 'pdf'] });
    s = await waitIdle('unchanged');
    assert.equal(s.jobs[0].outcome, 'unchanged', 'second export detects no change');
    const after = (await sw.evaluate(() => chrome.downloads.search({ state: 'complete' }))).length;
    assert.equal(after, before, 'no files written for unchanged chat');

    // 4. Marked chats only (default scope): without marks nothing is backed up.
    r = await call('runBackupNow');
    assert.match(r.data.note || '', /Нет помеченных чатов/);
    const iconTitle = () => sw.evaluate(async () => {
      const [tab] = await chrome.tabs.query({ url: 'https://chatgpt.com/c/*' });
      return chrome.action.getTitle({ tabId: tab.id });
    });
    console.log('icon title before:', await iconTitle());
    assert.match(await iconTitle(), /не помечен/, 'red icon on an unmarked chat');
    r = await call('setTracked', { service: 'chatgpt', id: CHATGPT_ID, title: 'Тестовый чат', tracked: true });
    assert.ok(r.ok, JSON.stringify(r));
    assert.match(await iconTitle(), /помечен «Сохранять»/, 'green icon on a marked chat');
    const iconCheck = await sw.evaluate(async () => {
      const [tab] = await chrome.tabs.query({ url: 'https://chatgpt.com/c/*' });
      const out = {};
      for (const p of ['/icons/icon-green16.png', '/icons/icon-red16.png', 'icons/icon-red16.png']) {
        try { await chrome.action.setIcon({ tabId: tab.id, path: { 16: p } }); out[p] = 'ok'; } catch (e) { out[p] = e.message; }
      }
      return out;
    });
    console.log('setIcon paths:', JSON.stringify(iconCheck));
    assert.equal(iconCheck['/icons/icon-green16.png'], 'ok');
    assert.equal(iconCheck['/icons/icon-red16.png'], 'ok');
    const trackedList = (await call('getTracked')).data;
    assert.deepEqual(trackedList.map((t) => t.key), [`chatgpt:${CHATGPT_ID}`]);

    // 4a. Automatic backup run: the marked chat changed on the "server" -> only it is exported;
    //     the Claude chat is not marked and is not even listed.
    state.chatgptUpdate += 3600;
    state.chatgptAnswer = 'Обновлённый ответ';
    r = await call('runBackupNow');
    assert.ok(r.ok, JSON.stringify(r));
    console.log('backup run:', JSON.stringify(r.data));
    s = await waitIdle('auto');
    const autoJobs = s.jobs.map((j) => `${j.key}:${j.status}:${j.outcome || ''}`);
    console.log('auto jobs:', autoJobs);
    assert.ok(s.jobs.some((j) => j.key === `chatgpt:${CHATGPT_ID}` && j.outcome === 'saved'), 'changed ChatGPT chat saved');
    assert.ok(!s.jobs.some((j) => j.key === `claude:${CLAUDE_ID}`), 'unchanged Claude chat not queued');

    // 4b. DOM fallback: the data endpoint fails (503) for a chat open in a tab with a virtualized list.
    const domTab = await context.newPage();
    await domTab.goto(`https://claude.ai/chat/${CLAUDE_DOM_ID}`);
    await domTab.waitForTimeout(1500);
    r = await call('exportSelected', { items: [{ service: 'claude', id: CLAUDE_DOM_ID, title: 'Длинный чат' }], formats: ['md'] });
    s = await waitIdle('dom');
    console.log('dom job:', JSON.stringify(s.jobs[0]));
    assert.equal(s.jobs[0].status, 'done', 'DOM fallback export succeeded');
    assert.equal(s.jobs[0].completeness, 'complete_with_warnings', 'DOM source is flagged with a warning, not as complete');
    const domMd = (await sw.evaluate(() => chrome.downloads.search({ state: 'complete' }))).find((i) => /Длинный чат|Dlinnyy chat/.test(i.filename) && i.filename.endsWith('.md'));
    const domText = readFileSync(domMd.filename, 'utf8');
    const userCount = (domText.match(/^## Пользователь/gm) || []).length;
    const aiCount = (domText.match(/^## Claude/gm) || []).length;
    console.log('DOM export messages:', userCount, aiCount);
    assert.equal(userCount, 30, 'all 30 user messages collected from the virtualized list');
    assert.equal(aiCount, 30, 'all 30 assistant messages collected');
    assert.match(domText, /Вопрос номер 58/);
    assert.match(domText, /Ответ номер 59/);
    assert.equal((domText.match(/^Ок$/gm) || []).length, 12, 'repeated identical messages are kept, not merged');
    await domTab.close();

    // 4c. The reported case: an answer that is one code block widget, PDF + MD, no meta.json.
    const cur = (await call('getState')).data.settings;
    await call('saveSettings', { settings: { ...cur, formats: ['pdf', 'md'], folderMask: '%name%', writeMeta: false } });
    const beforeCanvas = (await sw.evaluate(() => chrome.downloads.search({ state: 'complete' }))).length;
    r = await call('exportSelected', { items: [{ service: 'chatgpt', id: CANVAS_ID, title: 'Показ промта улучшателя' }] });
    s = await waitIdle('canvas');
    console.log('canvas job:', JSON.stringify({ status: s.jobs[0].status, completeness: s.jobs[0].completeness, error: s.jobs[0].error }));
    assert.equal(s.jobs[0].completeness, 'complete');
    const canvasFiles = (await sw.evaluate(() => chrome.downloads.search({ state: 'complete', orderBy: ['-startTime'] }))).slice(0, 2);
    console.log('canvas files:', canvasFiles.map((i) => i.filename));
    assert.equal((await sw.evaluate(() => chrome.downloads.search({ state: 'complete' }))).length - beforeCanvas, 2, 'exactly pdf + md, no meta.json');
    const canvasMd = canvasFiles.find((i) => i.filename.endsWith('.md'));
    assert.match(canvasMd.filename, /ChatBackups[\\/](Показ промта улучшателя|Pokaz promta uluchshatelya)[\\/](Показ промта улучшателя|Pokaz promta uluchshatelya)_\d{4}-\d{2}-\d{2}\.md$/);
    const canvasText = readFileSync(canvasMd.filename, 'utf8');
    assert.match(canvasText, /```markdown\n# Промт-улучшатель/);
    assert.ok(!canvasText.includes('формат не распознан'), 'no raw-format rescue');
    assert.match(canvasText, /Шаг 4\. Сделай промпт проверяемым/);
    const canvasPdf = canvasFiles.find((i) => i.filename.endsWith('.pdf'));
    assert.ok(canvasPdf && canvasPdf.fileSize > 1000);

    // 5. Missed run detection: pretend the 23:00 run was missed while the browser was closed.
    await sw.evaluate(async () => {
      const settings = (await chrome.storage.local.get('settings')).settings;
      settings.auto.enabled = true;
      settings.auto.missedRun = 'ask';
      await chrome.storage.local.set({ settings, schedule: { nextRunAt: Date.now() - 3 * 3600_000, lastRunStartedAt: Date.now() - 27 * 3600_000 } });
    });
    await sw.evaluate(() => globalThis.chatExporterDebug.checkMissedRun());
    const sched = (await call('getState')).data.schedule;
    console.log('schedule after missed check:', JSON.stringify(sched));
    assert.ok(sched.missedPending, 'missed run is reported for confirmation');
    assert.ok(sched.nextRunAt > Date.now(), 'next run rescheduled in the future');
    const alarms = await sw.evaluate(() => chrome.alarms.getAll());
    assert.ok(alarms.some((a) => a.name === 'auto-backup'), 'alarm re-created');

    // 6. Log has entries.
    const log = await call('getLog');
    assert.ok(log.data.length > 0);
    console.log('log entries:', log.data.length, '| last:', log.data[0].message);

    assert.deepEqual(errors.filter((e) => !/Failed to load resource/.test(e)), [], 'no console errors');
    console.log('\nE2E: OK');
  } finally {
    await browser.close().catch(() => {});
    proc.kill();
  }
}

main().catch((err) => {
  console.error('E2E FAILED:', err);
  process.exit(1);
});
