#!/usr/bin/env node
// Verifies backups on disk against their *.meta.json files (SHA-256 and size of every file).
// The extension cannot read files back from disk (chrome.downloads only writes), so this
// check is done by a separate local script.
//
// Usage: node tools/verify-backups.mjs ~/Downloads/ChatBackups
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';

/** @param {string} dir @returns {string[]} */
function findMeta(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) out.push(...findMeta(p));
    else if (name.endsWith('.meta.json')) out.push(p);
  }
  return out;
}

/**
 * @param {string} root
 * @returns {{ ok: number, problems: string[], incomplete: string[] }}
 */
export function verifyBackups(root) {
  const problems = [];
  const incomplete = [];
  let ok = 0;
  for (const metaPath of findMeta(root)) {
    let meta;
    try {
      meta = JSON.parse(readFileSync(metaPath, 'utf8'));
    } catch (err) {
      problems.push(`${metaPath}: повреждён meta.json (${err instanceof Error ? err.message : err})`);
      continue;
    }
    if (meta.completeness?.status === 'incomplete') incomplete.push(`${meta.title} (${metaPath})`);
    for (const [format, info] of Object.entries(meta.files || {})) {
      const file = join(dirname(metaPath), /** @type {any} */ (info).file);
      if (!existsSync(file)) {
        problems.push(`${file}: файл ${format} отсутствует`);
        continue;
      }
      const data = readFileSync(file);
      const sha = createHash('sha256').update(data).digest('hex');
      if (data.length !== /** @type {any} */ (info).bytes) problems.push(`${file}: размер ${data.length}, ожидался ${/** @type {any} */ (info).bytes}`);
      else if (sha !== /** @type {any} */ (info).sha256) problems.push(`${file}: контрольная сумма не совпадает (файл изменён или повреждён)`);
      else ok += 1;
    }
  }
  return { ok, problems, incomplete };
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const root = process.argv[2];
  if (!root || !existsSync(root)) {
    console.error('Укажите папку с резервными копиями: node tools/verify-backups.mjs <папка>');
    process.exit(2);
  }
  const { ok, problems, incomplete } = verifyBackups(root);
  console.log(`Проверено файлов без ошибок: ${ok}`);
  for (const p of incomplete) console.log(`НЕПОЛНЫЙ экспорт: ${p}`);
  for (const p of problems) console.log(`ОШИБКА: ${p}`);
  process.exit(problems.length ? 1 : 0);
}
