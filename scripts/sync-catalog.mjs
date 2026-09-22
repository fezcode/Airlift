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
  SirWordALot: ['Productivity', 'Words that stay with you.', 'A vocabulary and thesaurus trainer. Keep a word with the sentence you met it in, enrich it with real synonyms, then meet it again until it sticks.', '#96c4b1'],
  hammeros: ['Utilities', 'A desktop inside your desktop.', 'A fullscreen retro workspace with your real files, independent shells, and Windows apps embedded in one tiled screen.', '#b3e5da'],
};
// A sibling project joins the shelf by inviting itself: a `properties.piml` holding `(airlift) true`.
// Without that line a project stays out however complete its Forge manifest is, which is how work in
// progress and tooling nobody installs (fez, gox) keep to themselves. Airlift itself never enlists.
const invited = async project => {
  let piml;
  try { piml = await readFile(path.join(project, 'properties.piml'), 'utf8'); }
  catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  return /^[ \t]*\(airlift\)[ \t]+true[ \t]*$/im.test(piml.replace(/^\uFEFF/, ''));
};
const rank = project => { const place = Object.keys(editorial).indexOf(project); return place < 0 ? Number.MAX_SAFE_INTEGER : place; };
await mkdir(path.join(root, 'public/icons'), { recursive: true });
await mkdir(path.join(root, 'src/data'), { recursive: true });
const apps = [];
const unpublished = [];
for (const entry of await readdir(workspace, { withFileTypes: true })) {
  if (!entry.isDirectory()) continue;
  const project = path.resolve(workspace, entry.name);
  if (project === root) continue;
  if (!await invited(project)) continue;
  const manifestPath = path.join(project, 'forge.toml');
  let manifest;
  try { manifest = parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, '')); }
  catch (error) {
    if (error.code === 'ENOENT') throw new Error(`Invited by properties.piml but carries no Forge manifest: ${manifestPath}`);
    throw error;
  }
  const { app, meta, install } = manifest;
  if (!app?.id || !app.name || !app.version) throw new Error(`Invalid app identity: ${manifestPath}`);
  let remote = '';
  try { remote = execFileSync('git', ['-C', project, 'remote', 'get-url', 'origin'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); }
  catch { remote = ''; }
  const match = remote.match(/^(?:https:\/\/github\.com\/|git@github\.com:)([\w.-]+\/[\w.-]+?)(?:\.git)?$/);
  // Airlift follows public GitHub releases, so an invited project that has not been published yet
  // waits outside the catalog rather than failing the import. It joins the shelf when its repository does.
  if (!match) { unpublished.push(entry.name); continue; }
  const [category, tagline, description, color] = editorial[entry.name] || ['Utilities', app.name, meta?.comments || 'An app from your Workhammer workspace.', '#b8c5ac'];
  let icon = '';
  if (app.icon) {
    const asset = await prepareIcon(root, project, app.id, app.icon);
    await writeIcon(asset); icon = asset.icon;
  }
  const artifacts = [];
  const distDir = path.join(project, 'dist');
  try {
    for (const name of await readdir(distDir)) {
      if (name.toLowerCase().endsWith('.exe') && name.includes(app.version) && /setup/i.test(name)) {
        const info = await stat(path.join(distDir, name));
        artifacts.push({ file: name, bytes: info.size, platform: 'windows', format: 'forge-exe' });
      }
    }
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  apps.push({ id: app.id, name: app.name, version: app.version, publisher: meta?.publisher || 'Fezcode', category, tagline, description, color, icon, project: entry.name, repository: match[1], manifest: `${entry.name}/forge.toml`, platforms: ['windows'], defaultDirectory: install?.default_dir || '', homepage: meta?.homepage || '', artifacts });
}
apps.sort((a, b) => rank(a.project) - rank(b.project));
if (new Set(apps.map(app => app.id)).size !== apps.length) throw new Error('Duplicate application IDs');
await writeFile(path.join(root, 'src/data/catalog.json'), JSON.stringify({ source: 'Local Forge manifests', apps }, null, 2) + '\n');
console.log(`Imported ${apps.length} Forge apps from ${workspace}. No installers were executed.`);
if (unpublished.length) console.log(`Waiting for a public GitHub repository: ${unpublished.join(', ')}.`);
