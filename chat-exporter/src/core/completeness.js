// Completeness verification. Distinguishes a full export from a document with missing parts.
// "fail" checks make the export INCOMPLETE (auto mode will retry it later and the UI warns);
// "warn" checks keep the text complete but flag degraded content (e.g. images not embedded).

/**
 * @typedef {Object} CompletenessContext
 * @property {string | null} [expectedUpdatedAt] update time reported by the chat list.
 */

/**
 * @param {import('./model.js').NormalizedChat} chat
 * @param {CompletenessContext} [ctx]
 * @returns {import('./model.js').CompletenessReport}
 */
export function evaluateCompleteness(chat, ctx = {}) {
  /** @type {import('./model.js').CompletenessCheck[]} */
  const checks = [];
  const add = (/** @type {string} */ id, /** @type {'pass'|'warn'|'fail'} */ result, /** @type {string} */ message) => checks.push({ id, result, message });
  const integ = chat.integrity;

  // 1. Something was extracted at all.
  if (!Array.isArray(chat.messages)) {
    add('structure', 'fail', 'Нет списка сообщений.');
  } else if (chat.messages.length === 0) {
    add('structure', integ.pathLength > 1 ? 'fail' : 'warn',
      integ.pathLength > 1 ? 'Сообщения есть в источнике, но ни одно не удалось извлечь.' : 'Чат пуст.');
  } else {
    add('structure', 'pass', `Извлечено сообщений: ${chat.messages.length}.`);
  }

  // 2. The parent chain of the exported branch is unbroken (API) / snapshots overlap (DOM).
  if (integ.brokenLinks.length > 0) {
    add('chain', 'fail', `Цепочка сообщений разорвана: не найдено звеньев ${integ.brokenLinks.length} (${integ.brokenLinks.slice(0, 3).join(', ')}).`);
  } else if (chat.source === 'api') {
    add('chain', 'pass', `Цепочка от первого до последнего сообщения непрерывна (${integ.pathLength} узлов).`);
  }

  // 3. No duplicate message ids.
  const ids = new Set();
  const dups = [];
  for (const m of chat.messages || []) {
    if (ids.has(m.id)) dups.push(m.id);
    ids.add(m.id);
  }
  add('unique', dups.length ? 'fail' : 'pass', dups.length ? `Дублирующиеся сообщения: ${dups.length}.` : 'Дубликатов нет.');

  // 4. Answer still being generated.
  if (integ.generating) add('generating', 'fail', 'Ответ ещё генерируется — последнее сообщение может быть неполным.');

  // 5. Timestamps do not go backwards (sanity check of the ordering).
  let backwards = 0;
  let prevTs = '';
  for (const m of chat.messages || []) {
    if (m.createdAt) {
      if (prevTs && m.createdAt < prevTs) backwards += 1;
      prevTs = m.createdAt;
    }
  }
  if (backwards > 0) add('order', 'warn', `Время сообщений идёт не по порядку в ${backwards} местах (проверьте порядок вручную).`);

  // 6. The chat list says the chat is newer than what we received.
  if (ctx.expectedUpdatedAt && chat.updatedAt && Date.parse(ctx.expectedUpdatedAt) - Date.parse(chat.updatedAt) > 5_000) {
    add('freshness', 'warn', 'Список чатов сообщает более позднее время изменения, чем полученная переписка: возможно, сервис ещё не сохранил последние сообщения.');
  }

  // 7. DOM cross-check (API mode with an open tab): every message visible on the page must be in the result.
  if (integ.crossCheckMissing && integ.crossCheckMissing.length > 0) {
    add('dom-cross-check', 'fail', `На странице видны сообщения, которых нет в выгрузке: ${integ.crossCheckMissing.length}.`);
  } else if (integ.crossCheckMissing) {
    add('dom-cross-check', 'pass', 'Все сообщения, видимые на странице, есть в выгрузке.');
  }

  // 8. DOM fallback specifics.
  if (chat.source === 'dom') {
    add('dom-source', 'warn', 'Переписка извлечена из страницы (резервный режим): форматирование восстановлено приближённо, время сообщений может отсутствовать.');
    if (integ.reachedTop === false) add('dom-top', 'fail', 'Не удалось долистать до начала переписки.');
    if (integ.reachedBottom === false) add('dom-bottom', 'fail', 'Не удалось долистать до конца переписки.');
    if ((integ.snapshotGaps || 0) > 0) add('dom-gaps', 'fail', `При прокрутке обнаружены разрывы между фрагментами: ${integ.snapshotGaps}.`);
  }

  // 9. Degraded content.
  if ((integ.imagesFailed || 0) > 0) add('images', 'warn', `Не встроено изображений: ${integ.imagesFailed}.`);
  const truncated = (chat.messages || []).filter((m) => m.meta?.truncated).length;
  if (truncated) add('truncated', 'warn', `Сообщений с флагом truncated: ${truncated}.`);
  const artifactIssues = chat.notes.filter((n) => n.includes('правку не удалось применить')).length;
  if (artifactIssues) add('artifacts', 'warn', `Артефактов, восстановленных не полностью: ${artifactIssues}.`);
  const interrupted = (chat.messages || []).filter((m) => m.meta?.interrupted).length;
  if (interrupted) add('interrupted', 'pass', `Ответов, остановленных пользователем: ${interrupted} (это не ошибка экспорта).`);

  const hasFail = checks.some((c) => c.result === 'fail');
  const hasWarn = checks.some((c) => c.result === 'warn');
  return {
    status: hasFail ? 'incomplete' : hasWarn ? 'complete_with_warnings' : 'complete',
    checks,
    warnings: checks.filter((c) => c.result !== 'pass').map((c) => c.message),
  };
}

export const STATUS_LABELS = /** @type {const} */ ({
  complete: 'Полный',
  complete_with_warnings: 'Полный, с предупреждениями',
  incomplete: 'НЕПОЛНЫЙ',
});
