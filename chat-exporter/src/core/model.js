// Normalized data model shared by adapters, renderers and the background worker.
// Every adapter (API or DOM based) must produce a NormalizedChat; renderers and
// the completeness checker only ever see this shape.

export const SCHEMA_VERSION = 1;

/** @typedef {'chatgpt' | 'claude'} ServiceId */
/** @typedef {'md' | 'html' | 'pdf'} ExportFormat */
/** @typedef {'user' | 'assistant' | 'system' | 'tool'} Role */
/** @typedef {'api' | 'dom'} SourceKind */

/**
 * @typedef {Object} MarkdownBlock
 * @property {'markdown'} type
 * @property {string} text Source Markdown, exactly as the service stores it.
 */
/**
 * @typedef {Object} CodeBlock
 * @property {'code'} type
 * @property {string} language
 * @property {string} code
 * @property {string} [title]
 */
/**
 * @typedef {Object} ImageBlock
 * @property {'image'} type
 * @property {string} [src] data: URL when embedded, otherwise empty.
 * @property {string} [remoteUrl] Original URL (not loaded by exported HTML).
 * @property {string} [alt]
 * @property {'user-upload' | 'generated' | 'tool-output' | 'unknown'} origin
 * @property {boolean} embedded True when `src` holds the image bytes.
 * @property {string} [note] Why the image was not embedded.
 */
/**
 * @typedef {Object} AttachmentBlock
 * @property {'attachment'} type
 * @property {string} name
 * @property {string} [mimeType]
 * @property {number} [size]
 * @property {number} [pageCount]
 * @property {string} [extractedText] Text the service extracted from the file.
 * @property {string} note Explains what was (not) saved: the original file is never claimed.
 */
/**
 * @typedef {Object} ArtifactBlock
 * @property {'artifact'} type
 * @property {string} kind  artifact | file | widget
 * @property {string} title
 * @property {string} language
 * @property {string} content
 */
/**
 * @typedef {Object} ThinkingBlock
 * @property {'thinking'} type
 * @property {string} text
 */
/**
 * @typedef {Object} ToolBlock
 * @property {'tool'} type
 * @property {string} name
 * @property {string} [input]
 * @property {string} [output]
 */
/**
 * @typedef {Object} NoticeBlock
 * @property {'notice'} type
 * @property {'info' | 'warning' | 'error'} level
 * @property {string} text
 */
/** @typedef {MarkdownBlock | CodeBlock | ImageBlock | AttachmentBlock | ArtifactBlock | ThinkingBlock | ToolBlock | NoticeBlock} Block */

/**
 * @typedef {Object} NormalizedMessage
 * @property {string} id Stable id from the service (API) or a synthetic key (DOM).
 * @property {string | null} parentId
 * @property {Role} role
 * @property {string | null} createdAt ISO timestamp if known.
 * @property {string | null} model
 * @property {Block[]} blocks
 * @property {{ branchIndex?: number, branchCount?: number, status?: string, interrupted?: boolean, truncated?: boolean, sourceIds?: string[] }} meta
 */

/**
 * @typedef {Object} CompletenessCheck
 * @property {string} id
 * @property {'pass' | 'warn' | 'fail'} result
 * @property {string} message
 */

/**
 * @typedef {'complete' | 'complete_with_warnings' | 'incomplete'} CompletenessStatus
 */

/**
 * @typedef {Object} CompletenessReport
 * @property {CompletenessStatus} status
 * @property {CompletenessCheck[]} checks
 * @property {string[]} warnings Human readable, already localized.
 */

/**
 * @typedef {Object} IntegrityInfo
 * @property {number} totalNodes Number of nodes/messages the source returned (all branches).
 * @property {number} pathLength Number of nodes on the exported (current) branch, hidden ones included.
 * @property {string[]} brokenLinks Parent ids that were referenced but missing.
 * @property {string | null} leafId
 * @property {boolean} generating True when the service reports an unfinished answer.
 * @property {number} [domMessageCount] DOM fallback: messages collected.
 * @property {number} [snapshotGaps] DOM fallback: snapshots that did not overlap.
 * @property {boolean} [reachedTop] DOM fallback: scroll reached the top of the list.
 * @property {boolean} [reachedBottom]
 * @property {number} [imagesFailed]
 * @property {string[]} [crossCheckMissing] Ids visible in the DOM but absent from the API result.
 * @property {string[]} [pathIds] Message ids on the current branch (hidden ones included), for the DOM cross-check.
 */

/**
 * @typedef {Object} NormalizedChat
 * @property {number} schemaVersion
 * @property {ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {string} url
 * @property {string | null} createdAt
 * @property {string | null} updatedAt
 * @property {string | null} model
 * @property {SourceKind} source
 * @property {string} extractedAt
 * @property {NormalizedMessage[]} messages Visible messages of the current branch, in order.
 * @property {IntegrityInfo} integrity
 * @property {string[]} notes Adapter notes (e.g. alternative branches not exported).
 * @property {CompletenessReport} [completeness]
 */

/**
 * @typedef {Object} ChatListItem
 * @property {ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {string | null} updatedAt ISO
 * @property {string | null} createdAt ISO
 * @property {string} [project]
 * @property {boolean} [archived]
 */

export const SERVICE_LABELS = /** @type {const} */ ({
  chatgpt: 'ChatGPT',
  claude: 'Claude',
});

export const ROLE_LABELS = /** @type {const} */ ({
  user: 'Пользователь',
  system: 'Система',
  tool: 'Инструмент',
});

/**
 * @param {ServiceId} service
 * @param {Role} role
 */
export function roleLabel(service, role) {
  if (role === 'assistant') return SERVICE_LABELS[service] || 'Ассистент';
  return ROLE_LABELS[role] || role;
}

/**
 * Converts seconds-since-epoch numbers, ms numbers and ISO strings to ISO strings.
 * @param {unknown} value
 * @returns {string | null}
 */
export function toIso(value) {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value === 'number' && Number.isFinite(value)) {
    // Values below 1e11 are seconds (ChatGPT uses float seconds).
    const ms = value < 1e11 ? value * 1000 : value;
    const d = new Date(ms);
    return Number.isNaN(d.getTime()) ? null : d.toISOString();
  }
  if (typeof value === 'string') {
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? null : d.toISOString();
  }
  return null;
}
