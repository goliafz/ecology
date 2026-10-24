// Pure change detection: which chats need a backup, given the stored index and a fresh chat list.

/**
 * @typedef {import('../background/storage.js').ChatIndexEntry} ChatIndexEntry
 * @typedef {import('./model.js').ChatListItem} ChatListItem
 */

/**
 * @typedef {Object} DetectOptions
 * @property {number} now
 * @property {number | null} baselineAt chats changed after this moment are new work
 * @property {boolean} backfill also back up chats never exported that changed before baseline
 * @property {Record<string, unknown>} excluded chatKey -> anything
 * @property {number} maxAttempts failures after which a chat waits for a new change
 * @property {number} maxChats
 * @property {boolean} listComplete the list covers every chat (enables deletion detection)
 * @property {import('./model.js').ServiceId} service
 */

/**
 * @typedef {Object} Candidate
 * @property {import('./model.js').ServiceId} service
 * @property {string} id
 * @property {string} title
 * @property {string | null} updatedAt
 * @property {'new' | 'changed' | 'activity' | 'retry' | 'backfill' | 'incomplete'} reason
 */

const ms = (/** @type {string | null | undefined} */ iso) => (iso ? Date.parse(iso) || 0 : 0);

/**
 * Merges the list into the index (titles, update times, missing chats) and selects candidates.
 * Returns a NEW index object; the input is not modified.
 * @param {Record<string, ChatIndexEntry>} index
 * @param {ChatListItem[]} items
 * @param {DetectOptions} opt
 * @returns {{ index: Record<string, ChatIndexEntry>, candidates: Candidate[], stats: { listed: number, missing: number, excluded: number } }}
 */
export function detectChanges(index, items, opt) {
  const next = { ...index };
  /** @type {Candidate[]} */
  const priority = [];
  /** @type {Candidate[]} */
  const backfill = [];
  const listed = new Set();
  let excluded = 0;

  for (const item of items) {
    const key = `${item.service}:${item.id}`;
    listed.add(key);
    const prev = next[key];
    /** @type {ChatIndexEntry} */
    const entry = {
      ...(prev || { service: item.service, id: item.id }),
      title: item.title,
      updatedAt: item.updatedAt || prev?.updatedAt || null,
      seenAt: opt.now,
      missingSince: null,
      archived: item.archived || false,
    };
    next[key] = entry;
    if (opt.excluded[key]) {
      excluded += 1;
      continue;
    }
    const c = classify(entry, opt);
    if (!c) continue;
    const cand = { service: item.service, id: item.id, title: item.title, updatedAt: entry.updatedAt, reason: c };
    (c === 'backfill' ? backfill : priority).push(cand);
  }

  // Chats marked by activity tracking but not present in this (possibly partial) list.
  for (const [key, entry] of Object.entries(next)) {
    if (entry.service !== opt.service || listed.has(key) || opt.excluded[key]) continue;
    if (entry.dirty && !entry.missingSince && (entry.failures || 0) < opt.maxAttempts) {
      priority.push({ service: entry.service, id: entry.id, title: entry.title, updatedAt: entry.updatedAt, reason: 'activity' });
    }
  }

  let missing = 0;
  if (opt.listComplete) {
    for (const [key, entry] of Object.entries(next)) {
      if (entry.service !== opt.service || listed.has(key)) continue;
      // Deleted or archived: keep the backup files, only remember that the chat disappeared.
      if (!entry.missingSince) next[key] = { ...entry, missingSince: opt.now, dirty: false };
      missing += 1;
    }
  }

  const byTime = (/** @type {Candidate} */ a, /** @type {Candidate} */ b) => ms(b.updatedAt) - ms(a.updatedAt);
  priority.sort(byTime);
  backfill.sort(byTime);
  const candidates = [...priority, ...backfill].slice(0, opt.maxChats);
  return { index: next, candidates, stats: { listed: items.length, missing, excluded } };
}

/**
 * @param {ChatIndexEntry} entry
 * @param {DetectOptions} opt
 * @returns {Candidate['reason'] | null}
 */
export function classify(entry, opt) {
  const last = entry.lastExport;
  const failures = entry.failures || 0;
  if (failures > 0) {
    if (failures >= opt.maxAttempts) {
      // Gave up until the chat changes again after the failure.
      if (!(entry.lastError && ms(entry.updatedAt) > entry.lastError.at)) return null;
    } else if (entry.nextRetryAt && entry.nextRetryAt > opt.now) {
      return null;
    } else {
      return 'retry';
    }
  }
  if (entry.dirty) return 'activity';
  if (last) {
    if (last.status === 'incomplete') return 'incomplete';
    if (ms(entry.updatedAt) > ms(last.updatedAt)) return 'changed';
    return null;
  }
  const updated = ms(entry.updatedAt);
  if (opt.baselineAt && updated >= opt.baselineAt) return 'new';
  if (!opt.baselineAt && updated) return 'new';
  return opt.backfill ? 'backfill' : null;
}
