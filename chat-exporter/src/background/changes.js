// Listing chats through the content script and turning the list into backup candidates.
import { SERVICES } from '../adapters/registry.js';
import { detectChanges } from '../core/change-detection.js';
import { addLog } from './log.js';
import { getIndex, mutateIndex, setKey, getKey } from './storage.js';
import { getServiceTab, sendToTab, withTabLease } from './tabs.js';

/**
 * Lists chats of a service.
 * @param {import('../core/model.js').ServiceId} service
 * @param {import('./settings.js').Settings} settings
 * @param {{ allowOpen: boolean, stopBefore?: string | null, maxPages?: number }} options
 * @returns {Promise<{ items: import('../core/model.js').ChatListItem[], complete: boolean, warnings: string[] }>}
 */
export async function listChats(service, settings, options) {
  const res = await withTabLease(async () => {
    const tabId = await getServiceTab(service, { allowOpen: options.allowOpen });
    return sendToTab(tabId, {
      cmd: 'listChats',
      stopBefore: options.stopBefore || null,
      maxPages: options.maxPages || 50,
      includeProjects: settings.includeChatGptProjects,
    }, 4 * 60_000);
  });
  const items = (Array.isArray(res.items) ? res.items : [])
    .map((raw) => SERVICES[service].normalizeListItem(raw))
    .filter(Boolean);
  // Deduplicate (projects and the main list may overlap).
  /** @type {Map<string, import('../core/model.js').ChatListItem>} */
  const unique = new Map();
  for (const it of /** @type {import('../core/model.js').ChatListItem[]} */ (items)) unique.set(it.id, it);
  const list = [...unique.values()];
  await setKey(`listCache:${service}`, { items: list, fetchedAt: Date.now(), complete: Boolean(res.complete) });
  return { items: list, complete: Boolean(res.complete), warnings: Array.isArray(res.warnings) ? res.warnings : [] };
}

/** @param {import('../core/model.js').ServiceId} service */
export async function getCachedList(service) {
  return (await getKey(`listCache:${service}`)) || null;
}

/**
 * Finds chats that need a backup for one service and updates the index.
 * @param {import('../core/model.js').ServiceId} service
 * @param {import('./settings.js').Settings} settings
 * @param {{ lastRunStartedAt?: number | null }} schedule
 */
export async function findCandidates(service, settings, schedule) {
  const now = Date.now();
  // ChatGPT lists chats newest-updated first, so an incremental listing can stop early:
  // everything older than the previous run (minus a safety day) cannot have changed since.
  const cutoffMs = schedule.lastRunStartedAt ? schedule.lastRunStartedAt - 24 * 3600_000 : null;
  const baselineAt = settings.auto.baselineAt ?? now - 24 * 3600_000;
  const stopBefore = service === 'chatgpt' && cutoffMs && !settings.auto.backfill
    ? new Date(Math.min(cutoffMs, baselineAt)).toISOString()
    : null;
  const listing = await listChats(service, settings, { allowOpen: settings.auto.openTabs, stopBefore });
  for (const w of listing.warnings) await addLog('warn', `${SERVICES[service].label}: ${w}`);

  const index = await getIndex();
  const result = detectChanges(index, listing.items, {
    now,
    service,
    baselineAt,
    backfill: settings.auto.backfill,
    excluded: settings.excluded,
    maxAttempts: settings.auto.maxAttempts,
    maxChats: settings.auto.maxChatsPerRun,
    listComplete: listing.complete && !stopBefore,
  });
  // Only list-derived fields are merged: an export may have updated lastExport meanwhile.
  await mutateIndex((idx) => {
    for (const [k, v] of Object.entries(result.index)) {
      if (v.service !== service) continue;
      const cur = idx[k] || { service: v.service, id: v.id, title: v.title, updatedAt: null };
      idx[k] = {
        ...cur,
        title: v.title,
        updatedAt: v.updatedAt,
        seenAt: v.seenAt,
        missingSince: v.missingSince,
        archived: v.archived,
        dirty: v.missingSince ? false : cur.dirty,
      };
    }
  });
  if (result.stats.missing > 0) {
    await addLog('info', `${SERVICES[service].label}: ${result.stats.missing} чат(ов) больше нет в списке (удалены или архивированы); резервные копии сохранены`);
  }
  return { candidates: result.candidates, stats: result.stats };
}
