// ChatGPT: converts the web app's conversation JSON (GET /backend-api/conversation/{id})
// into a NormalizedChat. Pure function: no network, no DOM, testable in Node.
//
// The endpoint is internal and undocumented. Field names below were taken from the
// open-source pionxzh/chatgpt-exporter (src/api.ts). Every access is defensive so a
// changed shape degrades into warnings instead of a crash.

import { SCHEMA_VERSION, toIso } from '../../core/model.js';
import { truncate } from '../../core/util.js';

const HIDDEN_CONTENT_TYPES = new Set(['user_editable_context', 'model_editable_context']);
const TOOL_TEXT_LIMIT = 20_000;

/**
 * @typedef {Object} ChatGptNormalizeOptions
 * @property {boolean} [includeThinking]
 * @property {boolean} [includeTools]
 * @property {Record<string, string>} [images] asset pointer or URL -> data: URL
 * @property {Record<string, string>} [imageErrors] asset pointer -> reason
 * @property {string} [fallbackId]
 * @property {string} [nowIso]
 */

/**
 * @param {any} raw
 * @param {ChatGptNormalizeOptions} [options]
 * @returns {import('../../core/model.js').NormalizedChat}
 */
export function normalizeChatGpt(raw, options = {}) {
  if (!raw || typeof raw !== 'object' || !raw.mapping || typeof raw.mapping !== 'object') {
    throw new Error('ChatGPT: в ответе нет поля mapping — формат API изменился');
  }
  const mapping = /** @type {Record<string, any>} */ (raw.mapping);
  const id = String(raw.conversation_id || raw.id || options.fallbackId || '');
  const notes = [];

  const { path, brokenLinks, leafId } = currentPath(mapping, raw.current_node);

  // Alternative versions (edits / regenerations) that are not on the current branch.
  let alternatives = 0;
  for (const nodeId of path) {
    const parent = mapping[mapping[nodeId]?.parent];
    if (parent && Array.isArray(parent.children) && parent.children.length > 1) alternatives += parent.children.length - 1;
  }
  if (alternatives > 0) {
    notes.push(`В чате есть альтернативные версии сообщений (${alternatives}) после правок или перегенерации. Экспортирована текущая (видимая) ветка.`);
  }

  /** @type {import('../../core/model.js').NormalizedMessage[]} */
  const messages = [];
  let generating = false;
  let lastModel = null;
  let imagesFailed = 0;

  for (const nodeId of path) {
    const node = mapping[nodeId];
    const msg = node?.message;
    if (!msg) continue;
    const role = msg.author?.role;
    const contentType = msg.content?.content_type;
    if (role === 'system') continue;
    if (msg.metadata?.is_visually_hidden_from_conversation) continue;
    if (HIDDEN_CONTENT_TYPES.has(contentType)) continue;

    if (msg.status === 'in_progress') generating = true;
    if (role === 'assistant' && msg.metadata?.model_slug) lastModel = msg.metadata.model_slug;

    const converted = convertContent(msg, role, options);
    imagesFailed += converted.imagesFailed;
    const blocks = [...attachmentBlocks(msg), ...converted.blocks];
    if (blocks.length === 0) continue;

    const normalizedRole = role === 'user' ? 'user' : 'assistant';
    const parent = mapping[node.parent];
    const branchCount = parent && Array.isArray(parent.children) ? parent.children.length : 1;
    const meta = {};
    if (branchCount > 1) {
      meta.branchIndex = parent.children.indexOf(nodeId) + 1;
      meta.branchCount = branchCount;
    }
    if (msg.metadata?.finish_details?.type === 'interrupted') meta.interrupted = true;
    if (msg.status) meta.status = msg.status;

    const prev = messages[messages.length - 1];
    // ChatGPT shows tool calls, tool output and the final answer as one assistant turn.
    if (normalizedRole === 'assistant' && prev && prev.role === 'assistant') {
      prev.blocks.push(...blocks);
      /** @type {any} */ (prev.meta).sourceIds.push(msg.id || nodeId);
      if (role === 'assistant' && hasText(blocks)) {
        prev.id = msg.id || nodeId; // DOM uses the id of the message holding the text
        prev.model = msg.metadata?.model_slug || prev.model;
      }
      Object.assign(prev.meta, meta, { sourceIds: /** @type {any} */ (prev.meta).sourceIds });
      continue;
    }

    messages.push({
      id: msg.id || nodeId,
      parentId: node.parent || null,
      role: normalizedRole,
      createdAt: toIso(msg.create_time),
      model: role === 'assistant' ? msg.metadata?.model_slug || null : null,
      blocks,
      meta: { ...meta, sourceIds: [msg.id || nodeId] },
    });
  }

  if (imagesFailed > 0) notes.push(`Не удалось встроить изображений: ${imagesFailed}. Вместо них в документе указана пометка.`);

  return {
    schemaVersion: SCHEMA_VERSION,
    service: 'chatgpt',
    id,
    title: String(raw.title || 'Без названия'),
    url: id ? `https://chatgpt.com/c/${id}` : 'https://chatgpt.com/',
    createdAt: toIso(raw.create_time),
    updatedAt: toIso(raw.update_time),
    model: lastModel,
    source: 'api',
    extractedAt: options.nowIso || new Date().toISOString(),
    messages,
    integrity: {
      totalNodes: Object.keys(mapping).length,
      pathLength: path.length,
      brokenLinks,
      leafId,
      generating,
      imagesFailed,
      pathIds: [...new Set(path.flatMap((n) => [n, mapping[n]?.message?.id].filter(Boolean)))],
    },
    notes,
  };
}

/**
 * Walks from the current node to the root. Detects missing parents and cycles.
 * @param {Record<string, any>} mapping
 * @param {string | undefined} currentNode
 */
export function currentPath(mapping, currentNode) {
  /** @type {string[]} */
  const brokenLinks = [];
  let leafId = currentNode && mapping[currentNode] ? currentNode : null;
  if (!leafId) {
    // Fallback: the deepest node following the last child at each step from the root.
    const root = Object.keys(mapping).find((k) => !mapping[k].parent || !mapping[mapping[k].parent]);
    let cursor = root;
    const seen = new Set();
    while (cursor && mapping[cursor] && !seen.has(cursor)) {
      seen.add(cursor);
      const children = mapping[cursor].children || [];
      if (children.length === 0) break;
      cursor = children[children.length - 1];
    }
    leafId = cursor || null;
    if (currentNode) brokenLinks.push(String(currentNode));
  }
  /** @type {string[]} */
  const path = [];
  const seen = new Set();
  let cursor = leafId;
  while (cursor) {
    if (seen.has(cursor)) {
      brokenLinks.push(`cycle:${cursor}`);
      break;
    }
    seen.add(cursor);
    const node = mapping[cursor];
    if (!node) {
      brokenLinks.push(cursor);
      break;
    }
    path.push(cursor);
    cursor = node.parent || null;
  }
  path.reverse();
  return { path, brokenLinks, leafId };
}

/** @param {import('../../core/model.js').Block[]} blocks */
function hasText(blocks) {
  return blocks.some((b) => b.type === 'markdown' && b.text.trim());
}

/**
 * @param {any} msg
 * @returns {import('../../core/model.js').AttachmentBlock[]}
 */
function attachmentBlocks(msg) {
  const list = Array.isArray(msg.metadata?.attachments) ? msg.metadata.attachments : [];
  return list
    .filter((a) => a && !String(a.mime_type || '').startsWith('image/'))
    .map((a) => ({
      type: 'attachment',
      name: String(a.name || a.id || 'файл'),
      mimeType: a.mime_type || undefined,
      size: typeof a.size === 'number' ? a.size : undefined,
      note: 'Оригинальный файл не сохранён: он доступен только в ChatGPT.',
    }));
}

/**
 * Replaces ChatGPT citation markers with Markdown links where the API provides them.
 * Unknown markers (private-use characters) are removed.
 * @param {string} text
 * @param {any} metadata
 */
export function cleanCitations(text, metadata) {
  let out = text;
  const refs = Array.isArray(metadata?.content_references) ? metadata.content_references : [];
  for (const ref of refs) {
    if (!ref || typeof ref.matched_text !== 'string' || !ref.matched_text) continue;
    let replacement = '';
    if (typeof ref.alt === 'string' && ref.alt.trim()) replacement = ref.alt;
    else if (ref.type === 'webpage' && ref.url) replacement = ` ([${ref.title || ref.url}](${ref.url}))`;
    out = out.split(ref.matched_text).join(replacement);
  }
  // Remaining markers look like "\uE200cite\uE202turn0search3\uE201".
  out = out.replace(/ ?\uE200[^\uE201]*\uE201/g, '');
  out = out.replace(/[\uE200-\uE2FF]/g, '');
  return out;
}

/**
 * @param {any} msg
 * @param {string} role
 * @param {ChatGptNormalizeOptions} options
 * @returns {{ blocks: import('../../core/model.js').Block[], imagesFailed: number }}
 */
function convertContent(msg, role, options) {
  const content = msg.content || {};
  const type = content.content_type;
  /** @type {import('../../core/model.js').Block[]} */
  const blocks = [];
  let imagesFailed = 0;

  const pushImage = (/** @type {any} */ part, /** @type {any} */ origin) => {
    const pointer = String(part.asset_pointer || part.image_url || '');
    const data = options.images?.[pointer];
    if (data) {
      blocks.push({ type: 'image', src: data, alt: part.metadata?.dalle?.prompt || '', origin, embedded: true });
    } else {
      imagesFailed += 1;
      blocks.push({
        type: 'image',
        src: '',
        alt: part.metadata?.dalle?.prompt || '',
        origin,
        embedded: false,
        note: options.imageErrors?.[pointer] || 'Изображение не встроено (недоступно или отключено в настройках).',
      });
    }
  };

  switch (type) {
    case 'text': {
      const parts = Array.isArray(content.parts) ? content.parts : [];
      let text = parts.filter((p) => typeof p === 'string').join('\n\n');
      const fallback = msg.metadata?.model_dil_v2?.fallbackMarkdown;
      if (typeof fallback === 'string' && fallback.trim() && !text.trim()) text = fallback;
      text = cleanCitations(text, msg.metadata);
      if (role === 'tool') {
        if (options.includeTools && text.trim()) blocks.push({ type: 'tool', name: msg.author?.name || 'tool', output: truncate(text, TOOL_TEXT_LIMIT) });
      } else if (text.trim()) {
        blocks.push({ type: 'markdown', text });
      }
      break;
    }
    case 'multimodal_text': {
      const parts = Array.isArray(content.parts) ? content.parts : [];
      const textParts = [];
      for (const part of parts) {
        if (typeof part === 'string') {
          if (part.trim()) textParts.push(part);
        } else if (part && part.content_type === 'image_asset_pointer') {
          if (textParts.length) blocks.push({ type: 'markdown', text: cleanCitations(textParts.splice(0).join('\n\n'), msg.metadata) });
          const origin = role === 'user' ? 'user-upload' : part.metadata?.dalle || role === 'tool' ? 'generated' : 'unknown';
          pushImage(part, origin);
        } else if (part && part.content_type === 'audio_transcription' && typeof part.text === 'string') {
          textParts.push(part.text);
        } else if (part && /audio|video/.test(String(part.content_type))) {
          blocks.push({ type: 'attachment', name: 'Аудио/видео', note: 'Медиафайл голосового режима не сохранён; текст расшифровки включён, если он есть.' });
        }
      }
      if (textParts.length) blocks.push({ type: 'markdown', text: cleanCitations(textParts.join('\n\n'), msg.metadata) });
      break;
    }
    case 'code': {
      if (options.includeTools && typeof content.text === 'string' && content.text.trim()) {
        blocks.push({
          type: 'tool',
          name: `${msg.recipient && msg.recipient !== 'all' ? msg.recipient : 'code'}`,
          input: truncate(content.text, TOOL_TEXT_LIMIT),
        });
      }
      break;
    }
    case 'execution_output': {
      const images = msg.metadata?.aggregate_result?.messages || [];
      for (const m of images) {
        if (m && m.message_type === 'image') pushImage({ image_url: m.image_url }, 'tool-output');
      }
      if (options.includeTools && typeof content.text === 'string' && content.text.trim()) {
        blocks.push({ type: 'tool', name: msg.author?.name || 'python', output: truncate(content.text, TOOL_TEXT_LIMIT) });
      }
      break;
    }
    case 'thoughts': {
      if (options.includeThinking && Array.isArray(content.thoughts)) {
        const text = content.thoughts
          .map((t) => [t?.summary ? `**${t.summary}**` : '', t?.content || ''].filter(Boolean).join('\n\n'))
          .filter(Boolean)
          .join('\n\n');
        if (text.trim()) blocks.push({ type: 'thinking', text });
      }
      break;
    }
    case 'reasoning_recap':
      // "Thought for N seconds": UI decoration, skipped.
      break;
    case 'tether_quote':
    case 'tether_browsing_display': {
      if (options.includeTools) {
        const text = [content.title, content.url, content.text || content.result || content.summary].filter(Boolean).join('\n');
        if (text.trim()) blocks.push({ type: 'tool', name: 'browser', output: truncate(text, TOOL_TEXT_LIMIT) });
      }
      break;
    }
    default: {
      // Unknown content type: keep any text so nothing silently disappears.
      const text = typeof content.text === 'string' ? content.text
        : Array.isArray(content.parts) ? content.parts.filter((p) => typeof p === 'string').join('\n\n') : '';
      if (text.trim() && role !== 'tool') {
        blocks.push({ type: 'notice', level: 'info', text: `Неизвестный тип содержимого «${type}», сохранён как текст.` });
        blocks.push({ type: 'markdown', text });
      } else if (text.trim() && options.includeTools) {
        blocks.push({ type: 'tool', name: String(type || 'unknown'), output: truncate(text, TOOL_TEXT_LIMIT) });
      }
    }
  }
  return { blocks, imagesFailed };
}

/**
 * Collects image pointers the content script should resolve before normalization.
 * @param {any} raw
 * @returns {string[]}
 */
export function collectChatGptImagePointers(raw) {
  /** @type {Set<string>} */
  const out = new Set();
  for (const node of Object.values(raw?.mapping || {})) {
    const content = /** @type {any} */ (node)?.message?.content;
    if (!content) continue;
    if (content.content_type === 'multimodal_text' && Array.isArray(content.parts)) {
      for (const p of content.parts) {
        if (p && p.content_type === 'image_asset_pointer' && typeof p.asset_pointer === 'string') out.add(p.asset_pointer);
      }
    }
    if (content.content_type === 'execution_output') {
      for (const m of /** @type {any} */ (node).message.metadata?.aggregate_result?.messages || []) {
        if (m?.message_type === 'image' && typeof m.image_url === 'string') out.add(m.image_url);
      }
    }
  }
  return [...out];
}

/**
 * Normalizes one item of GET /backend-api/conversations.
 * @param {any} item
 * @returns {import('../../core/model.js').ChatListItem | null}
 */
export function normalizeChatGptListItem(item) {
  if (!item || typeof item.id !== 'string') return null;
  return {
    service: 'chatgpt',
    id: item.id,
    title: String(item.title || 'Без названия'),
    updatedAt: toIso(item.update_time),
    createdAt: toIso(item.create_time),
    archived: Boolean(item.is_archived),
    project: item.gizmo_id || undefined,
  };
}

/**
 * Extracts the conversation id from a chatgpt.com URL.
 * Formats: /c/<uuid>, /g/<gizmo>/c/<uuid>, /g/<gizmo>/project/c/<uuid> (best effort).
 * @param {string} url
 */
export function chatGptIdFromUrl(url) {
  try {
    const { pathname } = new URL(url);
    const m = pathname.match(/\/c\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
    return m ? m[1] : null;
  } catch {
    return null;
  }
}
