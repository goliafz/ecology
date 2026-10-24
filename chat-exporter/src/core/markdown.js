// NormalizedChat -> Markdown document.
// Message text is the service's own Markdown, written out unchanged. Elements Markdown cannot
// express (collapsible blocks) use <details>, which GitHub, Obsidian and most viewers render.
import { roleLabel, SERVICE_LABELS } from './model.js';
import { STATUS_LABELS } from './completeness.js';
import { formatBytes, formatDateTime } from './util.js';

/**
 * @typedef {Object} MarkdownOptions
 * @property {'embed' | 'omit'} [images] Embed images as data: URIs (large files) or leave a placeholder.
 */

/**
 * Builds a fenced code block whose fence is longer than any backtick run inside the code.
 * @param {string} code
 * @param {string} [language]
 */
export function fence(code, language = '') {
  const longest = Math.max(2, ...Array.from(String(code).matchAll(/`+/g), (m) => m[0].length));
  const ticks = '`'.repeat(longest + 1);
  const body = String(code).endsWith('\n') ? String(code) : `${code}\n`;
  return `${ticks}${language.replace(/[^\w+#.-]/g, '')}\n${body}${ticks}`;
}

/** @param {string} kind */
export function artifactLabel(kind) {
  return { file: 'Файл', widget: 'Виджет', canvas: 'Холст', 'canvas-document': 'Холст' }[kind] || 'Артефакт';
}

/** @param {string} text */
function quote(text) {
  return String(text).split('\n').map((l) => (l ? `> ${l}` : '>')).join('\n');
}

/** @param {string} text */
function escapeInline(text) {
  return String(text).replace(/([\\`*_[\]<>])/g, '\\$1');
}

/** @param {string} text */
function escapeHtmlText(text) {
  return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * @param {import('./model.js').Block} block
 * @param {MarkdownOptions} options
 * @returns {string}
 */
export function blockToMarkdown(block, options = {}) {
  switch (block.type) {
    case 'markdown':
      return block.text.trim();
    case 'code':
      return [block.title ? `**${escapeInline(block.title)}**` : '', fence(block.code, block.language)].filter(Boolean).join('\n\n');
    case 'image': {
      const alt = escapeInline(block.alt || 'изображение').replace(/\n/g, ' ');
      if (block.embedded && block.src && options.images === 'embed') return `![${alt}](${block.src})`;
      if (block.embedded) return `*[Изображение: ${alt} — встроено в HTML- и PDF-версии]*`;
      return `*[Изображение не сохранено: ${escapeInline(block.note || 'недоступно')}]*`;
    }
    case 'attachment': {
      const meta = [block.mimeType, block.size !== undefined ? formatBytes(block.size) : '', block.pageCount ? `${block.pageCount} стр.` : '']
        .filter(Boolean).join(' · ');
      const lines = [`> **Вложение:** ${escapeInline(block.name)}${meta ? ` · ${meta}` : ''}`, `> *${escapeInline(block.note)}*`];
      let out = lines.join('\n');
      if (block.extractedText) {
        out += `\n\n<details>\n<summary>Текст из файла «${escapeHtmlText(block.name)}»</summary>\n\n${fence(block.extractedText, 'text')}\n\n</details>`;
      }
      return out;
    }
    case 'artifact': {
      // A canvas text document is Markdown itself: written out, not fenced.
      if (block.kind === 'canvas-document') return `**${artifactLabel(block.kind)}: ${escapeInline(block.title)}**\n\n${block.content.trim()}`;
      return `**${artifactLabel(block.kind)}: ${escapeInline(block.title)}**\n\n${fence(block.content, block.language)}`;
    }
    case 'thinking':
      return `<details>\n<summary>Размышления модели</summary>\n\n${block.text.trim()}\n\n</details>`;
    case 'tool': {
      const parts = [`<details>\n<summary>Инструмент: ${escapeHtmlText(block.name)}</summary>\n`];
      if (block.input) parts.push(`Ввод:\n\n${fence(block.input)}`);
      if (block.output) parts.push(`Результат:\n\n${fence(block.output)}`);
      parts.push('</details>');
      return parts.join('\n\n');
    }
    case 'notice':
      return quote(`**${block.level === 'error' ? 'Ошибка' : block.level === 'warning' ? 'Внимание' : 'Примечание'}:** ${block.text}`);
    default:
      return '';
  }
}

/**
 * @param {import('./model.js').NormalizedChat} chat
 * @param {MarkdownOptions} [options]
 * @returns {string}
 */
export function chatToMarkdown(chat, options = {}) {
  const report = chat.completeness;
  const front = {
    title: chat.title,
    service: SERVICE_LABELS[chat.service],
    chat_id: chat.id,
    url: chat.url,
    created: chat.createdAt,
    updated: chat.updatedAt,
    exported: chat.extractedAt,
    model: chat.model,
    messages: chat.messages.length,
    source: chat.source,
    completeness: report?.status || 'unknown',
  };
  const yaml = Object.entries(front)
    .filter(([, v]) => v !== null && v !== undefined)
    .map(([k, v]) => `${k}: ${typeof v === 'number' ? v : JSON.stringify(String(v))}`)
    .join('\n');

  const out = [`---\n${yaml}\n---`, `# ${chat.title.replace(/\n/g, ' ')}`];
  const info = [
    `Сервис: ${SERVICE_LABELS[chat.service]}`,
    `Экспортировано: ${formatDateTime(chat.extractedAt)}`,
    chat.updatedAt ? `Изменён: ${formatDateTime(chat.updatedAt)}` : '',
    `Сообщений: ${chat.messages.length}`,
    report ? `Полнота: ${STATUS_LABELS[report.status]}` : '',
  ].filter(Boolean);
  out.push(quote(`${info.join(' · ')}\n\nИсточник: <${chat.url}>`));
  const warnings = [...(report?.warnings || []), ...chat.notes];
  if (warnings.length) out.push(quote(`**Предупреждения:**\n${warnings.map((w) => `- ${w}`).join('\n')}`));

  chat.messages.forEach((m) => {
    const head = [roleLabel(chat.service, m.role)];
    if (m.createdAt) head.push(formatDateTime(m.createdAt));
    if (m.meta.branchCount) head.push(`версия ${m.meta.branchIndex} из ${m.meta.branchCount}`);
    const body = m.blocks.map((b) => blockToMarkdown(b, options)).filter(Boolean).join('\n\n');
    let section = `## ${head.join(' · ')}\n\n${body}`;
    if (m.meta.interrupted) section += '\n\n*Ответ остановлен пользователем.*';
    out.push(section);
  });
  return `${out.join('\n\n')}\n`;
}
