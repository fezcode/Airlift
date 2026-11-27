// Builds release.json, the one file Airlift reads to learn every catalog app's releases.
//
// The Release index workflow runs this whenever a catalog app publishes, edits or deletes a
// release (each app repository notifies fezcode/Airlift), when Airlift itself releases, when
// the catalog changes, and once a day as a safety net. It always rebuilds every entry: GitHub
// keeps only one queued run per concurrency group, so a run that covered a single repository
// could silently lose another app's notification.
//
// Releases keep GitHub's own field names, so the desktop app validates them with the same
// parser it has always used. A repository GitHub answers 404 for (private, renamed away or
// deleted) is listed as unavailable and Airlift hides it. A repository that fails for any
// other reason keeps its previous entry, so a GitHub hiccup never empties the index.
//
// Usage: node scripts/release-index.mjs [--out release.json] [--catalog src/data/catalog.json]
// Reads GITHUB_TOKEN when set (the workflow's built-in token is enough for public repositories).
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const SCHEMA = 1;
export const SELF = 'fezcode/Airlift';
export const KEEP = 20;
export const MAX_NOTES = 24000;
export const MAX_BYTES = 8_000_000;
const REPOSITORY = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const SEMVER = /^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;
const DIGEST = /^sha256:[a-fA-F0-9]{64}$/;

/** Every repository the index covers: the catalog's, plus Airlift's own for self-update. */
export function repositories(catalog) {
  const seen = new Map();
  for (const repository of [...catalog.apps.map((app) => app.repository), SELF]) {
    if (!REPOSITORY.test(repository)) throw new Error(`Invalid repository in catalog: ${repository}`);
    if (!seen.has(repository.toLowerCase())) seen.set(repository.toLowerCase(), repository);
  }
  return [...seen.values()].sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
}

export function compareVersions(left, right) {
  const a = SEMVER.exec(left.trim()); const b = SEMVER.exec(right.trim());
  if (!a || !b) throw new Error('Not a semantic version.');
  for (let i = 1; i <= 3; i++) {
    const c = BigInt(a[i]) < BigInt(b[i]) ? -1 : BigInt(a[i]) > BigInt(b[i]) ? 1 : 0;
    if (c) return c;
  }
  const ap = a[4] ?? ''; const bp = b[4] ?? '';
  if (ap === bp) return 0;
  if (!ap) return 1;
  if (!bp) return -1;
  const aa = ap.split('.'); const bb = bp.split('.');
  for (let i = 0; i < Math.min(aa.length, bb.length); i++) {
    const an = /^\d+$/.test(aa[i]); const bn = /^\d+$/.test(bb[i]);
    const c = an && bn ? (BigInt(aa[i]) < BigInt(bb[i]) ? -1 : BigInt(aa[i]) > BigInt(bb[i]) ? 1 : 0)
      : an ? -1 : bn ? 1 : aa[i] < bb[i] ? -1 : aa[i] > bb[i] ? 1 : 0;
    if (c) return c;
  }
  return Math.sign(aa.length - bb.length);
}

const isVersioned = (release) => SEMVER.test(release.tag_name.trim());

/**
 * Keeps only what Airlift reads, in GitHub's shape. Drafts, releases whose page is outside the
 * repository, and assets that are unfinished, empty or hosted elsewhere are dropped here;
 * Airlift checks all of it again when it reads the index.
 */
export function trimRelease(release, repository) {
  if (!release || release.draft || typeof release.tag_name !== 'string' || typeof release.html_url !== 'string') return null;
  const prefix = `https://github.com/${repository}/releases/`.toLowerCase();
  if (!release.html_url.toLowerCase().startsWith(`${prefix}tag/`)) return null;
  const assets = (Array.isArray(release.assets) ? release.assets : [])
    .filter((asset) => asset && asset.state === 'uploaded' && Number.isSafeInteger(asset.size) && asset.size > 0
      && typeof asset.name === 'string' && !/[\\/]/.test(asset.name) && asset.name !== '.' && asset.name !== '..'
      && typeof asset.browser_download_url === 'string' && asset.browser_download_url.toLowerCase().startsWith(`${prefix}download/`))
    .map((asset) => ({
      id: asset.id, name: asset.name, size: asset.size, state: 'uploaded',
      browser_download_url: asset.browser_download_url,
      digest: typeof asset.digest === 'string' && DIGEST.test(asset.digest) ? asset.digest.toLowerCase() : null,
    }));
  let body = typeof release.body === 'string' ? release.body : '';
  if (body.length > MAX_NOTES) body = `${body.slice(0, MAX_NOTES)}\n\n… Full notes are available on GitHub.`;
  return {
    tag_name: release.tag_name, html_url: release.html_url, body,
    published_at: release.published_at, prerelease: Boolean(release.prerelease), draft: false, assets,
  };
}

/** The newest releases by date, always including the highest stable and the highest overall version. */
export function selectReleases(releases) {
  const byDate = [...releases].sort((a, b) => String(b.published_at).localeCompare(String(a.published_at)));
  const kept = byDate.slice(0, KEEP);
  const versioned = byDate.filter(isVersioned);
  const highest = (list) => list.reduce((best, r) => (!best || compareVersions(r.tag_name, best.tag_name) > 0 ? r : best), null);
  for (const required of [highest(versioned.filter((r) => !r.prerelease)), highest(versioned)])
    if (required && !kept.includes(required)) kept.push(required);
  return kept;
}

export async function fetchEntry(repository, { token, fetchImpl = fetch } = {}) {
  const headers = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'airlift-release-index' };
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetchImpl(`https://api.github.com/repos/${repository}/releases?per_page=100`, { headers });
  if (response.status === 404) return { available: false, releases: [] };
  if (!response.ok) throw new Error(`GitHub returned HTTP ${response.status}`);
  const list = await response.json();
  if (!Array.isArray(list)) throw new Error('GitHub returned an unexpected response.');
  return { available: true, releases: selectReleases(list.map((release) => trimRelease(release, repository)).filter(Boolean)) };
}

/**
 * Merges fresh entries over the previous index. `results` maps each repository to an entry or
 * an Error; a failed repository keeps its previous entry. Repositories no longer in the catalog
 * drop out. `generated` only moves when the content does, so an unchanged run commits nothing.
 */
export function buildIndex(previous, results, now) {
  const apps = {};
  for (const [repository, result] of results) {
    if (result instanceof Error) {
      const old = previous?.apps && Object.entries(previous.apps).find(([key]) => key.toLowerCase() === repository.toLowerCase());
      if (old) apps[repository] = old[1];
    } else apps[repository] = result;
  }
  const changed = previous?.schema !== SCHEMA || JSON.stringify(apps) !== JSON.stringify(previous?.apps ?? {});
  return { changed, index: { schema: SCHEMA, generated: changed || !previous?.generated ? now : previous.generated, apps } };
}

async function main() {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const args = process.argv.slice(2);
  const option = (name, fallback) => { const i = args.indexOf(name); return i >= 0 && args[i + 1] ? args[i + 1] : fallback; };
  const out = path.resolve(root, option('--out', 'release.json'));
  const catalog = JSON.parse(await readFile(path.resolve(root, option('--catalog', 'src/data/catalog.json')), 'utf8'));
  let previous = null;
  try { previous = JSON.parse(await readFile(out, 'utf8')); } catch (error) { if (error.code !== 'ENOENT') throw error; }

  const results = new Map(); let failures = 0;
  for (const repository of repositories(catalog)) {
    try {
      const entry = await fetchEntry(repository, { token: process.env.GITHUB_TOKEN });
      results.set(repository, entry);
      console.log(`${repository}: ${entry.available ? `${entry.releases.length} releases` : 'unavailable (private or missing)'}`);
    } catch (error) {
      failures++; results.set(repository, error instanceof Error ? error : new Error(String(error)));
      console.log(`::warning::${repository}: ${error.message ?? error}; keeping its previous entry.`);
    }
  }
  if (failures === results.size) throw new Error('Every repository failed; leaving release.json untouched.');

  const { changed, index } = buildIndex(previous, results, new Date().toISOString());
  const text = `${JSON.stringify(index, null, 2)}\n`;
  if (Buffer.byteLength(text) > MAX_BYTES) throw new Error('release.json would exceed the size Airlift accepts.');
  if (changed) { await writeFile(out, text); console.log(`Wrote ${path.relative(root, out)}.`); }
  else console.log('No release changes.');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message ?? error); process.exitCode = 1; });
}
