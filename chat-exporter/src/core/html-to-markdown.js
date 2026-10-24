// DOM -> Markdown conversion used by the DOM fallback (content script).
// Works on any Element implementation (Chrome or jsdom in tests). The conversion is
// approximate by nature: it reconstructs Markdown from rendered HTML.

const SKIP_TAGS = new Set(['BUTTON', 'SVG', 'STYLE', 'SCRIPT', 'NOSCRIPT', 'TEMPLATE', 'INPUT', 'TEXTAREA', 'SELECT', 'CANVAS', 'VIDEO', 'AUDIO']);
const BLOCK_TAGS = new Set(['P', 'DIV', 'SECTION', 'ARTICLE', 'HEADER', 'FOOTER', 'MAIN', 'ASIDE', 'FIGURE', 'FIGCAPTION']);

/**
 * @typedef {Object} ConvertOptions
 * @property {(img: HTMLImageElement) => string | null} [imageSrc] Returns the src to keep (data: URL) or null.
 */

/**
 * @param {Element} root
 * @param {ConvertOptions} [options]
 * @returns {string}
 */
export function htmlToMarkdown(root, options = {}) {
  const out = convertChildren(root, { listDepth: 0, inPre: false, options });
  return out.replace(/\n{3,}/g, '\n\n').replace(/[ \t]+\n/g, '\n').trim();
}

/**
 * @typedef {{ listDepth: number, inPre: boolean, options: ConvertOptions }} Ctx
 */

/** @param {Node} node @param {Ctx} ctx */
function convertChildren(node, ctx) {
  let s = '';
  node.childNodes.forEach((child) => {
    s += convertNode(child, ctx);
  });
  return s;
}

/** @param {Element} el */
function isHidden(el) {
  const cls = typeof el.className === 'string' ? el.className : '';
  if (/\bsr-only\b/.test(cls)) return true;
  if (el.getAttribute('aria-hidden') === 'true' && !el.querySelector('img')) return true;
  return el.hasAttribute('hidden');
}

/** @param {string} text */
function inlineText(text) {
  return text.replace(/\s+/g, ' ');
}

/**
 * @param {Node} node
 * @param {Ctx} ctx
 * @returns {string}
 */
function convertNode(node, ctx) {
  if (node.nodeType === 3) {
    const text = node.textContent || '';
    return ctx.inPre ? text : inlineText(text);
  }
  if (node.nodeType !== 1) return '';
  const el = /** @type {Element} */ (node);
  const tag = el.tagName.toUpperCase();
  if (SKIP_TAGS.has(tag) || tag === 'svg') return '';

  // KaTeX: keep the TeX source.
  if (el.classList.contains('katex') || el.classList.contains('katex-display')) {
    const tex = el.querySelector('annotation[encoding="application/x-tex"]')?.textContent;
    if (tex) return el.classList.contains('katex-display') || el.closest('.katex-display') ? `\n\n$$\n${tex.trim()}\n$$\n\n` : `$${tex.trim()}$`;
  }
  if (isHidden(el)) return '';

  switch (tag) {
    case 'H1': case 'H2': case 'H3': case 'H4': case 'H5': case 'H6':
      return `\n\n${'#'.repeat(Number(tag[1]))} ${convertChildren(el, ctx).trim()}\n\n`;
    case 'BR':
      return ctx.inPre ? '\n' : '  \n';
    case 'HR':
      return '\n\n---\n\n';
    case 'STRONG': case 'B': {
      const t = convertChildren(el, ctx).trim();
      return t ? `**${t}**` : '';
    }
    case 'EM': case 'I': {
      const t = convertChildren(el, ctx).trim();
      return t ? `*${t}*` : '';
    }
    case 'DEL': case 'S': {
      const t = convertChildren(el, ctx).trim();
      return t ? `~~${t}~~` : '';
    }
    case 'CODE': {
      if (ctx.inPre) return el.textContent || '';
      const t = el.textContent || '';
      const ticks = t.includes('`') ? '``' : '`';
      return `${ticks}${t}${ticks}`;
    }
    case 'PRE':
      return preToMarkdown(el);
    case 'A': {
      const href = el.getAttribute('href') || '';
      const t = convertChildren(el, ctx).trim() || href;
      if (!/^(https?:|mailto:)/i.test(href)) return t;
      return `[${t.replace(/[[\]]/g, '\\$&')}](${href.replace(/\)/g, '%29')})`;
    }
    case 'IMG': {
      const img = /** @type {HTMLImageElement} */ (el);
      const alt = (img.getAttribute('alt') || 'изображение').replace(/[[\]\n]/g, ' ');
      const src = ctx.options.imageSrc ? ctx.options.imageSrc(img) : img.getAttribute('src');
      return src ? `![${alt}](${src})` : `*[Изображение: ${alt}]*`;
    }
    case 'UL': case 'OL':
      return listToMarkdown(el, ctx);
    case 'BLOCKQUOTE': {
      const inner = convertChildren(el, ctx).trim();
      return `\n\n${inner.split('\n').map((l) => (l ? `> ${l}` : '>')).join('\n')}\n\n`;
    }
    case 'TABLE':
      return tableToMarkdown(el, ctx);
    case 'DETAILS': {
      const summary = el.querySelector(':scope > summary');
      const title = summary ? convertChildren(summary, ctx).trim() : 'Подробнее';
      const clone = /** @type {Element} */ (el.cloneNode(true));
      clone.querySelector(':scope > summary')?.remove();
      return `\n\n<details>\n<summary>${title}</summary>\n\n${convertChildren(clone, ctx).trim()}\n\n</details>\n\n`;
    }
    case 'LI':
      return convertChildren(el, ctx);
    default:
      if (BLOCK_TAGS.has(tag)) return `\n\n${convertChildren(el, ctx).trim()}\n\n`;
      return convertChildren(el, ctx);
  }
}

/** @param {Element} pre */
function preToMarkdown(pre) {
  const code = pre.querySelector('code');
  const source = code || pre;
  let language = '';
  const cls = `${code?.getAttribute('class') || ''} ${pre.getAttribute('class') || ''}`;
  const m = /language-([\w+#.-]+)/.exec(cls);
  if (m) language = m[1];
  const text = (source.textContent || '').replace(/\n$/, '');
  const longest = Math.max(2, ...Array.from(text.matchAll(/`+/g), (x) => x[0].length));
  const ticks = '`'.repeat(longest + 1);
  return `\n\n${ticks}${language}\n${text}\n${ticks}\n\n`;
}

/** @param {Element} list @param {Ctx} ctx */
function listToMarkdown(list, ctx) {
  const ordered = list.tagName.toUpperCase() === 'OL';
  let n = Number(list.getAttribute('start') || 1);
  const indent = '   '.repeat(ctx.listDepth);
  const items = Array.from(list.children).filter((c) => c.tagName.toUpperCase() === 'LI');
  const lines = items.map((li) => {
    const marker = ordered ? `${n++}.` : '-';
    const inner = convertChildren(li, { ...ctx, listDepth: ctx.listDepth + 1 }).trim().replace(/\n{2,}/g, '\n');
    const [first, ...rest] = inner.split('\n');
    return [`${indent}${marker} ${first}`, ...rest.map((l) => (l.startsWith(indent + '   ') ? l : `${indent}   ${l}`))].join('\n');
  });
  return `\n\n${lines.join('\n')}\n\n`;
}

/** @param {Element} table @param {Ctx} ctx */
function tableToMarkdown(table, ctx) {
  const rows = Array.from(table.querySelectorAll('tr'));
  if (rows.length === 0) return '';
  const cell = (/** @type {Element} */ c) => convertChildren(c, ctx).trim().replace(/\n+/g, ' ').replace(/\|/g, '\\|');
  const matrix = rows.map((r) => Array.from(r.children).filter((c) => /^(TD|TH)$/i.test(c.tagName)).map(cell));
  const width = Math.max(...matrix.map((r) => r.length));
  const norm = matrix.map((r) => [...r, ...Array(width - r.length).fill('')]);
  const [head, ...body] = norm;
  const line = (/** @type {string[]} */ r) => `| ${r.join(' | ')} |`;
  return `\n\n${[line(head), line(Array(width).fill('---')), ...body.map(line)].join('\n')}\n\n`;
}
