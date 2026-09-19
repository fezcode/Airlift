import { useEffect, useState } from 'react';
import { ArrowUpRight, Github, LoaderCircle, RefreshCw } from 'lucide-react';
import { apps } from './model';
import { getLatestRelease, type ReleaseResult } from './github';

export function Sources() {
  const [results, setResults] = useState<Record<string, ReleaseResult>>({});
  const [checking, setChecking] = useState(false);
  const [lastCheck, setLastCheck] = useState(0);
  const [cooldown, setCooldown] = useState(false);
  useEffect(() => { if (!cooldown) return; const timer = setTimeout(() => setCooldown(false), 60_000); return () => clearTimeout(timer); }, [cooldown]);
  async function checkReleases() {
    if (checking || Date.now() - lastCheck < 60_000) return;
    setChecking(true);
    try {
      for (const app of apps) {
        try { const release = await getLatestRelease(app.repository); setResults(s => ({ ...s, [app.id]: { release } })); }
        catch (error) {
          const message = error instanceof Error ? error.message : 'Could not reach GitHub';
          setResults(s => ({ ...s, [app.id]: { error: message } }));
          if (message.includes('rate limit')) break;
        }
      }
    } finally { setChecking(false); setLastCheck(Date.now()); setCooldown(true); }
  }
  return <section className="panel"><div className="source-header"><span className="source-icon"><Github size={28}/></span><div><h2>GitHub Releases</h2><p>Public repositories · Fezcode</p></div><button className="secondary source-refresh" disabled={checking || cooldown} onClick={checkReleases}>{checking ? <LoaderCircle className="spin" size={14}/> : <RefreshCw size={14}/>} {checking ? 'Checking…' : 'Check releases'}</button></div><div className="source-stats"><div><span>REPOSITORIES</span><strong>{apps.length}</strong></div><div><span>RELEASE CHANNEL</span><strong>Stable</strong></div><div><span>ACCESS</span><strong>Public · No sign-in</strong></div></div><div className="inline-note">{checking ? 'Reading public release metadata from GitHub…' : lastCheck ? `Last checked ${new Date(lastCheck).toLocaleTimeString()}. Refresh is limited to once per minute.` : 'Check releases to fetch current published versions and asset links from GitHub. Local catalog versions come from Forge manifests.'} Downloads open GitHub; in-app installation remains simulated.</div><div className="repo-list">{apps.map(app => {
    const result = results[app.id];
    return <div className="repo-row" key={app.id}><div className="repo-heading"><a href={`https://github.com/${app.repository}/releases`} target="_blank" rel="noreferrer"><Github size={14}/>{app.repository}<ArrowUpRight size={12}/></a><span>{result?.release ? result.release.tag : result?.error ? 'Unavailable' : 'Not checked'}</span></div>{result?.error && <p className="release-error">{result.error}</p>}{result?.release && <div className="release-assets">{result.release.assets.length ? result.release.assets.map(asset => <a key={asset.url} href={asset.url} target="_blank" rel="noreferrer" title={asset.digest ? 'GitHub provides a SHA-256 digest; the preview has not downloaded or verified this file.' : 'No SHA-256 digest provided by GitHub.'}>{asset.name}<span>{(asset.bytes / 1024 / 1024).toFixed(1)} MB</span><ArrowUpRight size={11}/></a>) : <p>No downloadable assets attached to this release.</p>}</div>}</div>;
  })}</div><div className="source-copy"><h3>Follow the release, from source to setup.</h3><p>Airlift will track each repository’s published releases, match packages to your operating system and processor, and keep your installed versions up to date. The desktop app will add scheduled checks, prerelease channels, resumable downloads, and checksum verification.</p></div></section>;
}
