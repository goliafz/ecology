// Markdown -> safe HTML for the exported documents.
// Raw HTML inside messages is shown as text (never executed), links are limited to safe
// schemes, and remote images are replaced by links so the exported file never loads
// anything from the network on its own.
import { Marked } from '../vendor/marked.esm.js';

/** @param {string} s */
export function escapeHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/**
 * @param {string} href
 * @returns {string | null} safe href or null
 */
export function safeHref(href) {
  const h = String(href || '').trim();
  if (!h) return null;
  if (h.startsWith('#')) return h;
  try {
    const u = new URL(h);
    if (['http:', 'https:', 'mailto:'].includes(u.protocol)) return u.href;
  } catch {
    // relative URLs are meaningless in an exported file
  }
  return null;
}

/** @param {string} src */
export function isDataImage(src) {
  return /^data:image\/(png|jpe?g|gif|webp|bmp|svg\+xml|avif);base64,[a-z0-9+/=\s]+$/i.test(String(src || ''));
}

const marked = new Marked({ gfm: true, breaks: false, async: false });

marked.use({
  renderer: {
    html({ text }) {
      return escapeHtml(text);
    },
    link({ href, title, tokens }) {
      const text = this.parser.parseInline(tokens);
      const safe = safeHref(href);
      if (!safe) return text;
      const t = title ? ` title="${escapeHtml(title)}"` : '';
      return `<a href="${escapeHtml(safe)}"${t} rel="noopener noreferrer">${text}</a>`;
    },
    image({ href, title, text }) {
      if (isDataImage(href)) {
        return `<img src="${escapeHtml(href)}" alt="${escapeHtml(text || '')}"${title ? ` title="${escapeHtml(title)}"` : ''}>`;
      }
      const safe = safeHref(href);
      const label = `Изображение${text ? `: ${escapeHtml(text)}` : ''} (внешнее, не загружено)`;
      return safe ? `<a class="ext-image" href="${escapeHtml(safe)}" rel="noopener noreferrer">[${label}]</a>` : `[${label}]`;
    },
    code({ text, lang }) {
      const language = (lang || '').trim().split(/\s+/)[0] || '';
      const cls = language ? ` class="language-${escapeHtml(language)}"` : '';
      const label = language ? `<div class="code-lang">${escapeHtml(language)}</div>` : '';
      return `<div class="code-block">${label}<pre><code${cls}>${escapeHtml(text.replace(/\n$/, ''))}\n</code></pre></div>\n`;
    },
    table(token) {
      // Wrap tables so wide ones scroll on screen and shrink in print.
      let header = '';
      for (const cell of token.header) header += this.tablecell(cell);
      let body = '';
      for (const row of token.rows) {
        let cells = '';
        for (const cell of row) cells += this.tablecell(cell);
        body += this.tablerow({ text: cells });
      }
      return `<div class="table-wrap"><table><thead>${this.tablerow({ text: header })}</thead>${body ? `<tbody>${body}</tbody>` : ''}</table></div>\n`;
    },
  },
});

/**
 * @param {string} markdown
 * @returns {string} HTML fragment
 */
export function renderMarkdown(markdown) {
  return /** @type {string} */ (marked.parse(String(markdown ?? '')));
}
