import test from 'node:test';
import assert from 'node:assert/strict';
import { buildIndex, compareVersions, fetchEntry, KEEP, repositories, SCHEMA, selectReleases, trimRelease } from '../scripts/release-index.mjs';

const repo = 'fezcode/App';
const asset = { id: 1, name: 'App-Setup-1.0.0.exe', size: 1234, state: 'uploaded', browser_download_url: `https://github.com/${repo}/releases/download/v1.0.0/App-Setup-1.0.0.exe`, digest: `sha256:${'A'.repeat(64)}`, uploader: { login: 'x' } };
const release = (tag, extra = {}) => ({ tag_name: tag, html_url: `https://github.com/${repo}/releases/tag/${tag}`, body: 'notes', published_at: '2026-01-01T00:00:00Z', prerelease: false, draft: false, assets: [asset], author: { login: 'x' }, ...extra });

test('lists each catalog repository once, plus Airlift itself', () => {
  const list = repositories({ apps: [{ repository: 'fezcode/B' }, { repository: 'fezcode/a' }, { repository: 'FEZCODE/b' }] });
  assert.deepEqual(list, ['fezcode/a', 'fezcode/Airlift', 'fezcode/B']);
  assert.throws(() => repositories({ apps: [{ repository: 'fezcode/App/../../x' }] }));
});

test('keeps GitHub field names and drops everything Airlift does not read', () => {
  const value = trimRelease(release('v1.0.0'), repo);
  assert.deepEqual(Object.keys(value), ['tag_name', 'html_url', 'body', 'published_at', 'prerelease', 'draft', 'assets']);
  assert.deepEqual(Object.keys(value.assets[0]), ['id', 'name', 'size', 'state', 'browser_download_url', 'digest']);
  assert.equal(value.assets[0].digest, `sha256:${'a'.repeat(64)}`);
});

test('drops drafts, foreign pages and unsafe or unfinished assets', () => {
  assert.equal(trimRelease(release('v1.0.0', { draft: true }), repo), null);
  assert.equal(trimRelease(release('v1.0.0', { html_url: 'https://github.com/other/App/releases/tag/v1.0.0' }), repo), null);
  const value = trimRelease(release('v1.0.0', { assets: [
    asset,
    { ...asset, browser_download_url: 'https://evil.example/App-Setup.exe' },
    { ...asset, browser_download_url: 'https://github.com/other/App/releases/download/v1/x.exe' },
    { ...asset, state: 'new' }, { ...asset, size: 0 }, { ...asset, name: '../x.exe' },
    { ...asset, digest: 'md5:abc' },
  ] }), repo);
  assert.equal(value.assets.length, 2);
  assert.equal(value.assets[1].digest, null);
});

test('caps very long release notes', () => {
  assert.ok(trimRelease(release('v1.0.0', { body: 'x'.repeat(30000) }), repo).body.length < 24100);
});

test('orders versions semantically', () => {
  assert.equal(compareVersions('1.10.0', '1.9.0'), 1);
  assert.equal(compareVersions('1.0.0', '1.0.0-rc.1'), 1);
  assert.equal(compareVersions('1.0.0-beta.2', '1.0.0-beta.11'), -1);
  assert.equal(compareVersions('v1.2.3', '1.2.3'), 0);
});

test('keeps the newest releases and never loses the highest stable or preview', () => {
  const previews = Array.from({ length: KEEP + 5 }, (_, i) => release(`v2.0.0-rc.${i}`, { prerelease: true, published_at: `2026-02-${String(i + 1).padStart(2, '0')}T00:00:00Z` }));
  const stable = release('v1.0.0', { published_at: '2025-01-01T00:00:00Z' });
  const kept = selectReleases([stable, ...previews]);
  assert.equal(kept.length, KEEP + 1);
  assert.ok(kept.includes(stable));
  assert.equal(kept[0].tag_name, `v2.0.0-rc.${KEEP + 4}`);
});

test('a private or missing repository becomes unavailable; other failures throw', async () => {
  const respond = (status, body) => async () => ({ status, ok: status < 300, json: async () => body });
  assert.deepEqual(await fetchEntry(repo, { fetchImpl: respond(404) }), { available: false, releases: [] });
  await assert.rejects(fetchEntry(repo, { fetchImpl: respond(500) }), /HTTP 500/);
  const entry = await fetchEntry(repo, { fetchImpl: respond(200, [release('v1.0.0'), release('v0.9.0', { draft: true })]) });
  assert.equal(entry.available, true); assert.equal(entry.releases.length, 1);
});

test('sends the token only as an Authorization header', async () => {
  let seen;
  await fetchEntry(repo, { token: 'secret', fetchImpl: async (url, init) => { seen = { url, init }; return { status: 200, ok: true, json: async () => [] }; } });
  assert.equal(seen.init.headers.Authorization, 'Bearer secret');
  assert.ok(!seen.url.includes('secret'));
});

test('a failed repository keeps its entry, removed repositories drop out, and no change means no new timestamp', () => {
  const previous = { schema: SCHEMA, generated: 'then', apps: { 'fezcode/A': { available: true, releases: [] }, 'fezcode/Gone': { available: true, releases: [] } } };
  const results = new Map([['fezcode/a', new Error('boom')]]);
  const { changed, index } = buildIndex(previous, results, 'now');
  assert.equal(changed, true); assert.deepEqual(Object.keys(index.apps), ['fezcode/a']); assert.equal(index.generated, 'now');
  const again = buildIndex(index, new Map([['fezcode/a', { available: true, releases: [] }]]), 'later');
  assert.equal(again.changed, false); assert.equal(again.index.generated, 'now');
});
