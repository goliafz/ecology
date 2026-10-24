import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sanitizeSegment, expandTemplate, buildChatFolder, buildFilePaths, isSafeRelativePath, shortId } from '../src/core/filenames.js';
import { canonicalJson, sha256Hex, chatContentHash } from '../src/core/hash.js';
import { mergeSnapshot, fnv1a } from '../src/core/snapshot-merge.js';
import { computeNextRun, isRunMissed, parseTime } from '../src/core/schedule-math.js';
import { backoffDelay, httpError, ErrorCode, ExportError, serializeError, withTimeout, toIsoSafe } from './helpers.js';
import { normalizeChatGpt } from '../src/adapters/chatgpt/normalize.js';
import { chatGptFixture } from './fixtures/chatgpt-conversation.js';

test('sanitizeSegment: forbidden chars, reserved names, length, Cyrillic', () => {
  assert.equal(sanitizeSegment('a/b\\c:d*e?f"g<h>i|j'), 'a b c d e f g h i j');
  assert.equal(sanitizeSegment('  ..hidden  '), 'hidden');
  assert.equal(sanitizeSegment('CON'), '_CON');
  assert.equal(sanitizeSegment('nul.txt'), '_nul.txt');
  assert.equal(sanitizeSegment('Привет, мир!'), 'Привет, мир!');
  assert.equal(sanitizeSegment(''), 'untitled');
  assert.equal(sanitizeSegment('...'), 'untitled');
  assert.equal(sanitizeSegment('ends with dot.'), 'ends with dot');
  assert.equal(sanitizeSegment('x'.repeat(200)).length, 80);
  const emoji = sanitizeSegment('a'.repeat(79) + '😀');
  assert.ok(!/[\uD800-\uDBFF]$/.test(emoji), 'no lone surrogate');
  assert.equal(sanitizeSegment('tab\tnew\nline'), 'tab new line');
  assert.equal(sanitizeSegment('cite\uE200x'), 'citex');
});

test('expandTemplate: no traversal, slashes in values do not create folders', () => {
  assert.equal(expandTemplate('{service}/{title}', { service: 'ChatGPT', title: '../../etc/passwd' }), 'ChatGPT/etc passwd');
  assert.equal(expandTemplate('../{a}/./b', { a: 'x' }), 'x/b');
  assert.equal(expandTemplate('%service%/%name%', { service: 'ChatGPT', name: 'a/b' }), 'ChatGPT/a b');
});

test('masks: default %name%/%name%_%date%, dated history, collisions, legacy {tokens}', () => {
  const now = new Date(2026, 9, 24, 23, 5);
  const chat = { service: /** @type {const} */ ('chatgpt'), id: '11111111-2222-3333', title: 'Мой чат: план', updatedAt: '2026-10-20T10:00:00Z' };
  const folder = buildChatFolder({ rootFolder: 'ChatBackups', folderMask: '%name%' }, chat, now);
  assert.equal(folder, 'ChatBackups/Мой чат план');
  const p = buildFilePaths(folder, { fileMask: '%name%_%date%' }, chat, ['pdf'], now);
  assert.deepEqual(p.files, { pdf: 'ChatBackups/Мой чат план/Мой чат план_2026-10-24.pdf' });
  assert.equal(p.meta, 'ChatBackups/Мой чат план/Мой чат план_2026-10-24.meta.json');
  const next = buildFilePaths(folder, { fileMask: '%name%_%date%' }, chat, ['pdf'], new Date(2026, 9, 25, 9, 0));
  assert.equal(next.files.pdf, 'ChatBackups/Мой чат план/Мой чат план_2026-10-25.pdf', 'next day -> new file');
  assert.equal(buildChatFolder({ rootFolder: 'ChatBackups', folderMask: '%name%' }, chat, now, new Set([folder])), 'ChatBackups/Мой чат план (11111111)', 'another chat owns the folder');
  assert.equal(buildChatFolder({ rootFolder: 'ChatBackups', folderMask: '' }, chat, now), 'ChatBackups', 'empty mask: one common folder');
  assert.equal(buildChatFolder({ rootFolder: 'Архив', folderMask: '%service%/%name%' }, chat, now), 'Архив/ChatGPT/Мой чат план');
  assert.equal(buildFilePaths(folder, { fileMask: '%date%_%time% %name% (%updated%)' }, chat, ['md'], now).files.md, `${folder}/2026-10-24_23-05 Мой чат план (2026-10-20).md`);
  assert.equal(buildFilePaths(folder, { fileMask: '{title}_{date}' }, chat, ['md'], now).files.md, `${folder}/Мой чат план_2026-10-24.md`, 'legacy tokens');
  assert.equal(buildFilePaths(folder, { fileMask: '%nmae%' }, chat, ['md'], now).files.md, `${folder}/nmae.md`, 'typo stays visible');
  assert.ok(isSafeRelativePath(p.files.pdf || ''));
  assert.ok(!isSafeRelativePath('/abs/x'));
  assert.ok(!isSafeRelativePath('a/../b'));
  assert.ok(!isSafeRelativePath('C:/x'));
  assert.equal(shortId('--'), 'noid');
});

test('hash: canonical JSON and content hash ignores volatile data', async () => {
  assert.equal(canonicalJson({ b: 1, a: [1, { d: 2, c: 3 }], u: undefined }), '{"a":[1,{"c":3,"d":2}],"b":1}');
  assert.equal(await sha256Hex('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  const a = normalizeChatGpt(chatGptFixture(), { nowIso: '2026-01-01T00:00:00Z' });
  const b = normalizeChatGpt(chatGptFixture(), { nowIso: '2026-02-02T00:00:00Z', images: { 'sediment://file_img1': 'data:image/png;base64,AA==' } });
  assert.equal(await chatContentHash(a), await chatContentHash(b), 'extraction time and embedded bytes do not count');
  const raw = chatGptFixture();
  raw.mapping.a3.message.content.parts = ['Изменённый ответ'];
  assert.notEqual(await chatContentHash(a), await chatContentHash(normalizeChatGpt(raw)));
});

const item = (/** @type {string} */ key) => ({ key, text: key });

test('mergeSnapshot: overlapping snapshots in order', () => {
  let r = mergeSnapshot([], ['a', 'b', 'c'].map(item));
  r = mergeSnapshot(r.merged, ['b', 'c', 'd', 'e'].map(item));
  assert.equal(r.gap, false);
  assert.equal(r.overlap, 2);
  r = mergeSnapshot(r.merged, ['e', 'f'].map(item));
  assert.deepEqual(r.merged.map((x) => x.key), ['a', 'b', 'c', 'd', 'e', 'f']);
});

test('mergeSnapshot: duplicates keep their positions, contained snapshot adds nothing', () => {
  let r = mergeSnapshot([], ['ok', 'x', 'ok', 'y'].map(item));
  r = mergeSnapshot(r.merged, ['ok', 'y', 'ok', 'z'].map(item));
  assert.deepEqual(r.merged.map((x) => x.key), ['ok', 'x', 'ok', 'y', 'ok', 'z']);
  r = mergeSnapshot(r.merged, ['x', 'ok'].map(item));
  assert.equal(r.merged.length, 6);
});

test('mergeSnapshot: gap detected, older messages prepended', () => {
  const r = mergeSnapshot(['a', 'b'].map(item), ['x', 'y'].map(item));
  assert.equal(r.gap, true);
  const r2 = mergeSnapshot(['c', 'd'].map(item), ['a', 'b', 'c'].map(item));
  assert.deepEqual(r2.merged.map((x) => x.key), ['a', 'b', 'c', 'd']);
  const r3 = mergeSnapshot([{ key: 'a', text: 'shor' }], [{ key: 'a', text: 'shorter text' }]);
  assert.equal(r3.merged[0].text, 'shorter text');
  assert.equal(fnv1a('abc'), fnv1a('abc'));
  assert.notEqual(fnv1a('abc'), fnv1a('abd'));
});

test('schedule: next run, days filter, missed detection', () => {
  const now = new Date(2026, 9, 24, 22, 0); // Saturday
  assert.deepEqual(parseTime('7:05'), { h: 7, m: 5 });
  assert.deepEqual(parseTime('bad'), { h: 23, m: 0 });
  const next = computeNextRun(now, '23:00');
  assert.equal(next.getDate(), 24);
  assert.equal(next.getHours(), 23);
  const after = computeNextRun(new Date(2026, 9, 24, 23, 0), '23:00');
  assert.equal(after.getDate(), 25, 'exactly at the time -> next day');
  const weekdays = computeNextRun(now, '23:00', [1, 2, 3, 4, 5]);
  assert.equal(weekdays.getDay(), 1, 'Saturday evening -> Monday');
  const t = next.getTime();
  assert.equal(isRunMissed({ nextRunAt: t, lastRunStartedAt: null }, t + 60_000), false, 'within grace');
  assert.equal(isRunMissed({ nextRunAt: t, lastRunStartedAt: null }, t + 3600_000), true);
  assert.equal(isRunMissed({ nextRunAt: t, lastRunStartedAt: t + 1000 }, t + 3600_000), false);
  assert.equal(isRunMissed({ nextRunAt: null }, t), false);
});

test('util: backoff, http errors, serialization, timeout', async () => {
  assert.equal(backoffDelay(0, 1000, 60_000, () => 0), 500);
  assert.equal(backoffDelay(10, 1000, 60_000, () => 1), 60_000);
  const e = httpError(429, 'list', '7');
  assert.equal(e.code, ErrorCode.RATE_LIMITED);
  assert.equal(e.retryAfterMs, 7000);
  assert.equal(e.transient, true);
  assert.equal(httpError(404, 'x').transient, false);
  assert.equal(httpError(401, 'x').code, ErrorCode.NOT_AUTHENTICATED);
  assert.deepEqual(serializeError(new ExportError('X', 'y')), { code: 'X', message: 'y', retryAfterMs: undefined });
  assert.equal(serializeError(new Error('boom')).code, ErrorCode.UNKNOWN);
  await assert.rejects(withTimeout(new Promise(() => {}), 20, 'тест'), (err) => /** @type {any} */ (err).code === ErrorCode.TIMEOUT);
  assert.equal(toIsoSafe(1761300000), '2025-10-24T10:00:00.000Z');
});

test('asciiPath: transliteration fallback keeps structure and id', async () => {
  const { asciiPath } = await import('../src/core/filenames.js');
  assert.equal(asciiPath('ChatBackups/ChatGPT/Тестовый чат  код и таблицы__11111111/chat.md'), 'ChatBackups/ChatGPT/Testovyy chat kod i tablitsy__11111111/chat.md');
  assert.equal(asciiPath('Щука Ёж/x.md'), 'Shchuka Ezh/x.md');
  assert.equal(asciiPath('日本/a.md'), '__/a.md');
});

test('verify-backups: detects missing, modified and incomplete files', async () => {
  const { mkdtempSync, writeFileSync, mkdirSync } = await import('node:fs');
  const { tmpdir } = await import('node:os');
  const { join } = await import('node:path');
  const { createHash } = await import('node:crypto');
  const { verifyBackups } = await import('../tools/verify-backups.mjs');
  const dir = mkdtempSync(join(tmpdir(), 'ce-verify-'));
  const chat = join(dir, 'ChatGPT', 'x__1');
  mkdirSync(chat, { recursive: true });
  const body = '# hi\n';
  writeFileSync(join(chat, 'chat.md'), body);
  writeFileSync(join(chat, 'chat.html'), 'tampered');
  const sha = (s) => createHash('sha256').update(s).digest('hex');
  writeFileSync(join(chat, 'chat.meta.json'), JSON.stringify({
    title: 'x', completeness: { status: 'incomplete' },
    files: { md: { file: 'chat.md', sha256: sha(body), bytes: Buffer.byteLength(body) }, html: { file: 'chat.html', sha256: sha('orig!!!!'), bytes: 8 }, pdf: { file: 'chat.pdf', sha256: 'x', bytes: 1 } },
  }));
  const r = verifyBackups(dir);
  assert.equal(r.ok, 1);
  assert.equal(r.problems.length, 2);
  assert.ok(r.problems.some((p) => p.includes('контрольная сумма')));
  assert.ok(r.problems.some((p) => p.includes('отсутствует')));
  assert.equal(r.incomplete.length, 1);
});

test('images load in parallel within a time budget; the rest get a note', async () => {
  const { loadImages } = await import('../src/content/http.js');
  const delay = (ms, v) => new Promise((resolve) => setTimeout(() => resolve(v), ms));
  let active = 0;
  let maxActive = 0;
  const keys = Array.from({ length: 8 }, (_, i) => `img${i}`);
  const t0 = Date.now();
  const res = await loadImages(keys, async (k) => {
    active += 1;
    maxActive = Math.max(maxActive, active);
    await delay(100);
    active -= 1;
    return `data:${k}`;
  });
  assert.equal(Object.keys(res.images).length, 8);
  assert.equal(maxActive, 4, 'four at a time');
  assert.ok(Date.now() - t0 < 400, 'parallel, not one by one');
  // A hanging image is cut by its own timeout; images after the budget are not started.
  const slow = await loadImages(['a', 'b', 'c'], (k) => (k === 'a' ? new Promise(() => {}) : delay(700, k)), { concurrency: 1, budgetMs: 2200, perImageMs: 800 });
  assert.match(slow.imageErrors.a, /Превышено время ожидания/);
  assert.equal(slow.images.b, 'b', JSON.stringify(slow));
  assert.match(slow.imageErrors.c, /превышено общее время/);
});

test('cancellation helpers', async () => {
  const { abortable, sleepUnlessCancelled, ErrorCode } = await import('../src/core/util.js');
  const ctrl = new AbortController();
  const never = new Promise(() => {});
  const p = abortable(never, ctrl.signal);
  ctrl.abort();
  await assert.rejects(p, (err) => /** @type {any} */ (err).code === ErrorCode.CANCELLED);
  assert.equal(await abortable(Promise.resolve(5), new AbortController().signal), 5);
  const c2 = new AbortController();
  const t0 = Date.now();
  setTimeout(() => c2.abort(), 50);
  await sleepUnlessCancelled(5000, c2.signal);
  assert.ok(Date.now() - t0 < 1000);
});

test('DOM cross-check: page ids of another scheme with no more messages than the data pass', async () => {
  const { evaluateCompleteness } = await import('../src/core/completeness.js');
  const base = { schemaVersion: 1, service: 'chatgpt', id: 'x', title: 't', url: '', createdAt: null, updatedAt: null, model: null, source: 'api', extractedAt: '', notes: [],
    messages: [{ id: 'u', parentId: null, role: 'user', createdAt: null, model: null, blocks: [{ type: 'markdown', text: 'q' }], meta: {} }, { id: 'a', parentId: 'u', role: 'assistant', createdAt: null, model: null, blocks: [{ type: 'markdown', text: 'a' }], meta: {} }] };
  const integ = { totalNodes: 3, pathLength: 3, brokenLinks: [], leafId: 'a', generating: false };
  const ok = evaluateCompleteness(/** @type {any} */ ({ ...base, integrity: { ...integ, crossCheckMissing: [], crossCheckUnknownIds: 87 } }));
  assert.equal(ok.status, 'complete');
  const bad = evaluateCompleteness(/** @type {any} */ ({ ...base, integrity: { ...integ, crossCheckMissing: ['m1'] } }));
  assert.equal(bad.status, 'incomplete');
});
