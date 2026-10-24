// Hashing helpers: canonical JSON + SHA-256 via Web Crypto (Chrome and Node >= 19).

/**
 * JSON with sorted object keys, so equal data always yields the same string.
 * @param {unknown} value
 * @returns {string}
 */
export function canonicalJson(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value) ?? 'null';
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  const obj = /** @type {Record<string, unknown>} */ (value);
  const keys = Object.keys(obj).filter((k) => obj[k] !== undefined).sort();
  return `{${keys.map((k) => `${JSON.stringify(k)}:${canonicalJson(obj[k])}`).join(',')}}`;
}

/**
 * @param {string | Uint8Array} data
 * @returns {Promise<string>} lowercase hex
 */
export async function sha256Hex(data) {
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : data;
  const digest = await crypto.subtle.digest('SHA-256', /** @type {BufferSource} */ (bytes));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Hash of the conversation content that matters for "has this chat changed?".
 * Volatile data is excluded: extraction time, signed image URLs, embedded bytes
 * (depend on settings), completeness report.
 * @param {import('./model.js').NormalizedChat} chat
 */
export async function chatContentHash(chat) {
  const stable = {
    title: chat.title,
    messages: chat.messages.map((m) => ({
      id: m.id,
      role: m.role,
      createdAt: m.createdAt,
      blocks: m.blocks.map((b) => {
        if (b.type === 'image') return { type: 'image', origin: b.origin, alt: b.alt || '' };
        return b;
      }),
    })),
  };
  return sha256Hex(canonicalJson(stable));
}
