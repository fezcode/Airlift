import { readdir, readFile, writeFile, mkdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'smol-toml';
import { execFileSync } from 'node:child_process';
import { prepareIcon, writeIcon } from './icon-assets.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workspace = path.resolve(process.argv[2] || path.join(root, '..'));
const editorial = {
  Descry: ['Productivity', 'A little space for big ideas.', 'A keyboard-driven Markdown editor. Connect your notes, explore your vault, and stay in your flow.', '#b9aceb'],
  Atelier: ['Creative', 'Every image deserves a good frame.', 'A lightweight native image viewer with a thoughtful interface and support for modern formats.', '#e9b29d'],
  clockt: ['Developer tools', 'Your terminal. Your territory.', 'A terminal and multiplexer with persistent sessions, workspaces, splits, and a plugin-driven status bar.', '#b3d7ba'],
  gitland: ['Developer tools', 'A clear view of your code.', 'A native Git client with readable diffs, three-way review, and an editable merge result.', '#8aa7ff'],
  hisashi: ['Utilities', 'Make yourself at home.', 'A customizable top bar and dock. Keep your apps, widgets, and everyday essentials within reach.', '#9cbedc'],
  Timp: ['Media', 'Less interface. More music.', 'An album-art-first music player with a live spectrum, equalizer, playlists, and synced lyrics.', '#e5ba73'],
  tivi: ['Media', 'Press play. Settle in.', 'A native video player with hardware decoding, crisp subtitles, and a warm, minimal interface.', '#aebeea'],
  pidi: ['Productivity', 'A fresh page.', 'A small, native PDF reader with annotations, search, forms, and a comfortable reading experience.', '#e4aca9'],
  Cogas: ['Gaming', 'All your games. One library.', 'Bring your installed games together across stores, compare prices, and find your next favorite.', '#c0a7e9'],
  typewriter: ['Creative', 'Just you and the next sentence.', 'A distraction-free text editor with warm paper, mechanical key sounds, and the soul of a typewriter.', '#dfceb0'],
  xboard: ['Utilities', 'A different way to type.', 'A native virtual keyboard for controllers, with dual-stick, dial, and Morse input.', '#92c4d2'],
  osXos: ['Utilities', 'System care, with no surprises.', 'Everyday system maintenance with clear explanations, a review before changes, and results you can understand.', '#b8cda0'],
};
// Projects with a Forge manifest but no published GitHub release yet. Airlift could only ever
// report "Release not checked" for these, so they wait here until they ship one.
const unpublished = new Set(['fez']);
await mkdir(path.join(root, 'public/icons'), { recursive: true });
await mkdir(path.join(root, 'src/data'), { recursive: true });
const apps = [];
for (const entry of await readdir(workspace, { withFileTypes: true })) {
  if (!entry.isDirectory()) continue;
  if (path.resolve(workspace, entry.name) === root) continue;
  if (unpublished.has(entry.name)) continue;
  const manifestPath = path.join(workspace, entry.name, 'forge.toml');
  let manifest;
  try { manifest = parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, '')); }
  catch (error) { if (error.code === 'ENOENT') continue; throw error; }
  const { app, meta, install } = manifest;
  if (!app?.id || !app.name || !app.version) throw new Error(`Invalid app identity: ${manifestPath}`);
  const [category, tagline, description, color] = editorial[entry.name] || ['Utilities', app.name, meta?.comments || 'An app from your Workhammer workspace.', '#b8c5ac'];
  let icon = '';
  if (app.icon) {
    const asset = await prepareIcon(root, path.join(workspace, entry.name), app.id, app.icon);
    await writeIcon(asset); icon = asset.icon;
  }
  const artifacts = [];
  const distDir = path.join(workspace, entry.name, 'dist');
  try {
    for (const name of await readdir(distDir)) {
      if (name.toLowerCase().endsWith('.exe') && name.includes(app.version) && /setup/i.test(name)) {
        const info = await stat(path.join(distDir, name));
        artifacts.push({ file: name, bytes: info.size, platform: 'windows', format: 'forge-exe' });
      }
    }
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const remote = execFileSync('git', ['-C', path.join(workspace, entry.name), 'remote', 'get-url', 'origin'], { encoding: 'utf8' }).trim();
  const match = remote.match(/^(?:https:\/\/github\.com\/|git@github\.com:)([\w.-]+\/[\w.-]+?)(?:\.git)?$/);
  if (!match) throw new Error(`A public GitHub repository is required: ${entry.name}`);
  apps.push({ id: app.id, name: app.name, version: app.version, publisher: meta?.publisher || 'Fezcode', category, tagline, description, color, icon, project: entry.name, repository: match[1], manifest: `${entry.name}/forge.toml`, platforms: ['windows'], defaultDirectory: install?.default_dir || '', homepage: meta?.homepage || '', artifacts });
}
apps.sort((a, b) => Object.keys(editorial).indexOf(a.project) - Object.keys(editorial).indexOf(b.project));
if (new Set(apps.map(app => app.id)).size !== apps.length) throw new Error('Duplicate application IDs');
await writeFile(path.join(root, 'src/data/catalog.json'), JSON.stringify({ source: 'Local Forge manifests', apps }, null, 2) + '\n');
console.log(`Imported ${apps.length} Forge apps from ${workspace}. No installers were executed.`);
