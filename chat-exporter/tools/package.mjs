#!/usr/bin/env node
// Builds dist/chat-exporter-<version>.zip with only the files the browser needs
// (manifest, icons, src without type declarations). Requires the `zip` command.
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const { version } = JSON.parse(readFileSync(join(root, 'manifest.json'), 'utf8'));
const out = join(root, 'dist', `chat-exporter-${version}.zip`);
mkdirSync(join(root, 'dist'), { recursive: true });
if (existsSync(out)) rmSync(out);
execFileSync('zip', ['-r', '-X', '-q', out, 'manifest.json', 'icons', 'src', '-x', '*.d.ts', '-x', '*.DS_Store'], { cwd: root, stdio: 'inherit' });
console.log(`written ${out}`);
