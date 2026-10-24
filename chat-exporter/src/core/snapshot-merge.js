// Merging of DOM snapshots collected while scrolling (DOM fallback mode).
//
// Each snapshot is the ordered list of messages currently present in the DOM. Virtualized
// lists drop off-screen messages, so the full chat is reconstructed by aligning every new
// snapshot with what was collected so far. Keys are message ids when the page exposes them,
// otherwise role + text hash; alignment over sequences (not single keys) keeps repeated
// identical messages ("ok", "continue") apart. A snapshot that shares no item with the
// collected sequence is a GAP: something may have been skipped, so the export is incomplete.

/**
 * @template {{ key: string, text?: string }} T
 * @typedef {Object} MergeResult
 * @property {T[]} merged
 * @property {number} overlap Number of aligned items.
 * @property {boolean} gap True when the snapshot could not be aligned.
 */

/**
 * @template {{ key: string, text?: string }} T
 * @param {T[]} collected
 * @param {T[]} snapshot
 * @returns {MergeResult<T>}
 */
export function mergeSnapshot(collected, snapshot) {
  if (snapshot.length === 0) return { merged: collected.slice(), overlap: 0, gap: false };
  if (collected.length === 0) return { merged: snapshot.slice(), overlap: snapshot.length, gap: false };

  // Offset d: snapshot[i] aligns with collected[i + d].
  let bestD = Number.NaN;
  let bestOverlap = 0;
  for (let d = -(snapshot.length - 1); d <= collected.length - 1; d++) {
    const start = Math.max(0, -d);
    const end = Math.min(snapshot.length, collected.length - d);
    const overlap = end - start;
    if (overlap <= 0 || overlap < bestOverlap) continue;
    let ok = true;
    for (let i = start; i < end; i++) {
      if (collected[i + d].key !== snapshot[i].key) {
        ok = false;
        break;
      }
    }
    // ">=" prefers the later offset on ties: we scroll downwards.
    if (ok && overlap >= bestOverlap) {
      bestOverlap = overlap;
      bestD = d;
    }
  }

  if (Number.isNaN(bestD)) {
    return { merged: [...collected, ...snapshot], overlap: 0, gap: true };
  }

  const merged = collected.slice();
  // Refresh overlapping items: lazily rendered content may have grown.
  for (let i = Math.max(0, -bestD); i < Math.min(snapshot.length, collected.length - bestD); i++) {
    const old = merged[i + bestD];
    const next = snapshot[i];
    if ((next.text || '').length > (old.text || '').length) merged[i + bestD] = next;
  }
  const before = bestD < 0 ? snapshot.slice(0, -bestD) : [];
  const afterStart = collected.length - bestD;
  const after = afterStart < snapshot.length ? snapshot.slice(Math.max(0, afterStart)) : [];
  return { merged: [...before, ...merged, ...after], overlap: bestOverlap, gap: false };
}

/**
 * Fast non-cryptographic 32-bit FNV-1a hash (synchronous, for DOM keys).
 * @param {string} text
 */
export function fnv1a(text) {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16).padStart(8, '0');
}
