// Service registry: everything service-specific that the background worker needs.
// Adding a third service means adding one entry here plus a content-script client.
import { chatGptIdFromUrl, normalizeChatGpt, normalizeChatGptListItem } from './chatgpt/normalize.js';
import { claudeIdFromUrl, normalizeClaude, normalizeClaudeListItem } from './claude/normalize.js';

/**
 * @typedef {Object} ServiceDefinition
 * @property {import('../core/model.js').ServiceId} id
 * @property {string} label
 * @property {string} homeUrl Opened in a background tab when no tab of the service exists.
 * @property {string[]} tabPatterns For chrome.tabs.query({ url }).
 * @property {(url: string) => string | null} idFromUrl
 * @property {(id: string) => string} chatUrl
 * @property {(raw: any, options: any) => import('../core/model.js').NormalizedChat} normalize
 * @property {(item: any) => import('../core/model.js').ChatListItem | null} normalizeListItem
 */

/** @type {Record<import('../core/model.js').ServiceId, ServiceDefinition>} */
export const SERVICES = {
  chatgpt: {
    id: 'chatgpt',
    label: 'ChatGPT',
    homeUrl: 'https://chatgpt.com/',
    tabPatterns: ['https://chatgpt.com/*', 'https://chat.openai.com/*'],
    idFromUrl: chatGptIdFromUrl,
    chatUrl: (id) => `https://chatgpt.com/c/${id}`,
    normalize: normalizeChatGpt,
    normalizeListItem: normalizeChatGptListItem,
  },
  claude: {
    id: 'claude',
    label: 'Claude',
    homeUrl: 'https://claude.ai/',
    tabPatterns: ['https://claude.ai/*'],
    idFromUrl: claudeIdFromUrl,
    chatUrl: (id) => `https://claude.ai/chat/${id}`,
    normalize: normalizeClaude,
    normalizeListItem: normalizeClaudeListItem,
  },
};

/**
 * Detects the service of a URL.
 * @param {string | undefined} url
 * @returns {import('../core/model.js').ServiceId | null}
 */
export function serviceFromUrl(url) {
  if (!url) return null;
  try {
    const host = new URL(url).hostname;
    if (host === 'chatgpt.com' || host === 'chat.openai.com') return 'chatgpt';
    if (host === 'claude.ai') return 'claude';
  } catch {
    // not a URL
  }
  return null;
}

/** Stable key for storage maps. @param {string} service @param {string} id */
export function chatKey(service, id) {
  return `${service}:${id}`;
}
