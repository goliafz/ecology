// Claude data client (runs in a claude.ai tab).
//
// Uses the endpoints the claude.ai web app itself calls. They are internal and undocumented;
// paths follow open-source exporters (agarwalvishal/claude-chat-exporter, unofficial-claude-api):
//   GET /api/organizations                                   -> organizations of the account
//   GET /api/organizations/{org}/chat_conversations          -> chat list
//   GET /api/organizations/{org}/chat_conversations/{id}?tree=True&rendering_mode=messages&render_all_tools=true
// The organization id is taken from the lastActiveOrg cookie when present.
import { collectClaudeImageUrls } from '../adapters/claude/normalize.js';
import { ErrorCode, ExportError, sleep } from '../core/util.js';
import { fetchImageAsDataUrl, getCookie, requestJson } from './http.js';

/** @type {string | null} */
let orgId = null;

async function getOrgId() {
  if (orgId) return orgId;
  const cookie = getCookie('lastActiveOrg');
  if (cookie && /^[0-9a-f-]{36}$/i.test(cookie)) {
    orgId = cookie;
    return orgId;
  }
  const orgs = await requestJson('/api/organizations', { what: 'организации Claude' });
  const list = Array.isArray(orgs) ? orgs : Array.isArray(orgs?.organizations) ? orgs.organizations : [];
  const chatOrg = list.find((o) => Array.isArray(o?.capabilities) && o.capabilities.includes('chat')) || list[0];
  if (!chatOrg?.uuid) throw new ExportError(ErrorCode.NOT_AUTHENTICATED, 'Claude: не найдена организация — войдите в аккаунт в этой вкладке');
  orgId = String(chatOrg.uuid);
  return orgId;
}

/**
 * The list endpoint historically returned all chats at once; newer versions may page.
 * Both are handled: paging stops on a short page or when a page brings nothing new.
 * @param {{ maxPages?: number }} options
 */
export async function listChats(options) {
  const org = await getOrgId();
  const limit = 100;
  const maxPages = options.maxPages || 50;
  /** @type {Map<string, any>} */
  const seen = new Map();
  let complete = false;
  for (let page = 0; page < maxPages; page++) {
    const data = await requestJson(`/api/organizations/${org}/chat_conversations?limit=${limit}&offset=${page * limit}`, { what: 'список чатов Claude' });
    const batch = Array.isArray(data) ? data : Array.isArray(data?.data) ? data.data : [];
    let fresh = 0;
    for (const it of batch) {
      if (it?.uuid && !seen.has(it.uuid)) {
        seen.set(it.uuid, it);
        fresh += 1;
      }
    }
    if (batch.length < limit || fresh === 0) {
      complete = true;
      break;
    }
    await sleep(400);
  }
  const warnings = complete ? [] : [`список чатов обрезан на ${seen.size} (лимит страниц)`];
  return { items: [...seen.values()], complete, warnings };
}

/**
 * @param {string} id
 * @param {{ includeImages: boolean, maxImageBytes: number }} options
 */
export async function fetchChat(id, options) {
  const org = await getOrgId();
  const raw = await requestJson(
    `/api/organizations/${org}/chat_conversations/${encodeURIComponent(id)}?tree=True&rendering_mode=messages&render_all_tools=true`,
    { what: 'переписка Claude', timeoutMs: 120_000 },
  );
  /** @type {Record<string, string>} */
  const images = {};
  /** @type {Record<string, string>} */
  const imageErrors = {};
  for (const url of collectClaudeImageUrls(raw)) {
    if (!options.includeImages) {
      imageErrors[url] = 'Встраивание изображений отключено в настройках.';
      continue;
    }
    try {
      images[url] = await fetchImageAsDataUrl(url, options.maxImageBytes);
    } catch (err) {
      imageErrors[url] = `Изображение не загружено: ${err instanceof Error ? err.message : err}`;
    }
  }
  return { raw, images, imageErrors };
}
