import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeChatGpt } from '../src/adapters/chatgpt/normalize.js';
import { normalizeClaude } from '../src/adapters/claude/normalize.js';
import { evaluateCompleteness } from '../src/core/completeness.js';
import { chatToMarkdown, fence } from '../src/core/markdown.js';
import { chatToHtml, EXPORT_CSP } from '../src/core/html.js';
import { renderMarkdown, safeHref } from '../src/core/md-render.js';
import { chatGptFixture } from './fixtures/chatgpt-conversation.js';
import { claudeFixture } from './fixtures/claude-conversation.js';

const PNG = 'data:image/png;base64,iVBORw0KGgo=';

function sampleChat() {
  const chat = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG, 'file-service://chart1': PNG }, nowIso: '2026-10-24T20:00:00.000Z' });
  chat.completeness = evaluateCompleteness(chat);
  return chat;
}

test('Markdown: front matter, roles, code, table, attachments', () => {
  const md = chatToMarkdown(sampleChat());
  assert.match(md, /^---\ntitle: "Тестовый чат: код и таблицы"\nservice: "ChatGPT"/);
  assert.match(md, /completeness: "complete_with_warnings"|completeness: "complete"/);
  assert.match(md, /## Пользователь · /);
  assert.match(md, /## ChatGPT · /);
  assert.match(md, /```python\nprint\("Привет, мир"\)\n```/);
  assert.match(md, /\| Колонка \| Значение \|/);
  assert.match(md, /\*\*Вложение:\*\* отчёт\.pdf · application\/pdf · 2\.0 КБ/);
  assert.match(md, /Изображение: изображение — встроено в HTML- и PDF-версии/);
  assert.match(md, /версия 2 из 2/);
  assert.match(md, /\*Ответ остановлен пользователем\.\*/);
  assert.ok(!md.includes('data:image'), 'images are not embedded by default');
  assert.match(chatToMarkdown(sampleChat(), { images: 'embed' }), /!\[изображение\]\(data:image\/png/);
});

test('Markdown: fence grows past backticks inside code', () => {
  assert.equal(fence('a ``` b', 'js'), '````js\na ``` b\n````');
  assert.equal(fence('x', 'bad lang!'), '```badlang\nx\n```');
});

test('HTML: self-contained, CSP, no scripts, escaped raw HTML', () => {
  const chat = sampleChat();
  chat.messages[0].blocks.push({ type: 'markdown', text: '<script>alert(1)</script>\n\nText <img src="https://evil.example/x.png"> [js](javascript:alert(1)) ![remote](https://example.com/a.png)' });
  const html = chatToHtml(chat);
  assert.ok(html.startsWith('<!DOCTYPE html>'));
  assert.ok(html.includes(`content="${EXPORT_CSP}"`));
  assert.ok(!/<script/i.test(html), 'no script tags');
  assert.ok(!html.includes('src="https://'), 'no remote resource loads');
  assert.ok(html.includes('&lt;script&gt;'));
  assert.ok(!html.includes('href="javascript:'));
  assert.match(html, /Изображение: remote \(внешнее, не загружено\)/);
  assert.match(html, /<img src="data:image\/png;base64,iVBORw0KGgo="/);
  assert.match(html, /<table>/);
  assert.match(html, /class="language-python"/);
  assert.match(html, /<html lang="ru">/);
});

test('HTML: print variant expands collapsible blocks', () => {
  const chat = normalizeClaude(claudeFixture(), { includeThinking: true, includeTools: true });
  chat.completeness = evaluateCompleteness(chat);
  assert.ok(!chatToHtml(chat).includes('<details open>'));
  assert.ok(chatToHtml(chat, { forPrint: true }).includes('<details open>'));
});

test('HTML: incomplete export is clearly marked', () => {
  const chat = sampleChat();
  chat.integrity.brokenLinks.push('x');
  chat.completeness = evaluateCompleteness(chat);
  const html = chatToHtml(chat);
  assert.match(html, /Экспорт неполный/);
  assert.match(html, /НЕПОЛНЫЙ/);
});

test('md-render: safe links only', () => {
  assert.equal(safeHref('javascript:alert(1)'), null);
  assert.equal(safeHref('https://a.b/c'), 'https://a.b/c');
  assert.equal(safeHref('#m-1'), '#m-1');
  assert.equal(safeHref('/relative'), null);
  assert.match(renderMarkdown('[x](https://a.b)'), /<a href="https:\/\/a.b\/" rel="noopener noreferrer">x<\/a>/);
  assert.equal(renderMarkdown('[x](javascript:alert(1))').trim(), '<p>x</p>');
});

test('Completeness: statuses', () => {
  const chat = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG, 'file-service://chart1': PNG } });
  assert.equal(evaluateCompleteness(chat).status, 'complete');

  const noImages = normalizeChatGpt(chatGptFixture(), {});
  assert.equal(evaluateCompleteness(noImages).status, 'complete_with_warnings');

  const generating = normalizeChatGpt(chatGptFixture(), {});
  generating.integrity.generating = true;
  assert.equal(evaluateCompleteness(generating).status, 'incomplete');

  const dup = normalizeChatGpt(chatGptFixture(), {});
  dup.messages.push(dup.messages[0]);
  assert.equal(evaluateCompleteness(dup).status, 'incomplete');

  const cross = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG, 'file-service://chart1': PNG } });
  cross.integrity.crossCheckMissing = ['zzz'];
  assert.equal(evaluateCompleteness(cross).status, 'incomplete');

  const stale = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG, 'file-service://chart1': PNG } });
  const r = evaluateCompleteness(stale, { expectedUpdatedAt: '2030-01-01T00:00:00Z' });
  assert.equal(r.status, 'complete_with_warnings');
  assert.ok(r.checks.some((c) => c.id === 'freshness'));
});

test('Completeness: DOM fallback rules', () => {
  const chat = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG, 'file-service://chart1': PNG } });
  chat.source = 'dom';
  chat.integrity.reachedTop = true;
  chat.integrity.reachedBottom = true;
  chat.integrity.snapshotGaps = 0;
  assert.equal(evaluateCompleteness(chat).status, 'complete_with_warnings');
  chat.integrity.snapshotGaps = 1;
  assert.equal(evaluateCompleteness(chat).status, 'incomplete');
  chat.integrity.snapshotGaps = 0;
  chat.integrity.reachedTop = false;
  assert.equal(evaluateCompleteness(chat).status, 'incomplete');
});
