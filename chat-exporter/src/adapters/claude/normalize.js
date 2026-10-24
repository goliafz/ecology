// Claude: converts claude.ai's conversation JSON
// (GET /api/organizations/{org}/chat_conversations/{id}?tree=True&rendering_mode=messages&render_all_tools=true)
// into a NormalizedChat. Pure function, testable in Node.
//
// The endpoint is internal and undocumented. The response contract used here is the one
// documented by the open-source agarwalvishal/claude-chat-exporter (CLAUDE.md): chat_messages[]
// with uuid / parent_message_uuid / index / sender / created_at / content[] / files[] / attachments[],
// plus current_leaf_message_uuid. All accesses are defensive.

import { SCHEMA_VERSION, toIso } from '../../core/model.js';
import { truncate } from '../../core/util.js';

const ROOT_SENTINEL = /^0{8}-0{4}-4000-8000-0{12}$/;
const TOOL_TEXT_LIMIT = 20_000;

/**
 * @typedef {Object} ClaudeNormalizeOptions
 * @property {boolean} [includeThinking]
 * @property {boolean} [includeTools]
 * @property {Record<string, string>} [images] preview URL -> data: URL
 * @property {Record<string, string>} [imageErrors]
 * @property {boolean} [pageGenerating] The open tab shows a streaming answer.
 * @property {string} [fallbackId]
 * @property {string} [nowIso]
 */

/**
 * @param {any} raw
 * @param {ClaudeNormalizeOptions} [options]
 * @returns {import('../../core/model.js').NormalizedChat}
 */
export function normalizeClaude(raw, options = {}) {
  const all = Array.isArray(raw?.chat_messages) ? raw.chat_messages : Array.isArray(raw?.messages) ? raw.messages : null;
  if (!all) throw new Error('Claude: в ответе нет массива chat_messages — формат API изменился');
  const id = String(raw.uuid || options.fallbackId || '');
  const notes = [];

  const { path, brokenLinks, leafId } = claudePath(all, raw.current_leaf_message_uuid);

  /** @type {Map<string | null, number>} parent -> number of children */
  const childCount = new Map();
  /** @type {Map<string, number>} uuid -> position among siblings (by created_at) */
  const siblingIndex = new Map();
  /** @type {Map<string, any[]>} */
  const byParent = new Map();
  for (const m of all) {
    const p = m.parent_message_uuid || null;
    childCount.set(p, (childCount.get(p) || 0) + 1);
    if (!byParent.has(p)) byParent.set(p, []);
    /** @type {any[]} */ (byParent.get(p)).push(m);
  }
  for (const list of byParent.values()) {
    list.sort((a, b) => String(a.created_at).localeCompare(String(b.created_at)));
    list.forEach((m, i) => siblingIndex.set(m.uuid, i + 1));
  }
  let alternatives = 0;
  for (const m of path) alternatives += Math.max(0, (childCount.get(m.parent_message_uuid || null) || 1) - 1);
  if (alternatives > 0) {
    notes.push(`В чате есть альтернативные версии сообщений (${alternatives}) после правок или перегенерации. Экспортирована текущая (видимая) ветка.`);
  }

  const artifacts = foldArtifacts(path, notes);

  /** @type {import('../../core/model.js').NormalizedMessage[]} */
  const messages = [];
  let imagesFailed = 0;

  path.forEach((m, msgIdx) => {
    const role = m.sender === 'human' ? 'user' : m.sender === 'assistant' ? 'assistant' : null;
    if (!role) {
      notes.push(`Пропущено сообщение с неизвестным отправителем «${m.sender}».`);
      return;
    }
    /** @type {import('../../core/model.js').Block[]} */
    const blocks = [];

    // Attachments are shown above the text in the UI.
    for (const f of Array.isArray(m.files) ? m.files : Array.isArray(m.files_v2) ? m.files_v2 : []) {
      const res = fileBlock(f, options);
      if (res.failed) imagesFailed += 1;
      blocks.push(res.block);
    }
    for (const a of Array.isArray(m.attachments) ? m.attachments : []) {
      blocks.push({
        type: 'attachment',
        name: String(a.file_name || 'вложение'),
        mimeType: a.file_type || undefined,
        size: typeof a.file_size === 'number' ? a.file_size : undefined,
        extractedText: typeof a.extracted_content === 'string' ? a.extracted_content : undefined,
        note: 'Оригинальный файл не сохранён; включён текст, извлечённый Claude из файла.',
      });
    }

    const content = Array.isArray(m.content) ? m.content : [];
    if (content.length === 0 && typeof m.text === 'string' && m.text.trim()) {
      blocks.push({ type: 'markdown', text: m.text });
    }
    content.forEach((block, blockIdx) => {
      if (!block || typeof block !== 'object') return;
      switch (block.type) {
        case 'text':
          if (typeof block.text === 'string' && block.text.trim()) blocks.push({ type: 'markdown', text: block.text });
          break;
        case 'thinking':
          if (options.includeThinking) {
            const text = block.thinking || block.text || '';
            if (String(text).trim()) blocks.push({ type: 'thinking', text: String(text) });
          }
          break;
        case 'tool_use': {
          const rendered = renderToolUse(block, msgIdx, blockIdx, artifacts, options);
          if (rendered) blocks.push(rendered);
          break;
        }
        case 'tool_result':
          if (options.includeTools) {
            const text = toolResultText(block);
            if (text.trim()) blocks.push({ type: 'tool', name: block.name || 'результат', output: truncate(text, TOOL_TEXT_LIMIT) });
          }
          break;
        default: {
          const text = typeof block.text === 'string' ? block.text : '';
          if (text.trim()) {
            blocks.push({ type: 'notice', level: 'info', text: `Неизвестный тип блока «${block.type}», сохранён как текст.` });
            blocks.push({ type: 'markdown', text });
          }
        }
      }
    });

    if (blocks.length === 0) return;
    const parentKey = m.parent_message_uuid || null;
    const meta = {};
    const siblings = childCount.get(parentKey) || 1;
    if (siblings > 1) {
      meta.branchIndex = siblingIndex.get(m.uuid) || 1;
      meta.branchCount = siblings;
    }
    if (m.stop_reason === 'user_canceled') meta.interrupted = true;
    if (m.truncated === true) meta.truncated = true;
    if (m.stop_reason) meta.status = String(m.stop_reason);

    messages.push({
      id: String(m.uuid),
      parentId: m.parent_message_uuid || null,
      role,
      createdAt: toIso(m.created_at),
      model: role === 'assistant' ? raw.model || null : null,
      blocks,
      meta,
    });
  });

  if (imagesFailed > 0) notes.push(`Не удалось встроить изображений: ${imagesFailed}. Вместо них в документе указана пометка.`);
  if (messages.some((m) => m.meta.truncated)) notes.push('Некоторые сообщения помечены сервисом как truncated (значение флага не документировано).');

  return {
    schemaVersion: SCHEMA_VERSION,
    service: 'claude',
    id,
    title: String(raw.name || 'Без названия'),
    url: id ? `https://claude.ai/chat/${id}` : 'https://claude.ai/',
    createdAt: toIso(raw.created_at),
    updatedAt: toIso(raw.updated_at),
    model: raw.model || null,
    source: 'api',
    extractedAt: options.nowIso || new Date().toISOString(),
    messages,
    integrity: {
      totalNodes: all.length,
      pathLength: path.length,
      brokenLinks,
      leafId,
      generating: Boolean(options.pageGenerating),
      imagesFailed,
    },
    notes,
  };
}

/**
 * Reconstructs the current branch: from the leaf up through parent_message_uuid.
 * Falls back to ordering by `index` (and checks that indexes are contiguous).
 * @param {any[]} all
 * @param {string | undefined} leaf
 */
export function claudePath(all, leaf) {
  /** @type {Map<string, any>} */
  const byId = new Map(all.map((m) => [String(m.uuid), m]));
  /** @type {string[]} */
  const brokenLinks = [];
  if (leaf && byId.has(leaf)) {
    const path = [];
    const seen = new Set();
    let cur = byId.get(leaf);
    while (cur) {
      if (seen.has(cur.uuid)) {
        brokenLinks.push(`cycle:${cur.uuid}`);
        break;
      }
      seen.add(cur.uuid);
      path.push(cur);
      const parent = cur.parent_message_uuid;
      if (!parent || ROOT_SENTINEL.test(parent)) break;
      const next = byId.get(parent);
      if (!next) {
        brokenLinks.push(String(parent));
        break;
      }
      cur = next;
    }
    path.reverse();
    return { path, brokenLinks, leafId: String(leaf) };
  }
  if (leaf) brokenLinks.push(String(leaf));
  // Fallback: no tree information. Valid only for linear chats.
  const sorted = [...all].sort((a, b) => (Number(a.index) || 0) - (Number(b.index) || 0));
  for (let i = 1; i < sorted.length; i++) {
    const prev = Number(sorted[i - 1].index);
    const cur = Number(sorted[i].index);
    if (Number.isFinite(prev) && Number.isFinite(cur) && cur !== prev + 1) brokenLinks.push(`index-gap:${prev}->${cur}`);
  }
  return { path: sorted, brokenLinks, leafId: sorted.length ? String(sorted[sorted.length - 1].uuid) : null };
}

/**
 * @typedef {{ title: string, type: string, language: string, content: string, lastPos: string }} ArtifactState
 */

/**
 * Folds artifact create/update/rewrite commands into the final version per artifact id.
 * @param {any[]} path
 * @param {string[]} notes
 * @returns {Map<string, ArtifactState>}
 */
export function foldArtifacts(path, notes) {
  /** @type {Map<string, ArtifactState>} */
  const map = new Map();
  path.forEach((m, msgIdx) => {
    (Array.isArray(m.content) ? m.content : []).forEach((/** @type {any} */ block, /** @type {number} */ blockIdx) => {
      if (block?.type !== 'tool_use' || block.name !== 'artifacts' || !block.input) return;
      const input = block.input;
      const artifactId = String(input.id || `anon-${msgIdx}-${blockIdx}`);
      const pos = `${msgIdx}:${blockIdx}`;
      const prev = map.get(artifactId);
      const command = input.command || 'create';
      if (command === 'update' && prev) {
        if (typeof input.old_str === 'string' && prev.content.includes(input.old_str)) {
          // A function replacement keeps "$&" and similar sequences in new_str literal.
          prev.content = prev.content.replace(input.old_str, () => String(input.new_str ?? ''));
        } else {
          notes.push(`Артефакт «${prev.title}»: правку не удалось применить, сохранена предыдущая версия.`);
        }
        prev.lastPos = pos;
        if (input.title) prev.title = input.title;
      } else {
        map.set(artifactId, {
          title: String(input.title || prev?.title || 'Артефакт'),
          type: String(input.type || prev?.type || ''),
          language: String(input.language || prev?.language || ''),
          content: typeof input.content === 'string' ? input.content : prev?.content || '',
          lastPos: pos,
        });
      }
    });
  });
  return map;
}

/** @param {string} type @param {string} language */
export function artifactLanguage(type, language) {
  if (language) return language;
  const map = {
    'text/markdown': 'markdown',
    'text/html': 'html',
    'image/svg+xml': 'svg',
    'application/vnd.ant.mermaid': 'mermaid',
    'application/vnd.ant.react': 'jsx',
    'application/vnd.ant.code': '',
  };
  return /** @type {Record<string, string>} */ (map)[type] ?? '';
}

/** @param {string} path */
function languageFromPath(path) {
  const ext = String(path).split('.').pop()?.toLowerCase() || '';
  const map = { js: 'javascript', ts: 'typescript', py: 'python', md: 'markdown', html: 'html', css: 'css', json: 'json', sh: 'bash', jsx: 'jsx', tsx: 'tsx', svg: 'svg', yml: 'yaml', yaml: 'yaml', sql: 'sql', java: 'java', go: 'go', rs: 'rust', cs: 'csharp', cpp: 'cpp', c: 'c' };
  return /** @type {Record<string, string>} */ (map)[ext] || ext;
}

/**
 * @param {any} block
 * @param {number} msgIdx
 * @param {number} blockIdx
 * @param {Map<string, ArtifactState>} artifacts
 * @param {ClaudeNormalizeOptions} options
 * @returns {import('../../core/model.js').Block | null}
 */
function renderToolUse(block, msgIdx, blockIdx, artifacts, options) {
  const input = block.input || {};
  if (block.name === 'artifacts') {
    const artifactId = String(input.id || `anon-${msgIdx}-${blockIdx}`);
    const state = artifacts.get(artifactId);
    if (!state || state.lastPos !== `${msgIdx}:${blockIdx}`) return null; // rendered once, at the last edit
    return { type: 'artifact', kind: 'artifact', title: state.title, language: artifactLanguage(state.type, state.language), content: state.content };
  }
  if (block.name === 'create_file' && typeof input.file_text === 'string') {
    return { type: 'artifact', kind: 'file', title: String(input.path || 'Файл'), language: languageFromPath(input.path || ''), content: input.file_text };
  }
  if (typeof block.name === 'string' && block.name.endsWith('show_widget') && typeof input.widget_code === 'string') {
    return { type: 'artifact', kind: 'widget', title: String(input.title || 'Виджет'), language: 'html', content: input.widget_code };
  }
  if (!options.includeTools) return null;
  let text = '';
  try {
    text = JSON.stringify(input, null, 2);
  } catch {
    text = String(input);
  }
  return { type: 'tool', name: String(block.name || 'tool'), input: truncate(text, TOOL_TEXT_LIMIT) };
}

/** @param {any} block */
function toolResultText(block) {
  const c = block.content;
  if (typeof c === 'string') return c;
  if (Array.isArray(c)) return c.map((x) => (typeof x?.text === 'string' ? x.text : '')).filter(Boolean).join('\n');
  return '';
}

/**
 * @param {any} f
 * @param {ClaudeNormalizeOptions} options
 * @returns {{ block: import('../../core/model.js').Block, failed: boolean }}
 */
function fileBlock(f, options) {
  const kind = f?.file_kind;
  const name = String(f?.file_name || 'файл');
  if (kind === 'image') {
    const url = imageUrlOf(f);
    const data = url ? options.images?.[url] : undefined;
    if (data) return { block: { type: 'image', src: data, alt: name, origin: 'user-upload', embedded: true }, failed: false };
    return {
      block: { type: 'image', src: '', alt: name, origin: 'user-upload', embedded: false, note: (url && options.imageErrors?.[url]) || 'Изображение не встроено (недоступно или отключено в настройках).' },
      failed: true,
    };
  }
  if (kind === 'document') {
    return {
      block: {
        type: 'attachment',
        name,
        pageCount: typeof f.document_asset?.page_count === 'number' ? f.document_asset.page_count : undefined,
        note: 'Оригинальный документ не сохранён: он доступен только в Claude.',
      },
      failed: false,
    };
  }
  return {
    block: {
      type: 'attachment',
      name,
      size: typeof f?.size_bytes === 'number' ? f.size_bytes : undefined,
      note: 'Оригинальный файл не сохранён: он доступен только в Claude.',
    },
    failed: false,
  };
}

/** @param {any} f */
function imageUrlOf(f) {
  return f?.preview_url || f?.preview_asset?.url || f?.thumbnail_url || f?.thumbnail_asset?.url || '';
}

/**
 * Image URLs the content script should resolve (same-origin claude.ai URLs).
 * @param {any} raw
 */
export function collectClaudeImageUrls(raw) {
  /** @type {Set<string>} */
  const out = new Set();
  for (const m of Array.isArray(raw?.chat_messages) ? raw.chat_messages : []) {
    for (const f of Array.isArray(m.files) ? m.files : Array.isArray(m.files_v2) ? m.files_v2 : []) {
      if (f?.file_kind === 'image') {
        const url = imageUrlOf(f);
        if (url) out.add(url);
      }
    }
  }
  return [...out];
}

/**
 * Normalizes one item of GET /api/organizations/{org}/chat_conversations.
 * @param {any} item
 * @returns {import('../../core/model.js').ChatListItem | null}
 */
export function normalizeClaudeListItem(item) {
  if (!item || typeof item.uuid !== 'string') return null;
  return {
    service: 'claude',
    id: item.uuid,
    title: String(item.name || 'Без названия'),
    updatedAt: toIso(item.updated_at),
    createdAt: toIso(item.created_at),
    project: item.project_uuid || item.project?.uuid || undefined,
    archived: Boolean(item.is_archived),
  };
}

/** @param {string} url */
export function claudeIdFromUrl(url) {
  try {
    const { pathname } = new URL(url);
    const m = pathname.match(/\/chat\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
    return m ? m[1] : null;
  } catch {
    return null;
  }
}
