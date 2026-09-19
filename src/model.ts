import catalog from './data/catalog.json';
export const apps = catalog.apps;
export type App = typeof apps[number];
export type Action = 'install' | 'update' | 'download' | 'uninstall';
export type Job = { id: string; appId: string; action: Action; progress: number; status: 'running' | 'paused' | 'done' | 'cancelled' };
export type State = { installed: Record<string, string>; pinned: string[]; jobs: Job[]; autoUpdate: boolean };
export const initialState: State = { installed: {}, pinned: [], jobs: [], autoUpdate: false };
export function readState(): State {
  try {
    const value = JSON.parse(localStorage.getItem('airlift-preview-v1') || 'null');
    if (!value || typeof value.installed !== 'object' || !value.installed || !Array.isArray(value.pinned) || !Array.isArray(value.jobs)) return initialState;
    return { installed: Object.fromEntries(Object.entries(value.installed).filter(([id, version]) => apps.some(a => a.id === id) && typeof version === 'string')) as Record<string, string>, pinned: value.pinned.filter((id: unknown) => typeof id === 'string'), jobs: value.jobs.filter((j: Job) => apps.some(a => a.id === j.appId) && ['install', 'update', 'download', 'uninstall'].includes(j.action) && ['running', 'paused', 'done', 'cancelled'].includes(j.status) && Number.isFinite(j.progress)), autoUpdate: value.autoUpdate === true };
  } catch { return initialState; }
}
export const labels: Record<Action, string> = { install: 'Install', update: 'Update', download: 'Download', uninstall: 'Uninstall' };
export function size(app: App) { const bytes = app.artifacts[0]?.bytes; return bytes ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : 'No local build'; }
