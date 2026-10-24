// ChatGPT data client (runs in a chatgpt.com tab).
//
// Uses the endpoints the ChatGPT web app itself calls. They are internal and undocumented;
// paths and headers follow the open-source pionxzh/chatgpt-exporter (src/api.ts):
//   GET /api/auth/session                       -> { accessToken } for the signed-in session
//   GET /backend-api/conversations?offset&limit -> chat list (newest updated first)
//   GET /backend-api/conversation/{id}          -> full conversation tree
//   GET /backend-api/files/download/{fileId}    -> signed URL of an image
//   GET /backend-api/gizmos/snorlax/sidebar     -> projects (their chats are not in the main list)
import { collectChatGptImagePointers } from '../adapters/chatgpt/normalize.js';
import { ErrorCode, ExportError, sleep } from '../core/util.js';
import { fetchImageAsDataUrl, getCookie, requestJson } from './http.js';

/** @type {{ token: string, at: number } | null} */
let session = null;
/** @type {string | null | undefined} */
let accountId;

async function getAccessToken(refresh = false) {
  if (!refresh && session && Date.now() - session.at < 10 * 60_000) return session.token;
  const data = await requestJson('/api/auth/session', { what: 'сессия ChatGPT' });
  if (!data || typeof data.accessToken !== 'string' || !data.accessToken) {
    throw new ExportError(ErrorCode.NOT_AUTHENTICATED, 'ChatGPT: вы не вошли в аккаунт в этой вкладке');
  }
  session = { token: data.accessToken, at: Date.now() };
  return session.token;
}

/** Workspace (Team) accounts need the account id header; personal accounts do not. */
async function getAccountId() {
  if (accountId !== undefined) return accountId;
  accountId = null;
  const workspace = getCookie('_account');
  if (!workspace) return accountId;
  try {
    const check = await api('/backend-api/accounts/check/v4-2023-04-27', 'аккаунт', false);
    accountId = check?.accounts?.[workspace]?.account?.account_id || null;
  } catch {
    accountId = null;
  }
  return accountId;
}

/**
 * @param {string} path
 * @param {string} what
 * @param {boolean} [withAccount]
 */
async function api(path, what, withAccount = true) {
  const call = async (/** @type {boolean} */ refresh) => {
    const token = await getAccessToken(refresh);
    /** @type {Record<string, string>} */
    const headers = { Authorization: `Bearer ${token}` };
    const acc = withAccount ? await getAccountId() : null;
    if (acc) headers['ChatGPT-Account-Id'] = acc;
    return requestJson(path, { what, headers });
  };
  try {
    return await call(false);
  } catch (err) {
    // The token may have expired: refresh once.
    if (err instanceof ExportError && err.code === ErrorCode.NOT_AUTHENTICATED) return call(true);
    throw err;
  }
}

/**
 * @param {{ stopBefore?: string | null, maxPages?: number, includeProjects?: boolean }} options
 */
export async function listChats(options) {
  const limit = 100;
  const maxPages = options.maxPages || 50;
  const stopMs = options.stopBefore ? Date.parse(options.stopBefore) : null;
  /** @type {any[]} */
  const items = [];
  /** @type {string[]} */
  const warnings = [];
  let complete = false;
  for (let page = 0; page < maxPages; page++) {
    const data = await api(`/backend-api/conversations?offset=${page * limit}&limit=${limit}&order=updated`, 'список чатов ChatGPT');
    const batch = Array.isArray(data?.items) ? data.items : [];
    items.push(...batch);
    if (batch.length < limit || (typeof data.total === 'number' && (page + 1) * limit >= data.total)) {
      complete = true;
      break;
    }
    if (stopMs) {
      const last = batch[batch.length - 1];
      const t = typeof last?.update_time === 'number' ? last.update_time * 1000 : Date.parse(last?.update_time);
      if (t && t < stopMs) break; // older chats did not change since the previous run
    }
    await sleep(400);
  }
  if (!complete && !stopMs) warnings.push(`список чатов обрезан на ${items.length} (лимит страниц)`);

  if (options.includeProjects) {
    try {
      items.push(...await listProjectChats(stopMs));
    } catch (err) {
      warnings.push(`чаты из проектов не получены: ${err instanceof Error ? err.message : err}`);
      complete = false;
    }
  }
  return { items, complete, warnings };
}

/** @param {number | null} stopMs */
async function listProjectChats(stopMs) {
  /** @type {any[]} */
  const out = [];
  /** @type {string | number | null} */
  let cursor = null;
  /** @type {string[]} */
  const projects = [];
  for (let i = 0; i < 20; i++) {
    const data = await api(`/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0${cursor !== null ? `&cursor=${encodeURIComponent(String(cursor))}` : ''}`, 'проекты ChatGPT');
    for (const it of Array.isArray(data?.items) ? data.items : []) {
      const id = it?.gizmo?.gizmo?.id;
      if (id) projects.push(id);
    }
    cursor = data?.cursor ?? null;
    if (cursor === null) break;
  }
  for (const gizmo of projects) {
    let c = '0';
    for (let page = 0; page < 20; page++) {
      const data = await api(`/backend-api/gizmos/${encodeURIComponent(gizmo)}/conversations?cursor=${encodeURIComponent(c)}&limit=50`, 'чаты проекта');
      const batch = Array.isArray(data?.items) ? data.items : [];
      out.push(...batch.map((/** @type {any} */ x) => ({ ...x, gizmo_id: x.gizmo_id || gizmo })));
      if (!data?.cursor || batch.length === 0) break;
      const last = batch[batch.length - 1];
      const t = typeof last?.update_time === 'number' ? last.update_time * 1000 : Date.parse(last?.update_time);
      if (stopMs && t && t < stopMs) break;
      c = String(data.cursor);
      await sleep(300);
    }
  }
  return out;
}

/**
 * @param {string} id
 * @param {{ includeImages: boolean, maxImageBytes: number }} options
 */
export async function fetchChat(id, options) {
  const raw = await api(`/backend-api/conversation/${encodeURIComponent(id)}`, 'переписка ChatGPT');
  /** @type {Record<string, string>} */
  const images = {};
  /** @type {Record<string, string>} */
  const imageErrors = {};
  if (options.includeImages) {
    for (const pointer of collectChatGptImagePointers(raw)) {
      try {
        images[pointer] = await resolveImage(pointer, options.maxImageBytes);
      } catch (err) {
        imageErrors[pointer] = `Изображение не загружено: ${err instanceof Error ? err.message : err}`;
      }
    }
  } else {
    for (const pointer of collectChatGptImagePointers(raw)) imageErrors[pointer] = 'Встраивание изображений отключено в настройках.';
  }
  return { raw, images, imageErrors };
}

/**
 * @param {string} pointer "sediment://file_x" or "file-service://file-x"
 * @param {number} maxBytes
 */
async function resolveImage(pointer, maxBytes) {
  const fileId = pointer.replace(/^[a-z-]+:\/\//, '');
  const info = await api(`/backend-api/files/download/${encodeURIComponent(fileId)}?post_id=&inline=false`, 'ссылка на изображение');
  if (!info || info.status === 'error' || typeof info.download_url !== 'string') {
    throw new Error(info?.error_message || info?.error_code || 'нет ссылки на файл');
  }
  // Signed URL; may be cross-origin (CORS decides whether the tab may read it).
  return fetchImageAsDataUrl(info.download_url, maxBytes);
}
