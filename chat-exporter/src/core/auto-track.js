// Rule for marking chats automatically: a title written in capital letters only ("ЧАТ",
// "ДЕДУШКА", "PLAN 2026") marks the chat "Сохранять".

/**
 * True when the title has at least two letters and none of them is lowercase.
 * Digits, spaces and punctuation are allowed; scripts without letter case do not count.
 * @param {string | null | undefined} title
 */
export function isUppercaseTitle(title) {
  const t = String(title || '').trim();
  if (!t) return false;
  const upper = t.match(/\p{Lu}/gu)?.length || 0;
  const lower = t.match(/\p{Ll}/gu)?.length || 0;
  return upper >= 2 && lower === 0;
}

/**
 * Automatic marks that no longer match the rule (wrongly added or the chat was renamed).
 * Manual marks are never touched.
 * @param {Record<string, { auto?: boolean }>} tracked
 * @param {Record<string, string>} titles chatKey -> current title
 * @returns {string[]} keys to unmark
 */
export function staleAutoMarks(tracked, titles) {
  return Object.entries(tracked)
    .filter(([key, t]) => t.auto && typeof titles[key] === 'string' && titles[key].trim() && !isUppercaseTitle(titles[key]))
    .map(([key]) => key);
}
