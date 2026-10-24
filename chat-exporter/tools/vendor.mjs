// Copies third-party browser code from node_modules into src/vendor.
// Run after `npm install` when updating a vendored dependency.
import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const files = [
  ['node_modules/marked/lib/marked.esm.js', 'src/vendor/marked.esm.js'],
  ['node_modules/marked/LICENSE.md', 'src/vendor/marked.LICENSE.md'],
  // Type declarations for the type checker only (not loaded by the browser).
  ['node_modules/marked/lib/marked.d.ts', 'src/vendor/marked.esm.d.ts'],
];
mkdirSync(join(root, 'src/vendor'), { recursive: true });
for (const [from, to] of files) {
  copyFileSync(join(root, from), join(root, to));
  console.log(`vendored ${from} -> ${to}`);
}
