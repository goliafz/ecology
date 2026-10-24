// Synthetic ChatGPT conversation in the shape of GET /backend-api/conversation/{id}.
// It covers: hidden system node, edited user message (two branches), regenerated answer,
// code + table Markdown, uploaded image, tool call with output image, citations,
// identical messages and Cyrillic text.

const t0 = 1761300000; // seconds, as ChatGPT returns them

function node(id, parent, children, message) {
  return { id, parent, children, message };
}

function msg(id, role, contentType, extra = {}) {
  return {
    id,
    author: { role, name: extra.name, metadata: {} },
    create_time: extra.time ?? null,
    content: { content_type: contentType, ...(extra.content || {}) },
    status: extra.status || 'finished_successfully',
    metadata: extra.metadata || {},
    recipient: extra.recipient || 'all',
  };
}

export function chatGptFixture() {
  return {
    title: 'Тестовый чат: код и таблицы',
    create_time: t0,
    update_time: t0 + 600,
    current_node: 'a3',
    conversation_id: '11111111-2222-3333-4444-555555555555',
    mapping: {
      root: node('root', null, ['sys'], null),
      sys: node('sys', 'root', ['u1'], msg('sys', 'system', 'text', { content: { parts: [''] }, metadata: { is_visually_hidden_from_conversation: true } })),
      u1: node('u1', 'sys', ['a1'], msg('u1', 'user', 'text', { time: t0 + 1, content: { parts: ['Привет! Покажи пример кода на Python.'] } })),
      a1: node('a1', 'u1', ['u2-old', 'u2'], msg('a1', 'assistant', 'text', {
        time: t0 + 2,
        content: { parts: ['Вот пример:\n\n```python\nprint("Привет, мир")\n```\n\n| Колонка | Значение |\n|---|---|\n| a | 1 |\n| b | 2 |'] },
        metadata: { model_slug: 'gpt-test' },
      })),
      'u2-old': node('u2-old', 'a1', ['a2-old'], msg('u2-old', 'user', 'text', { time: t0 + 3, content: { parts: ['Старая версия вопроса'] } })),
      'a2-old': node('a2-old', 'u2-old', [], msg('a2-old', 'assistant', 'text', { time: t0 + 4, content: { parts: ['Старый ответ'] } })),
      u2: node('u2', 'a1', ['a2'], msg('u2', 'user', 'multimodal_text', {
        time: t0 + 5,
        content: { parts: [{ content_type: 'image_asset_pointer', asset_pointer: 'sediment://file_img1', width: 10, height: 10 }, 'Что на картинке? Ок'] },
        metadata: { attachments: [{ id: 'f1', name: 'отчёт.pdf', mime_type: 'application/pdf', size: 2048 }, { id: 'f2', name: 'img.png', mime_type: 'image/png' }] },
      })),
      a2: node('a2', 'u2', ['t2'], msg('a2', 'assistant', 'code', { time: t0 + 6, recipient: 'python', content: { language: 'python', text: 'import matplotlib' } })),
      t2: node('t2', 'a2', ['a2b'], msg('t2', 'tool', 'execution_output', {
        time: t0 + 7,
        name: 'python',
        content: { text: 'OK' },
        metadata: { aggregate_result: { messages: [{ message_type: 'image', image_url: 'file-service://chart1' }] } },
      })),
      a2b: node('a2b', 't2', ['u3'], msg('a2b', 'assistant', 'text', {
        time: t0 + 8,
        content: { parts: ['На картинке график \uE200cite\uE202turn0search0\uE201 и ссылка CITE1.'] },
        metadata: { model_slug: 'gpt-test', content_references: [{ matched_text: 'CITE1', alt: '([Источник](https://example.com))', type: 'grouped_webpages' }] },
      })),
      u3: node('u3', 'a2b', ['a3-old', 'a3'], msg('u3', 'user', 'text', { time: t0 + 9, content: { parts: ['Ок'] } })),
      'a3-old': node('a3-old', 'u3', [], msg('a3-old', 'assistant', 'text', { time: t0 + 10, content: { parts: ['Первый вариант ответа'] } })),
      a3: node('a3', 'u3', [], msg('a3', 'assistant', 'text', { time: t0 + 11, content: { parts: ['Ок'] }, metadata: { model_slug: 'gpt-test', finish_details: { type: 'interrupted' } } })),
    },
  };
}
