import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeChatGpt, cleanCitations, collectChatGptImagePointers, chatGptIdFromUrl } from '../src/adapters/chatgpt/normalize.js';
import { normalizeClaude, claudeIdFromUrl, collectClaudeImageUrls } from '../src/adapters/claude/normalize.js';
import { chatGptFixture } from './fixtures/chatgpt-conversation.js';
import { claudeFixture } from './fixtures/claude-conversation.js';

const PNG = 'data:image/png;base64,iVBORw0KGgo=';

test('ChatGPT: current branch only, hidden nodes skipped, order kept', () => {
  const chat = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG } });
  const roles = chat.messages.map((m) => m.role);
  assert.deepEqual(roles, ['user', 'assistant', 'user', 'assistant', 'user', 'assistant']);
  const texts = chat.messages.map((m) => m.blocks.filter((b) => b.type === 'markdown').map((b) => b.text).join('|'));
  assert.ok(!texts.some((t) => t.includes('Старая версия')), 'old branch must not be exported');
  assert.ok(!texts.some((t) => t.includes('Первый вариант')), 'regenerated answer must not be exported');
  assert.equal(chat.integrity.brokenLinks.length, 0);
  assert.equal(chat.integrity.totalNodes, 13);
  assert.equal(chat.title, 'Тестовый чат: код и таблицы');
  assert.equal(chat.url, 'https://chatgpt.com/c/11111111-2222-3333-4444-555555555555');
  assert.match(chat.notes[0], /альтернативные версии/);
});

test('ChatGPT: tool call, output and answer merge into one assistant turn', () => {
  const chat = normalizeChatGpt(chatGptFixture(), { includeTools: true, images: {} });
  const turn = chat.messages[3];
  assert.equal(turn.role, 'assistant');
  assert.deepEqual(turn.blocks.map((b) => b.type), ['tool', 'image', 'tool', 'markdown']);
  assert.equal(turn.id, 'a2b', 'id of the message holding the text (matches DOM data-message-id)');
  assert.deepEqual(/** @type {any} */ (turn.meta).sourceIds, ['a2', 't2', 'a2b']);
});

test('ChatGPT: tools hidden by default, images reported when not embedded', () => {
  const chat = normalizeChatGpt(chatGptFixture(), {});
  const turn = chat.messages[3];
  assert.deepEqual(turn.blocks.map((b) => b.type), ['image', 'markdown']);
  assert.equal(chat.integrity.imagesFailed, 2);
});

test('ChatGPT: attachments, uploaded image, branches and interrupted flag', () => {
  const chat = normalizeChatGpt(chatGptFixture(), { images: { 'sediment://file_img1': PNG } });
  const u2 = chat.messages[2];
  assert.equal(u2.blocks[0].type, 'attachment');
  assert.equal(/** @type {any} */ (u2.blocks[0]).name, 'отчёт.pdf');
  assert.equal(u2.blocks.filter((b) => b.type === 'attachment').length, 1, 'image attachments are shown as images');
  const img = /** @type {any} */ (u2.blocks.find((b) => b.type === 'image'));
  assert.equal(img.embedded, true);
  assert.equal(img.origin, 'user-upload');
  assert.equal(u2.meta.branchIndex, 2);
  assert.equal(u2.meta.branchCount, 2);
  assert.equal(chat.messages[5].meta.interrupted, true);
});

test('ChatGPT: citations replaced or removed', () => {
  assert.equal(cleanCitations('a \uE200cite\uE202x\uE201 b', {}), 'a b');
  assert.equal(cleanCitations('see C1', { content_references: [{ matched_text: 'C1', alt: '[s](https://e.com)' }] }), 'see [s](https://e.com)');
  const chat = normalizeChatGpt(chatGptFixture(), {});
  const text = /** @type {any} */ (chat.messages[3].blocks.find((b) => b.type === 'markdown')).text;
  assert.ok(!/[\uE200-\uE2FF]/.test(text));
  assert.match(text, /\[Источник\]\(https:\/\/example.com\)/);
});

test('ChatGPT: missing current_node and broken parent are detected', () => {
  const raw = chatGptFixture();
  delete raw.mapping.u2; // breaks the chain a2 -> u2 -> a1
  const chat = normalizeChatGpt(raw, {});
  assert.ok(chat.integrity.brokenLinks.includes('u2'));
});

test('ChatGPT: generating status and invalid input', () => {
  const raw = chatGptFixture();
  raw.mapping.a3.message.status = 'in_progress';
  assert.equal(normalizeChatGpt(raw).integrity.generating, true);
  assert.throws(() => normalizeChatGpt({ title: 'x' }), /mapping/);
});

test('ChatGPT: helpers', () => {
  assert.deepEqual(collectChatGptImagePointers(chatGptFixture()).sort(), ['file-service://chart1', 'sediment://file_img1']);
  assert.equal(chatGptIdFromUrl('https://chatgpt.com/c/11111111-2222-3333-4444-555555555555'), '11111111-2222-3333-4444-555555555555');
  assert.equal(chatGptIdFromUrl('https://chatgpt.com/g/g-abc/c/11111111-2222-3333-4444-555555555555?x=1'), '11111111-2222-3333-4444-555555555555');
  assert.equal(chatGptIdFromUrl('https://chatgpt.com/'), null);
});

test('Claude: current branch via leaf, artifacts folded to final version once', () => {
  const chat = normalizeClaude(claudeFixture(), { images: { '/api/org/files/1/preview': PNG } });
  assert.deepEqual(chat.messages.map((m) => m.id), ['m1', 'm2', 'm3', 'm4', 'm5', 'm6']);
  const artifacts = chat.messages.flatMap((m) => m.blocks).filter((b) => b.type === 'artifact');
  assert.equal(artifacts.length, 1, 'artifact rendered once, at its last edit');
  assert.equal(/** @type {any} */ (artifacts[0]).content, '<h1>v2 $1 $& </h1>', '$-patterns stay literal');
  assert.equal(chat.messages[3].blocks[0].type, 'artifact');
  assert.equal(/** @type {any} */ (artifacts[0]).language, 'html');
  assert.equal(chat.integrity.brokenLinks.length, 0);
  assert.equal(chat.messages[5].meta.interrupted, true);
  assert.equal(chat.messages[3].meta.branchIndex, 2);
  assert.equal(chat.messages[3].meta.branchCount, 2);
});

test('Claude: files, attachments, thinking and tools by option', () => {
  const plain = normalizeClaude(claudeFixture(), {});
  const first = plain.messages[0];
  assert.deepEqual(first.blocks.map((b) => b.type), ['image', 'attachment', 'attachment', 'markdown']);
  assert.equal(/** @type {any} */ (first.blocks[1]).pageCount, 3);
  assert.equal(/** @type {any} */ (first.blocks[2]).extractedText, '# Заметки\nтекст');
  assert.equal(plain.integrity.imagesFailed, 1);
  assert.ok(!plain.messages[1].blocks.some((b) => b.type === 'thinking' || b.type === 'tool'));

  const full = normalizeClaude(claudeFixture(), { includeThinking: true, includeTools: true });
  assert.deepEqual(full.messages[1].blocks.map((b) => b.type), ['thinking', 'markdown', 'tool'], 'artifact is shown at its last edit (m4), tool_result as tool');
  assert.ok(full.messages[3].blocks.some((b) => b.type === 'tool' && b.name === 'web_search'));
});

test('Claude: missing leaf falls back to index order and reports gaps', () => {
  const raw = claudeFixture();
  raw.current_leaf_message_uuid = 'nope';
  raw.chat_messages = raw.chat_messages.filter((m) => m.uuid !== 'm4-old' && m.uuid !== 'm3');
  const chat = normalizeClaude(raw, {});
  assert.ok(chat.integrity.brokenLinks.includes('nope'));
  assert.ok(chat.integrity.brokenLinks.some((l) => l.startsWith('index-gap')));
});

test('Claude: broken parent link detected', () => {
  const raw = claudeFixture();
  raw.chat_messages = raw.chat_messages.filter((m) => m.uuid !== 'm3');
  const chat = normalizeClaude(raw, {});
  assert.ok(chat.integrity.brokenLinks.includes('m3'));
});

test('Claude: helpers', () => {
  assert.deepEqual(collectClaudeImageUrls(claudeFixture()), ['/api/org/files/1/preview']);
  assert.equal(claudeIdFromUrl('https://claude.ai/chat/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'), 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee');
  assert.equal(claudeIdFromUrl('https://claude.ai/new'), null);
  assert.throws(() => normalizeClaude({}), /chat_messages/);
});

/** Chat with a root, a question and one answer message (like the reported chat). */
function threeNodeChat(answer) {
  return {
    title: 'Показ промта улучшателя',
    current_node: 'a',
    mapping: {
      root: { id: 'root', parent: null, children: ['q'], message: null },
      q: { id: 'q', parent: 'root', children: ['a'], message: { id: 'q', author: { role: 'user' }, content: { content_type: 'text', parts: ['Покажи как составлен промт'] }, create_time: 1, status: 'finished_successfully' } },
      a: { id: 'a', parent: 'q', children: [], message: { id: 'a', author: { role: 'assistant' }, create_time: 2, status: 'finished_successfully', recipient: 'all', ...answer } },
    },
  };
}

test('ChatGPT canvas answer (canmore.create_textdoc) is exported, not dropped', async () => {
  const { evaluateCompleteness } = await import('../src/core/completeness.js');
  const doc = '# Промт\n\n### Шаг 4. Сделай промпт проверяемым\n\nЗаменяй абстрактные требования конкретными.';
  const chat = normalizeChatGpt(threeNodeChat({
    recipient: 'canmore.create_textdoc',
    content: { content_type: 'code', language: 'json', text: JSON.stringify({ name: 'Промт-улучшатель', type: 'code/markdown', content: doc }) },
  }));
  assert.equal(chat.messages.length, 2);
  const art = /** @type {any} */ (chat.messages[1].blocks[0]);
  assert.equal(art.type, 'artifact');
  assert.equal(art.kind, 'canvas');
  assert.equal(art.language, 'markdown');
  assert.equal(art.content, doc);
  assert.equal(evaluateCompleteness(chat).status, 'complete');
});

test('ChatGPT canvas: document type and updates', () => {
  const raw = threeNodeChat({ recipient: 'canmore.create_textdoc', content: { content_type: 'code', text: JSON.stringify({ name: 'Док', type: 'document', content: 'Версия 1 $&' }) } });
  raw.mapping.a.children = ['u'];
  raw.mapping.u = { id: 'u', parent: 'a', children: [], message: { id: 'u', author: { role: 'assistant' }, recipient: 'canmore.update_textdoc', content: { content_type: 'code', text: JSON.stringify({ updates: [{ pattern: 'Версия 1', multiple: false, replacement: 'Версия 2 $1' }] }) } } };
  raw.current_node = 'u';
  const chat = normalizeChatGpt(raw);
  const arts = chat.messages[1].blocks.filter((b) => b.type === 'artifact');
  assert.equal(arts.length, 2);
  assert.equal(/** @type {any} */ (arts[0]).kind, 'canvas-document');
  assert.equal(/** @type {any} */ (arts[1]).content, 'Версия 2 $1 $&');
  assert.match(/** @type {any} */ (arts[1]).title, /обновлено/);
});

test('ChatGPT: unknown answer format is saved as raw text with a warning', async () => {
  const { evaluateCompleteness } = await import('../src/core/completeness.js');
  const chat = normalizeChatGpt(threeNodeChat({
    recipient: 'some.new_tool',
    content: { content_type: 'code', text: 'Текст ответа в новом формате' },
  }));
  assert.equal(chat.messages.length, 2);
  assert.equal(chat.messages[1].blocks[0].type, 'notice');
  assert.equal(/** @type {any} */ (chat.messages[1].blocks[1]).code, 'Текст ответа в новом формате');
  assert.equal(chat.integrity.rescuedTurns, 1);
  assert.equal(evaluateCompleteness(chat).status, 'complete_with_warnings');

  const nested = normalizeChatGpt(threeNodeChat({ content: { content_type: 'brand_new', parts: [{ kind: 'block', markdown: 'Вложенный **текст**' }] } }));
  assert.ok(nested.messages[1].blocks.some((b) => b.type === 'markdown' && b.text.includes('Вложенный')));
});

test('ChatGPT: an answer without any extractable text makes the export INCOMPLETE', async () => {
  const { evaluateCompleteness } = await import('../src/core/completeness.js');
  const chat = normalizeChatGpt(threeNodeChat({ content: { content_type: 'mystery', payload: { id: 42 } } }));
  assert.equal(chat.messages.length, 1);
  const report = evaluateCompleteness(chat);
  assert.equal(report.status, 'incomplete');
  assert.ok(report.checks.some((c) => c.id === 'answer' && c.result === 'fail'));
  assert.ok(!chat.integrity.pathIds?.includes('a'), 'DOM cross-check will also flag it');
});

test('ChatGPT: tool calls skipped by settings do not trigger rescue when the turn has an answer', () => {
  const chat = normalizeChatGpt(chatGptFixture(), {});
  assert.equal(chat.integrity.rescuedTurns, 0);
  assert.ok(chat.integrity.pathIds?.includes('a2'), 'skipped tool call is accounted for');
});

// Structure of a real ChatGPT answer (diagnostics file of the reported chat, text replaced):
// the whole answer is a ```markdown code block, and metadata.content_references marks that same
// block as a client_defined_widget (alt: null). Version 1.0.0 treated it as a citation and cut
// the answer out; 1.1.0 rescued it from fallbackMarkdown with false warnings.
test('ChatGPT: code block widget in content_references keeps the answer (regression)', async () => {
  const { evaluateCompleteness } = await import('../src/core/completeness.js');
  const answer = '```markdown\n# Системный промпт\n\n## 1. Роль\n\nТекст «в кавычках» и `код`.\n```\n\n';
  const block = answer.trimEnd();
  const chat = normalizeChatGpt(threeNodeChat({
    channel: 'final',
    content: { content_type: 'text', parts: [answer] },
    metadata: {
      model_slug: 'gpt-6',
      content_references: [{ type: 'client_defined_widget', category: 'code_block', data: { content: '# Системный промпт', language: 'markdown', widget_type: 'code_block' }, matched_text: block, start_idx: 0, end_idx: block.length, alt: null }],
      model_dil_v2: { code: 'DIL.render(...)', fallbackMarkdown: block, clientDefinedWidgets: {} },
    },
  }));
  assert.equal(chat.messages.length, 2);
  assert.equal(chat.messages[1].blocks.length, 1);
  assert.equal(/** @type {any} */ (chat.messages[1].blocks[0]).text.trim(), block);
  assert.equal(chat.integrity.rescuedTurns, 0, 'no raw-format rescue needed');
  assert.equal(evaluateCompleteness(chat).status, 'complete');
});

test('ChatGPT: citation spans are still replaced, plain spans without alt are kept', () => {
  assert.equal(cleanCitations('A citet0 B', { content_references: [{ matched_text: 'citet0', alt: '[s](https://x.y)' }] }), 'A [s](https://x.y) B');
  assert.equal(cleanCitations('keep this text', { content_references: [{ matched_text: 'this text', alt: null, type: 'client_defined_widget' }] }), 'keep this text');
  assert.equal(cleanCitations('see x', { content_references: [{ matched_text: 'x', type: 'webpage', url: 'https://e.com', title: 'E' }] }), 'see  ([E](https://e.com))');
});

test('ChatGPT: "analysis" channel is hidden reasoning, not the answer', () => {
  const raw = threeNodeChat({ channel: 'final', content: { content_type: 'text', parts: ['Ответ'] } });
  raw.mapping.q.children = ['r'];
  raw.mapping.r = { id: 'r', parent: 'q', children: ['a'], message: { id: 'r', author: { role: 'assistant' }, channel: 'analysis', recipient: 'all', content: { content_type: 'text', parts: ['Внутренние рассуждения'] } } };
  raw.mapping.a.parent = 'r';
  const plain = normalizeChatGpt(raw);
  assert.deepEqual(plain.messages[1].blocks.map((b) => b.type), ['markdown']);
  assert.ok(!JSON.stringify(plain.messages).includes('Внутренние рассуждения'));
  assert.equal(plain.integrity.rescuedTurns, 0);
  const withThinking = normalizeChatGpt(raw, { includeThinking: true });
  assert.deepEqual(withThinking.messages[1].blocks.map((b) => b.type), ['thinking', 'markdown']);
});
