// NormalizedChat -> self-contained HTML document (also the source for PDF printing).
// The document carries a Content-Security-Policy that forbids scripts and every network
// request: it can only show inline styles and embedded data: images.
import { roleLabel, SERVICE_LABELS } from './model.js';
import { STATUS_LABELS } from './completeness.js';
import { escapeHtml, isDataImage, renderMarkdown, safeHref } from './md-render.js';
import { formatBytes, formatDateTime } from './util.js';
import { fence } from './markdown.js';

export const EXPORT_CSP = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";

const CSS = `
:root{--bg:#fff;--fg:#1d1d1f;--muted:#6b6b70;--border:#e3e3e8;--user-bg:#f3f6fb;--user-border:#c9d6ee;--ai-bg:#fff;--code-bg:#f6f7f9;--accent:#2457c5;--warn-bg:#fff7e0;--warn-border:#e8c35a;--fail-bg:#fdecec;--fail-border:#e08a8a;--ok-bg:#eaf7ee;--ok-border:#8cc79c}
@media screen and (prefers-color-scheme:dark){:root{--bg:#17181a;--fg:#e8e8ea;--muted:#a0a0a8;--border:#33353a;--user-bg:#1f2633;--user-border:#34476b;--ai-bg:#17181a;--code-bg:#22252a;--accent:#7aa2ff;--warn-bg:#3a3220;--warn-border:#8a7130;--fail-bg:#3a2222;--fail-border:#8a4040;--ok-bg:#1f3326;--ok-border:#3f7a4f}}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.6 system-ui,-apple-system,"Segoe UI",Roboto,"Noto Sans","Helvetica Neue",Arial,"Liberation Sans",sans-serif}
.page{max-width:900px;margin:0 auto;padding:24px 16px 48px}
.doc-header h1{font-size:1.6em;line-height:1.25;margin:0 0 8px;overflow-wrap:anywhere}
.doc-meta{color:var(--muted);font-size:.9em;margin:0 0 12px;display:flex;flex-wrap:wrap;gap:4px 16px}
.doc-meta a{color:inherit}
.badge{display:inline-block;padding:2px 8px;border-radius:999px;border:1px solid var(--border);font-size:.85em}
.badge.complete{background:var(--ok-bg);border-color:var(--ok-border)}
.badge.complete_with_warnings{background:var(--warn-bg);border-color:var(--warn-border)}
.badge.incomplete{background:var(--fail-bg);border-color:var(--fail-border);font-weight:600}
.warnings{background:var(--warn-bg);border:1px solid var(--warn-border);border-radius:8px;padding:8px 12px;margin:12px 0;font-size:.9em}
.warnings.incomplete{background:var(--fail-bg);border-color:var(--fail-border)}
.warnings ul{margin:4px 0 0;padding-left:20px}
.toc{border:1px solid var(--border);border-radius:8px;padding:8px 12px;margin:16px 0;font-size:.9em}
.toc summary{cursor:pointer;color:var(--muted)}
.toc ol{margin:8px 0 0;padding-left:24px}
.toc a{color:var(--accent);text-decoration:none}
.msg{border:1px solid var(--border);border-radius:10px;padding:10px 16px 6px;margin:14px 0;background:var(--ai-bg)}
.msg-user{background:var(--user-bg);border-color:var(--user-border)}
.msg-head{display:flex;flex-wrap:wrap;align-items:baseline;gap:4px 12px;font-size:.85em;color:var(--muted);border-bottom:1px solid var(--border);padding-bottom:4px;margin-bottom:6px}
.msg-head .role{font-weight:700;color:var(--fg);font-size:1.05em}
.msg-head .num{margin-left:auto;color:var(--muted);text-decoration:none}
.msg-body{overflow-wrap:break-word}
.msg-body>:first-child{margin-top:4px}
.msg-body h1,.msg-body h2,.msg-body h3,.msg-body h4{line-height:1.3;margin:1em 0 .4em}
.msg-body h1{font-size:1.35em}.msg-body h2{font-size:1.2em}.msg-body h3{font-size:1.08em}
.msg-body a{color:var(--accent)}
.msg-body blockquote{margin:.6em 0;padding:.1em 1em;border-left:3px solid var(--border);color:var(--muted)}
.msg-body img{max-width:100%;height:auto;border-radius:6px}
.msg-body hr{border:0;border-top:1px solid var(--border)}
code{font-family:"Cascadia Mono",Consolas,"SF Mono",Menlo,"DejaVu Sans Mono","Liberation Mono","Courier New",monospace;font-size:.88em}
:not(pre)>code{background:var(--code-bg);padding:.1em .35em;border-radius:4px;overflow-wrap:anywhere}
.code-block{margin:.7em 0;border:1px solid var(--border);border-radius:8px;background:var(--code-bg);overflow:hidden}
.code-lang{font:12px/1.4 system-ui,sans-serif;color:var(--muted);padding:4px 12px;border-bottom:1px solid var(--border)}
.code-block pre{margin:0;padding:10px 12px;overflow-x:auto;white-space:pre;tab-size:4}
.table-wrap{overflow-x:auto;margin:.7em 0}
table{border-collapse:collapse;min-width:50%}
th,td{border:1px solid var(--border);padding:5px 9px;text-align:left;vertical-align:top}
th{background:var(--code-bg)}
.figure{margin:.7em 0}
.figure figcaption{font-size:.82em;color:var(--muted)}
.placeholder{border:1px dashed var(--border);border-radius:8px;padding:8px 12px;color:var(--muted);font-size:.9em;margin:.6em 0}
.attachment{border:1px solid var(--border);border-radius:8px;padding:8px 12px;margin:.6em 0;font-size:.92em}
.attachment .note{color:var(--muted);font-size:.9em}
.artifact-title{font-weight:600;margin:.8em 0 .2em}
details{border:1px solid var(--border);border-radius:8px;padding:6px 12px;margin:.6em 0}
details>summary{cursor:pointer;color:var(--muted);font-size:.92em}
.notice{border-left:3px solid var(--warn-border);background:var(--warn-bg);padding:6px 12px;margin:.6em 0;font-size:.92em}
.notice.error{border-color:var(--fail-border);background:var(--fail-bg)}
.meta-line{color:var(--muted);font-size:.85em;font-style:italic}
.doc-footer{margin-top:32px;color:var(--muted);font-size:.8em;border-top:1px solid var(--border);padding-top:8px}
@page{size:A4;margin:16mm 14mm 18mm}
@media print{
 body{font-size:10.5pt;background:#fff;color:#000}
 .page{max-width:none;padding:0}
 .toc{display:none}
 .msg{break-inside:auto;border-color:#ccc;page-break-inside:auto}
 .msg-head{break-after:avoid;page-break-after:avoid}
 .code-block pre{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;overflow:visible}
 .table-wrap{overflow:visible}
 table{font-size:9pt;width:100%;table-layout:auto}
 th,td{overflow-wrap:anywhere;word-break:break-word}
 thead{display:table-header-group}
 tr,img,.figure,.attachment{break-inside:avoid;page-break-inside:avoid}
 img{max-height:240mm}
 h1,h2,h3,h4{break-after:avoid;page-break-after:avoid}
 a{color:#1a4fb5;text-decoration:none}
 details>summary{list-style:none}
}
`;

/**
 * @typedef {Object} HtmlOptions
 * @property {boolean} [forPrint] Expand collapsible blocks (they would be hidden in a PDF).
 * @property {string} [generator]
 */

/**
 * @param {import('./model.js').Block} block
 * @param {HtmlOptions} options
 * @returns {string}
 */
export function blockToHtml(block, options) {
  const open = options.forPrint ? ' open' : '';
  switch (block.type) {
    case 'markdown':
      return renderMarkdown(block.text);
    case 'code':
      return `${block.title ? `<div class="artifact-title">${escapeHtml(block.title)}</div>` : ''}${renderMarkdown(fence(block.code, block.language))}`;
    case 'image': {
      if (block.embedded && block.src && isDataImage(block.src)) {
        const caption = imageCaption(block);
        return `<figure class="figure"><img src="${escapeHtml(block.src)}" alt="${escapeHtml(block.alt || '')}">${caption ? `<figcaption>${escapeHtml(caption)}</figcaption>` : ''}</figure>`;
      }
      return `<div class="placeholder">Изображение не сохранено${block.alt ? ` («${escapeHtml(block.alt)}»)` : ''}: ${escapeHtml(block.note || 'недоступно')}</div>`;
    }
    case 'attachment': {
      const meta = [block.mimeType, block.size !== undefined ? formatBytes(block.size) : '', block.pageCount ? `${block.pageCount} стр.` : '']
        .filter(Boolean).map(escapeHtml).join(' · ');
      let html = `<div class="attachment"><div><strong>Вложение:</strong> ${escapeHtml(block.name)}${meta ? ` · ${meta}` : ''}</div><div class="note">${escapeHtml(block.note)}</div>`;
      if (block.extractedText) {
        html += `<details${open}><summary>Текст из файла</summary><div class="code-block"><pre><code>${escapeHtml(block.extractedText)}</code></pre></div></details>`;
      }
      return `${html}</div>`;
    }
    case 'artifact': {
      const label = block.kind === 'file' ? 'Файл' : block.kind === 'widget' ? 'Виджет' : 'Артефакт';
      return `<div class="artifact-title">${label}: ${escapeHtml(block.title)}</div>${renderMarkdown(fence(block.content, block.language))}`;
    }
    case 'thinking':
      return `<details${open}><summary>Размышления модели</summary>${renderMarkdown(block.text)}</details>`;
    case 'tool': {
      let inner = '';
      if (block.input) inner += `<div class="meta-line">Ввод</div><div class="code-block"><pre><code>${escapeHtml(block.input)}</code></pre></div>`;
      if (block.output) inner += `<div class="meta-line">Результат</div><div class="code-block"><pre><code>${escapeHtml(block.output)}</code></pre></div>`;
      return `<details${open}><summary>Инструмент: ${escapeHtml(block.name)}</summary>${inner}</details>`;
    }
    case 'notice':
      return `<div class="notice${block.level === 'error' ? ' error' : ''}">${escapeHtml(block.text)}</div>`;
    default:
      return '';
  }
}

/** @param {import('./model.js').ImageBlock} block */
function imageCaption(block) {
  const origin = { 'user-upload': 'Загружено пользователем', generated: 'Создано нейросетью', 'tool-output': 'Результат инструмента', unknown: '' }[block.origin] || '';
  return [origin, block.alt].filter(Boolean).join(': ');
}

/** @param {string} text */
function plainPreview(text, max = 90) {
  const t = String(text).replace(/[#*_`>[\]()!|-]+/g, ' ').replace(/\s+/g, ' ').trim();
  return t.length > max ? `${t.slice(0, max)}…` : t;
}

/**
 * @param {import('./model.js').NormalizedChat} chat
 * @param {HtmlOptions} [options]
 * @returns {string}
 */
export function chatToHtml(chat, options = {}) {
  const report = chat.completeness;
  const status = report?.status || 'complete';
  const service = SERVICE_LABELS[chat.service];
  const safeUrl = safeHref(chat.url);
  const meta = [
    `<span>${escapeHtml(service)}</span>`,
    `<span>Экспорт: ${escapeHtml(formatDateTime(chat.extractedAt))}</span>`,
    chat.createdAt ? `<span>Создан: ${escapeHtml(formatDateTime(chat.createdAt))}</span>` : '',
    chat.updatedAt ? `<span>Изменён: ${escapeHtml(formatDateTime(chat.updatedAt))}</span>` : '',
    chat.model ? `<span>Модель: ${escapeHtml(chat.model)}</span>` : '',
    `<span>Сообщений: ${chat.messages.length}</span>`,
    `<span class="badge ${escapeHtml(status)}">Полнота: ${escapeHtml(STATUS_LABELS[status] || status)}</span>`,
    safeUrl ? `<span><a href="${escapeHtml(safeUrl)}" rel="noopener noreferrer">${escapeHtml(safeUrl)}</a></span>` : '',
  ].filter(Boolean).join('');

  const warnings = [...(report?.warnings || []), ...chat.notes];
  const warningsHtml = warnings.length
    ? `<div class="warnings ${status === 'incomplete' ? 'incomplete' : ''}"><strong>${status === 'incomplete' ? 'Экспорт неполный' : 'Предупреждения'}</strong><ul>${warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul></div>`
    : '';

  const userMsgs = chat.messages.map((m, i) => ({ m, i })).filter(({ m }) => m.role === 'user');
  const toc = userMsgs.length > 3
    ? `<details class="toc"><summary>Содержание (${userMsgs.length} вопросов)</summary><ol>${userMsgs.map(({ m, i }) => {
      const firstText = m.blocks.find((b) => b.type === 'markdown');
      return `<li><a href="#m-${i + 1}">${escapeHtml(plainPreview(firstText && firstText.type === 'markdown' ? firstText.text : '(без текста)'))}</a></li>`;
    }).join('')}</ol></details>`
    : '';

  const body = chat.messages.map((m, i) => {
    const head = [`<span class="role">${escapeHtml(roleLabel(chat.service, m.role))}</span>`];
    if (m.createdAt) head.push(`<time datetime="${escapeHtml(m.createdAt)}">${escapeHtml(formatDateTime(m.createdAt))}</time>`);
    if (m.model && m.role === 'assistant') head.push(`<span>${escapeHtml(m.model)}</span>`);
    if (m.meta.branchCount) head.push(`<span>версия ${m.meta.branchIndex} из ${m.meta.branchCount}</span>`);
    head.push(`<a class="num" href="#m-${i + 1}">#${i + 1}</a>`);
    const blocks = m.blocks.map((b) => blockToHtml(b, options)).join('\n');
    const tail = m.meta.interrupted ? '<p class="meta-line">Ответ остановлен пользователем.</p>' : '';
    return `<article class="msg msg-${escapeHtml(m.role)}" id="m-${i + 1}"><div class="msg-head">${head.join('')}</div><div class="msg-body">${blocks}${tail}</div></article>`;
  }).join('\n');

  return `<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${EXPORT_CSP}">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<meta name="generator" content="${escapeHtml(options.generator || 'Chat Exporter')}">
<title>${escapeHtml(chat.title)} — ${escapeHtml(service)}</title>
<style>${CSS}</style>
</head>
<body>
<div class="page">
<header class="doc-header">
<h1>${escapeHtml(chat.title)}</h1>
<div class="doc-meta">${meta}</div>
${warningsHtml}
</header>
${toc}
<main>
${body}
</main>
<footer class="doc-footer">Документ создан локально расширением Chat Exporter. Идентификатор чата: ${escapeHtml(chat.id)}. Источник данных: ${chat.source === 'api' ? 'данные веб-приложения' : 'страница (DOM)'}.</footer>
</div>
</body>
</html>
`;
}
