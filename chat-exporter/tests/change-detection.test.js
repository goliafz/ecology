import { test } from 'node:test';
import assert from 'node:assert/strict';
import { detectChanges, classify } from '../src/core/change-detection.js';
import { normalizeSettings, DEFAULT_SETTINGS } from '../src/background/settings.js';

const NOW = Date.parse('2026-10-24T23:00:00Z');
const base = {
  now: NOW,
  service: /** @type {const} */ ('chatgpt'),
  baselineAt: Date.parse('2026-10-20T00:00:00Z'),
  backfill: false,
  excluded: {},
  maxAttempts: 3,
  maxChats: 100,
  listComplete: true,
};
const item = (id, updatedAt, title = id) => ({ service: /** @type {const} */ ('chatgpt'), id, title, updatedAt, createdAt: null });
const exported = (updatedAt, status = 'complete') => ({ at: NOW - 1000, updatedAt, contentHash: 'h', status, formats: ['md'], files: {}, messages: 2, source: 'api' });

test('new chats after baseline are selected, older ones only with backfill', () => {
  const items = [item('new', '2026-10-24T10:00:00Z'), item('old', '2026-01-01T00:00:00Z')];
  let r = detectChanges({}, items, base);
  assert.deepEqual(r.candidates.map((c) => [c.id, c.reason]), [['new', 'new']]);
  r = detectChanges({}, items, { ...base, backfill: true });
  assert.deepEqual(r.candidates.map((c) => [c.id, c.reason]), [['new', 'new'], ['old', 'backfill']], 'backfill goes last');
});

test('changed vs unchanged exported chats', () => {
  const index = {
    'chatgpt:a': { service: 'chatgpt', id: 'a', title: 'a', updatedAt: null, lastExport: exported('2026-10-23T10:00:00Z') },
    'chatgpt:b': { service: 'chatgpt', id: 'b', title: 'b', updatedAt: null, lastExport: exported('2026-10-24T10:00:00Z') },
  };
  const r = detectChanges(/** @type {any} */ (index), [item('a', '2026-10-24T12:00:00Z'), item('b', '2026-10-24T10:00:00Z')], base);
  assert.deepEqual(r.candidates.map((c) => [c.id, c.reason]), [['a', 'changed']]);
  assert.equal(r.index['chatgpt:a'].lastExport?.updatedAt, '2026-10-23T10:00:00Z', 'lastExport is not touched by listing');
});

test('excluded chats, renamed titles, deleted chats', () => {
  const index = {
    'chatgpt:gone': { service: 'chatgpt', id: 'gone', title: 'Удалённый', updatedAt: '2026-10-01T00:00:00Z', lastExport: exported('2026-10-01T00:00:00Z') },
    'chatgpt:r': { service: 'chatgpt', id: 'r', title: 'Старое имя', updatedAt: '2026-10-21T00:00:00Z', lastExport: exported('2026-10-21T00:00:00Z') },
    'claude:x': { service: 'claude', id: 'x', title: 'Claude', updatedAt: null },
  };
  const r = detectChanges(/** @type {any} */ (index), [item('r', '2026-10-21T00:00:00Z', 'Новое имя'), item('ex', '2026-10-24T00:00:00Z')], { ...base, excluded: { 'chatgpt:ex': true } });
  assert.equal(r.candidates.length, 0);
  assert.equal(r.stats.excluded, 1);
  assert.equal(r.index['chatgpt:r'].title, 'Новое имя');
  assert.equal(r.index['chatgpt:gone'].missingSince, NOW, 'disappeared chat is marked, not deleted');
  assert.equal(r.index['claude:x'].missingSince, undefined, 'other services untouched');
  const partial = detectChanges(/** @type {any} */ (index), [], { ...base, listComplete: false });
  assert.equal(partial.index['chatgpt:gone'].missingSince, undefined, 'partial list never marks deletions');
});

test('activity-marked chats are included even when absent from a partial list', () => {
  const index = { 'chatgpt:d': { service: 'chatgpt', id: 'd', title: 'd', updatedAt: null, dirty: true } };
  const r = detectChanges(/** @type {any} */ (index), [], { ...base, listComplete: false });
  assert.deepEqual(r.candidates.map((c) => [c.id, c.reason]), [['d', 'activity']]);
});

test('retry policy: backoff, give-up, resume after a new change', () => {
  const e = (extra) => /** @type {any} */ ({ service: 'chatgpt', id: 'f', title: 'f', updatedAt: '2026-10-24T10:00:00Z', ...extra });
  assert.equal(classify(e({ failures: 1, nextRetryAt: NOW + 1000 }), base), null, 'waits for backoff');
  assert.equal(classify(e({ failures: 1, nextRetryAt: NOW - 1000 }), base), 'retry');
  assert.equal(classify(e({ failures: 3, lastError: { at: Date.parse('2026-10-24T11:00:00Z') } }), base), null, 'gave up');
  assert.equal(classify(e({ failures: 3, lastError: { at: Date.parse('2026-10-24T09:00:00Z') } }), base), 'new', 'changed after the failure');
  assert.equal(classify(e({ lastExport: exported('2026-10-24T10:00:00Z', 'incomplete') }), base), 'incomplete', 'incomplete exports are redone');
});

test('candidate cap and ordering by update time', () => {
  const items = Array.from({ length: 10 }, (_, i) => item(`c${i}`, new Date(Date.parse('2026-10-21T00:00:00Z') + i * 3600_000).toISOString()));
  const r = detectChanges({}, items, { ...base, maxChats: 3 });
  assert.deepEqual(r.candidates.map((c) => c.id), ['c9', 'c8', 'c7']);
});

test('settings normalization: defaults, clamping, invalid values', () => {
  const s = normalizeSettings({ formats: ['pdf', 'bogus', 'pdf'], versioning: 'x', auto: { time: '7:30', days: [1, 9, 1], maxChatsPerRun: 99999, delayMs: 1 } });
  assert.deepEqual(s.formats, ['pdf']);
  assert.equal(s.versioning, 'overwrite');
  assert.equal(s.auto.time, '7:30');
  assert.deepEqual(s.auto.days, [1]);
  assert.equal(s.auto.maxChatsPerRun, 1000);
  assert.equal(s.auto.delayMs, 500);
  assert.deepEqual(normalizeSettings(null).formats, DEFAULT_SETTINGS.formats);
  assert.deepEqual(normalizeSettings({ formats: [] }).formats, ['html', 'md']);
});
