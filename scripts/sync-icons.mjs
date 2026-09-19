import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'smol-toml';
import { prepareIcon, writeIcon } from './icon-assets.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workspace = path.resolve(process.argv[2] || path.join(root, '..'));
const catalogPath = path.join(root, 'src/data/catalog.json');
const catalog = JSON.parse(await readFile(catalogPath, 'utf8'));
const pending = [];
// Validate every source before replacing assets. This is an icons-only refresh, not a catalog/version import.
for (const app of catalog.apps) {
  const project = path.resolve(workspace, app.project);
  if (path.dirname(project) !== workspace) throw new Error(`Project outside workspace: ${app.project}`);
  const manifest = parse((await readFile(path.join(project, 'forge.toml'), 'utf8')).replace(/^\uFEFF/, ''));
  if (manifest.app?.id !== app.id) throw new Error(`Manifest identity changed for ${app.name}`);
  pending.push({ app, icon: await prepareIcon(root, project, app.id, manifest.app.icon) });
}
let pathsChanged = false;
for (const { app, icon } of pending) {
  await writeIcon(icon);
  if (app.icon !== icon.icon) { app.icon = icon.icon; pathsChanged = true; }
  console.log(`${app.name}: ${path.relative(workspace, icon.source)} -> browser + native`);
}
if (pathsChanged) await writeFile(catalogPath, JSON.stringify(catalog, null, 2) + '\n');
console.log(`Refreshed ${pending.length} icons. Catalog versions and app metadata preserved.`);
