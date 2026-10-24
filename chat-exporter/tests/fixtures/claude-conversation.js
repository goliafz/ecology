// Synthetic Claude conversation in the shape of
// GET /api/organizations/{org}/chat_conversations/{id}?tree=True&rendering_mode=messages&render_all_tools=true

const ROOT = '00000000-0000-4000-8000-000000000000';

export function claudeFixture() {
  return {
    uuid: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    name: 'Артефакты и вложения',
    model: 'claude-test',
    created_at: '2026-10-20T10:00:00.000Z',
    updated_at: '2026-10-20T10:10:00.000Z',
    current_leaf_message_uuid: 'm6',
    chat_messages: [
      {
        uuid: 'm1', parent_message_uuid: ROOT, index: 0, sender: 'human', created_at: '2026-10-20T10:00:01.000Z',
        content: [{ type: 'text', text: 'Сделай HTML-страницу' }],
        files: [{ file_kind: 'image', file_name: 'скрин.png', preview_url: '/api/org/files/1/preview' }, { file_kind: 'document', file_name: 'спека.pdf', document_asset: { url: '/x', page_count: 3 } }],
        attachments: [{ file_name: 'notes.md', file_type: 'text/markdown', file_size: 12, extracted_content: '# Заметки\nтекст' }],
      },
      {
        uuid: 'm2', parent_message_uuid: 'm1', index: 1, sender: 'assistant', created_at: '2026-10-20T10:00:02.000Z', stop_reason: 'end_turn',
        content: [
          { type: 'thinking', thinking: 'размышляю' },
          { type: 'text', text: 'Создаю артефакт.' },
          { type: 'tool_use', name: 'artifacts', input: { command: 'create', id: 'page', title: 'Страница', type: 'text/html', content: '<h1>v1 $& </h1>' } },
          { type: 'tool_result', name: 'artifacts', content: [{ type: 'text', text: 'OK' }] },
        ],
      },
      { uuid: 'm3', parent_message_uuid: 'm2', index: 2, sender: 'human', created_at: '2026-10-20T10:00:03.000Z', content: [{ type: 'text', text: 'Исправь заголовок' }] },
      {
        uuid: 'm4-old', parent_message_uuid: 'm3', index: 3, sender: 'assistant', created_at: '2026-10-20T10:00:04.000Z', stop_reason: 'end_turn',
        content: [{ type: 'text', text: 'Старая версия ответа' }],
      },
      {
        uuid: 'm4', parent_message_uuid: 'm3', index: 4, sender: 'assistant', created_at: '2026-10-20T10:00:05.000Z', stop_reason: 'end_turn',
        content: [
          { type: 'tool_use', name: 'artifacts', input: { command: 'update', id: 'page', old_str: 'v1', new_str: 'v2 $1' } },
          { type: 'text', text: 'Готово.' },
          { type: 'tool_use', name: 'web_search', input: { query: 'тест' } },
        ],
      },
      { uuid: 'm5', parent_message_uuid: 'm4', index: 5, sender: 'human', created_at: '2026-10-20T10:00:06.000Z', content: [{ type: 'text', text: 'Спасибо' }] },
      {
        uuid: 'm6', parent_message_uuid: 'm5', index: 6, sender: 'assistant', created_at: '2026-10-20T10:00:07.000Z', stop_reason: 'user_canceled',
        content: [{ type: 'text', text: 'Пожалуйста' }],
      },
    ],
  };
}
