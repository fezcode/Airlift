export type ReleaseAsset = { name: string; bytes: number; url: string; digest: string | null };
export type Release = { tag: string; url: string; publishedAt: string; assets: ReleaseAsset[]; checkedAt: string };
export type ReleaseResult = { release?: Release; error?: string };
export function repositoryPath(repository: string): string {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Invalid GitHub repository');
  return repository.split('/').map(encodeURIComponent).join('/');
}
export function normalizeRelease(value: unknown, repository: string): Release {
  const data = value as Record<string, unknown>;
  if (!data || typeof data.tag_name !== 'string' || data.draft !== false || data.prerelease !== false || !Array.isArray(data.assets)) throw new Error('GitHub returned an invalid stable release');
  const path = repositoryPath(repository);
  const releaseUrl = new URL(String(data.html_url));
  if (releaseUrl.origin !== 'https://github.com' || !releaseUrl.pathname.toLowerCase().startsWith(`/${path}/releases/tag/`.toLowerCase())) throw new Error('Unexpected release URL');
  return {
    tag: data.tag_name, url: releaseUrl.href, publishedAt: typeof data.published_at === 'string' ? data.published_at : '', checkedAt: new Date().toISOString(),
    assets: data.assets.filter((a: Record<string, unknown>) => {
      if (!a || typeof a.name !== 'string' || typeof a.size !== 'number' || a.size < 0 || a.state !== 'uploaded') return false;
      try { const url = new URL(String(a.browser_download_url)); return url.origin === 'https://github.com' && url.pathname.toLowerCase().startsWith(`/${path}/releases/download/`.toLowerCase()); } catch { return false; }
    }).map((a: Record<string, unknown>) => ({ name: String(a.name), bytes: Number(a.size), url: String(a.browser_download_url), digest: typeof a.digest === 'string' && /^sha256:[a-f0-9]{64}$/.test(a.digest) ? a.digest : null })),
  };
}
export async function getLatestRelease(repository: string): Promise<Release> {
  const path = repositoryPath(repository);
  const response = await fetch(`https://api.github.com/repos/${path}/releases/latest`, { headers: { Accept: 'application/vnd.github+json' }, signal: AbortSignal.timeout(15000) });
  if (response.status === 404) throw new Error('No public stable release found');
  if (response.status === 403 || response.status === 429) throw new Error('GitHub rate limit or access restriction. Try again later.');
  if (!response.ok) throw new Error(`GitHub returned HTTP ${response.status}`);
  return normalizeRelease(await response.json(), repository);
}
