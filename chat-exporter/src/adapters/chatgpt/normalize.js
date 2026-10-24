// ChatGPT: converts the web app's conversation JSON (GET /backend-api/conversation/{id})
// into a NormalizedChat. Pure function: no network, no DOM, testable in Node.
//
// The endpoint is internal and undocumented. Field names below were taken from the
// open-source pionxzh/chatgpt-exporter (src/api.ts). Every access is defensive so a
// changed shape degrades into warnings instead of a crash.

import { SCHEMA_VERSION, toIso } from '../../core/model.js';
import { truncate } from '../../core/util.js';

const HIDDEN_CONTENT_TYPES = new Set(['user_editable_context', 'model_editable_context']);
/** Block types that count as a visible answer (tool calls and thinking alone do not). */
const VISIBLE_BLOCKS = new Set(['markdown', 'code', 'artifact', 'image']);
/** Keys whose string values are message text in unknown content shapes. */
const TEXT_KEYS = new Set(['text', 'parts', 'content', 'markdown', 'value', 'summary', 'result', 'fallbackMarkdown']);
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
  let rescuedTurns = 0;
  let lastPathRole = null;
  /** Ids the DOM may show that are accounted for (exported or deliberately hidden). */
  const knownIds = new Set();
  /** Assistant/tool messages of the current turn that produced nothing visible. */
  /** @type {Array<{ id: string, text: string, type: string, time: unknown, nodeParent: string | null, recipient: string }>} */
  let turnSkipped = [];
  let turnHasOutput = false;
  /** @type {string[]} */
  const unextracted = [];
  const ctx = { canvas: /** @type {{ title: string, type: string, content: string } | null} */ (null) };

  // An answer must never disappear silently: if a turn produced no visible content but its
  // messages carry text in a format this adapter does not know, that text is saved as-is.
  const flushTurn = () => {
    if (!turnHasOutput && turnSkipped.length > 0) {
      const types = [...new Set(turnSkipped.map((t) => t.recipient && t.recipient !== 'all' ? `${t.type} → ${t.recipient}` : t.type))].join(', ');
      /** @type {import('../../core/model.js').Block[]} */
      const blocks = [{ type: 'notice', level: 'warning', text: `Ответ сохранён в исходном виде: его формат не распознан (${types}).` }];
      for (const t of turnSkipped) {
        blocks.push(t.type === 'code' ? { type: 'code', language: '', code: t.text } : { type: 'markdown', text: t.text });
      }
      const last = turnSkipped[turnSkipped.length - 1];
      messages.push({
        id: last.id,
        parentId: last.nodeParent,
        role: 'assistant',
        createdAt: toIso(last.time),
        model: null,
        blocks,
        meta: { sourceIds: turnSkipped.map((t) => t.id) },
      });
      rescuedTurns += 1;
    }
    for (const t of turnSkipped) knownIds.add(t.id);
    turnSkipped = [];
    turnHasOutput = false;
  };

  for (const nodeId of path) {
    const node = mapping[nodeId];
    const msg = node?.message;
    if (!msg) {
      knownIds.add(nodeId);
      continue;
    }
    const msgId = String(msg.id || nodeId);
    const role = msg.author?.role;
    const contentType = msg.content?.content_type;
    if (role === 'user') flushTurn();
    if (role === 'system' || msg.metadata?.is_visually_hidden_from_conversation || HIDDEN_CONTENT_TYPES.has(contentType)) {
      knownIds.add(msgId).add(nodeId);
      continue;
    }
    if (role === 'user' || role === 'assistant' || role === 'tool') lastPathRole = role === 'user' ? 'user' : 'assistant';

    if (msg.status === 'in_progress') generating = true;
    if (role === 'assistant' && msg.metadata?.model_slug) lastModel = msg.metadata.model_slug;

    const converted = convertContent(msg, role, options, ctx);
    imagesFailed += converted.imagesFailed;
    const blocks = [...attachmentBlocks(msg), ...converted.blocks];
    if (blocks.length === 0) {
      const text = role === 'user' || msg.channel === 'analysis' ? '' : anyText(msg);
      if (text.trim()) {
        turnSkipped.push({ id: msgId, text, type: String(contentType || 'unknown'), time: msg.create_time, nodeParent: node.parent || null, recipient: String(msg.recipient || '') });
      } else if (contentType === 'thoughts' || contentType === 'reasoning_recap' || msg.channel === 'analysis') {
        knownIds.add(msgId).add(nodeId);
      } else {
        unextracted.push(msgId);
      }
      continue;
    }
    knownIds.add(msgId).add(nodeId);

    const normalizedRole = role === 'user' ? 'user' : 'assistant';
    if (normalizedRole === 'assistant' && blocks.some((b) => VISIBLE_BLOCKS.has(b.type))) turnHasOutput = true;
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
      /** @type {any} */ (prev.meta).sourceIds.push(msgId);
      if (role === 'assistant' && hasText(blocks)) {
        prev.id = msgId; // DOM uses the id of the message holding the text
        prev.model = msg.metadata?.model_slug || prev.model;
      }
      Object.assign(prev.meta, meta, { sourceIds: /** @type {any} */ (prev.meta).sourceIds });
      continue;
    }

    messages.push({
      id: msgId,
      parentId: node.parent || null,
      role: normalizedRole,
      createdAt: toIso(msg.create_time),
      model: role === 'assistant' ? msg.metadata?.model_slug || null : null,
      blocks,
      meta: { ...meta, sourceIds: [msgId] },
    });
  }
  flushTurn();

  if (rescuedTurns > 0) notes.push(`Ответов в нераспознанном формате: ${rescuedTurns}. Они сохранены в исходном виде; проверьте их оформление.`);
  if (unextracted.length > 0) notes.push(`Сообщений без извлекаемого содержимого: ${unextracted.length}.`);
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
      pathIds: [...knownIds],
      rescuedTurns,
      unextracted: unextracted.length,
      lastPathRole,
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
 * Applies ChatGPT content references to the message text.
 *
 * `content_references` mark spans of the text (`matched_text`). Citation markers are wrapped in
 * private-use characters ("citeturn0search3") and are replaced by their
 * Markdown link (`alt`) or removed. Other references point at real content: a
 * `client_defined_widget` with widget_type "code_block" marks a whole ```code block``` that the UI
 * draws as a widget. Such spans are kept as they are: deleting them deleted the answer itself.
 * @param {string} text
 * @param {any} metadata
 */
export function cleanCitations(text, metadata) {
  let out = text;
  const refs = Array.isArray(metadata?.content_references) ? metadata.content_references : [];
  for (const ref of refs) {
    if (!ref || typeof ref.matched_text !== 'string' || !ref.matched_text) continue;
    const hasAlt = typeof ref.alt === 'string' && ref.alt.trim();
    const isMarker = /[-]/.test(ref.matched_text);
    if (!hasAlt && !isMarker) continue; // real content (code block widget etc.): keep it
    let replacement = '';
    if (hasAlt) replacement = ref.alt;
    else if (ref.type === 'webpage' && ref.url) replacement = ` ([${ref.title || ref.url}](${ref.url}))`;
    out = out.split(ref.matched_text).join(replacement);
  }
  out = out.replace(/ ?[^]*/g, '');
  out = out.replace(/[-]/g, '');
  return out;
}

/**
 * Best-effort text of any message content, used so an answer in an unknown or new format is
 * saved as-is instead of disappearing.
 * @param {any} msg
 */
export function anyText(msg) {
  const fallback = msg?.metadata?.model_dil_v2?.fallbackMarkdown;
  if (typeof fallback === 'string' && fallback.trim()) return fallback;
  /** @type {string[]} */
  const out = [];
  const visit = (/** @type {any} */ v, /** @type {number} */ depth, /** @type {string} */ key) => {
    if (depth > 8 || v === null || v === undefined) return;
    if (typeof v === 'string') {
      if (v.trim() && TEXT_KEYS.has(key)) out.push(v);
    } else if (Array.isArray(v)) {
      for (const x of v) visit(x, depth + 1, key === 'parts' ? 'text' : key);
    } else if (typeof v === 'object') {
      for (const [k, x] of Object.entries(v)) visit(x, depth + 1, k);
    }
  };
  visit(msg?.content, 0, '');
  return out.join('\n\n');
}

/**
 * ChatGPT canvas ("canmore"): the document arrives as a call with JSON arguments.
 * create_textdoc -> { name, type: 'document' | 'code/<lang>', content };
 * update_textdoc -> { updates: [{ pattern, multiple, replacement }] } applied to the last document.
 * @param {any} msg
 * @param {{ canvas: { title: string, type: string, content: string } | null }} ctx
 * @param {ChatGptNormalizeOptions} options
 * @returns {import('../../core/model.js').Block[] | null} null when the call cannot be parsed
 */
export function canvasBlocks(msg, ctx, options) {
  const action = String(msg.recipient).slice('canmore.'.length);
  const raw = typeof msg.content?.text === 'string' ? msg.content.text
    : Array.isArray(msg.content?.parts) && typeof msg.content.parts[0] === 'string' ? msg.content.parts[0] : '';
  let data;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (action === 'create_textdoc' && typeof data?.content === 'string') {
    ctx.canvas = { title: String(data.name || 'Холст'), type: String(data.type || 'document'), content: data.content };
    return [canvasBlock(ctx.canvas, false)];
  }
  if (action === 'update_textdoc' && Array.isArray(data?.updates)) {
    if (!ctx.canvas) return [{ type: 'notice', level: 'warning', text: 'Правка холста без исходного документа; сохранены сами правки.' }, { type: 'code', language: 'json', code: JSON.stringify(data, null, 2) }];
    let text = ctx.canvas.content;
    let failed = 0;
    for (const u of data.updates) {
      try {
        const re = new RegExp(String(u.pattern), u.multiple ? 'g' : '');
        if (!re.test(text)) failed += 1;
        re.lastIndex = 0;
        // A function keeps "$&" and similar sequences in the replacement literal.
        text = text.replace(re, () => String(u.replacement ?? ''));
      } catch {
        failed += 1;
      }
    }
    ctx.canvas = { ...ctx.canvas, content: text };
    const blocks = [canvasBlock(ctx.canvas, true)];
    if (failed) blocks.unshift({ type: 'notice', level: 'warning', text: `Не удалось применить правок холста: ${failed}; показан результат остальных.` });
    return blocks;
  }
  if (action === 'comment_textdoc') return options.includeTools ? [{ type: 'tool', name: 'canvas: комментарии', input: truncate(raw, TOOL_TEXT_LIMIT) }] : [];
  return null;
}

/**
 * @param {{ title: string, type: string, content: string }} doc
 * @param {boolean} updated
 * @returns {import('../../core/model.js').Block}
 */
function canvasBlock(doc, updated) {
  const title = updated ? `${doc.title} (обновлено)` : doc.title;
  if (doc.type.startsWith('code/')) return { type: 'artifact', kind: 'canvas', title, language: doc.type.slice(5), content: doc.content };
  return { type: 'artifact', kind: 'canvas-document', title, language: 'markdown', content: doc.content };
}

/**
 * @param {any} msg
 * @param {string} role
 * @param {ChatGptNormalizeOptions} options
 * @param {{ canvas: { title: string, type: string, content: string } | null }} ctx
 * @returns {{ blocks: import('../../core/model.js').Block[], imagesFailed: number }}
 */
function convertContent(msg, role, options, ctx) {
  const content = msg.content || {};
  const type = content.content_type;
  /** @type {import('../../core/model.js').Block[]} */
  const blocks = [];
  let imagesFailed = 0;

  if (role === 'assistant' && typeof msg.recipient === 'string' && msg.recipient.startsWith('canmore.')) {
    const canvas = canvasBlocks(msg, ctx, options);
    if (canvas) return { blocks: canvas, imagesFailed: 0 };
  }

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

  // Reasoning models write hidden reasoning to the "analysis" channel; it is not the answer.
  if (role === 'assistant' && msg.channel === 'analysis' && type === 'text') {
    if (options.includeThinking) {
      const parts = Array.isArray(content.parts) ? content.parts.filter((p) => typeof p === 'string') : [];
      if (parts.join('').trim()) blocks.push({ type: 'thinking', text: parts.join('\n\n') });
    }
    return { blocks, imagesFailed };
  }

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
