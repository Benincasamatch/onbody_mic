import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { check } from '@tauri-apps/plugin-updater';
import type { Update } from '@tauri-apps/plugin-updater';
import { openUrl } from '@tauri-apps/plugin-opener';
import { Button, Dialog, Icon, IconButton } from './components';
import { errorMessage, nativeRuntime } from './bridge';
import type { AppInfo, BuildInfo, LatestRelease, ReleaseNote, UpdateCheck } from './version';
import { fetchLatestRelease, formatBytes, formatStamp, isNewerVersion, toUpdateCheck } from './version';

const CHECK_INTERVAL_MS = 12 * 60 * 60 * 1000;
const CHECKED_AT_KEY = 'onbodymic.update.checkedAt';
const LATEST_KEY = 'onbodymic.update.latest';
const DISMISSED_KEY = 'onbodymic.update.dismissed';

function readStorage(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function writeStorage(key: string, value: string) {
  try { localStorage.setItem(key, value); } catch { /* private or restricted webview: remember for this session only */ }
}

function readDismissed(): string[] {
  try {
    const parsed = JSON.parse(readStorage(DISMISSED_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter(value => typeof value === 'string').slice(0, 20) : [];
  } catch { return []; }
}

function readCachedRelease(): LatestRelease | null {
  try {
    const parsed = JSON.parse(readStorage(LATEST_KEY) ?? 'null') as Partial<LatestRelease> | null;
    if (!parsed || typeof parsed.tag_name !== 'string' || typeof parsed.html_url !== 'string') return null;
    return {
      tag_name: parsed.tag_name,
      name: typeof parsed.name === 'string' ? parsed.name : null,
      body: typeof parsed.body === 'string' ? parsed.body : null,
      html_url: parsed.html_url,
      published_at: typeof parsed.published_at === 'string' ? parsed.published_at : null,
      prerelease: parsed.prerelease === true,
      draft: parsed.draft === true,
      assets: Array.isArray(parsed.assets) ? parsed.assets.filter(asset => asset && typeof asset.name === 'string' && typeof asset.browser_download_url === 'string').map(asset => ({ name: asset.name, browser_download_url: asset.browser_download_url, size: Number(asset.size) || 0 })) : [],
    };
  } catch { return null; }
}

export async function openExternal(url: string) {
  if (nativeRuntime) { await openUrl(url); return; }
  window.open(url, '_blank', 'noopener,noreferrer');
}

export type InstallPhase = 'idle' | 'checking' | 'downloading' | 'installing' | 'failed';

export interface InstallState {
  phase: InstallPhase;
  received: number;
  total: number | null;
  error: string;
}

const IDLE_INSTALL: InstallState = { phase: 'idle', received: 0, total: null, error: '' };

interface PortableManifest { url: string; sha256: string; size: number | null }

// Release URLs only; loopback is accepted so local end-to-end tests can run,
// the Rust side additionally requires ONBODYMIC_UPDATE_ALLOW_LOCAL=1 for it.
const UPDATE_URL_PATTERN = /^(https:\/\/github\.com\/Benincasamatch\/onbody_mic\/releases\/download\/|http:\/\/127\.0\.0\.1:)/;

export function portableManifest(rawJson: Record<string, unknown>): PortableManifest {
  const portable = rawJson.portable as { url?: unknown; sha256?: unknown; size?: unknown } | undefined;
  if (!portable || typeof portable.url !== 'string' || !UPDATE_URL_PATTERN.test(portable.url)) {
    throw new Error('更新清单缺少可用的便携包地址。');
  }
  if (typeof portable.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(portable.sha256)) {
    throw new Error('更新清单缺少可用的便携包校验值。');
  }
  const size = typeof portable.size === 'number' && Number.isFinite(portable.size) && portable.size > 0 ? portable.size : null;
  return { url: portable.url, sha256: portable.sha256.toLowerCase(), size };
}

export function installSummary(state: InstallState, version: string): string {
  const percent = state.total && state.total > 0 ? Math.min(100, Math.round(state.received / state.total * 100)) : null;
  switch (state.phase) {
    case 'checking': return '正在获取更新清单…';
    case 'downloading': return state.total ? `正在下载 v${version} · ${percent}%（${formatBytes(state.received)} / ${formatBytes(state.total)}）` : `正在下载 v${version} · ${formatBytes(state.received)}`;
    case 'installing': return `正在退出并安装 v${version}，完成后会自动重启。`;
    default: return '';
  }
}

export function useUpdater(currentVersion: string | undefined) {
  const [info, setInfo] = useState<AppInfo | null>(null);
  const [infoError, setInfoError] = useState('');
  const [update, setUpdate] = useState<UpdateCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState('');
  const [checkedAt, setCheckedAt] = useState<number | null>(() => {
    const value = Number(readStorage(CHECKED_AT_KEY));
    return Number.isFinite(value) && value > 0 ? value : null;
  });
  const [dismissed, setDismissed] = useState<string[]>(readDismissed);
  const [cachedRelease] = useState<LatestRelease | null>(readCachedRelease);
  const [installState, setInstallState] = useState<InstallState>(IDLE_INSTALL);
  const running = useRef(false);
  const installing = useRef(false);

  useEffect(() => {
    if (!nativeRuntime) return;
    let stopped = false;
    void invoke<AppInfo>('app_info')
      .then(value => { if (!stopped) setInfo(value); })
      .catch(failure => { if (!stopped) setInfoError(errorMessage(failure)); });
    return () => { stopped = true; };
  }, []);

  useEffect(() => {
    if (!info || update || !cachedRelease) return;
    setUpdate(toUpdateCheck(cachedRelease, info.build.version || currentVersion || '0.0.0'));
  }, [info, update, cachedRelease, currentVersion]);

  const checkNow = useCallback(async (manual = false) => {
    if (!info || running.current) return;
    if (typeof navigator !== 'undefined' && navigator.onLine === false) {
      if (manual) setError('当前处于离线状态，无法检查更新。');
      return;
    }
    running.current = true;
    setChecking(true);
    setError('');
    try {
      const latest = await fetchLatestRelease(info.updateApi);
      const result = toUpdateCheck(latest, info.build.version || currentVersion || '0.0.0');
      setUpdate(result);
      setCheckedAt(result.checkedAt);
      writeStorage(CHECKED_AT_KEY, String(result.checkedAt));
      writeStorage(LATEST_KEY, JSON.stringify(latest));
    } catch (failure) {
      if (manual) setError(errorMessage(failure));
    } finally {
      running.current = false;
      setChecking(false);
    }
  }, [info, currentVersion]);

  useEffect(() => {
    if (!info || running.current) return;
    if (checkedAt && Date.now() - checkedAt < CHECK_INTERVAL_MS) return;
    void checkNow(false);
  }, [info, checkedAt, checkNow]);

  const installKind = info?.installKind ?? 'unknown';
  const installable = installKind === 'installed' || installKind === 'portable';

  function applyInstall(next: Partial<InstallState> & { phase: InstallPhase }) {
    setInstallState(current => ({ ...current, received: 0, total: null, error: '', ...next }));
  }

  const install = useCallback(async () => {
    if (!info || !update?.available || installing.current || !installable) return;
    installing.current = true;
    applyInstall({ phase: 'checking' });
    let pending: Update | null = null;
    let unlisten: (() => void) | undefined;
    try {
      pending = await check({ timeout: 20000 });
      if (!pending) throw new Error('该版本未提供应用内更新清单（latest.json），请改用“下载安装程序”。');
      if (isNewerVersion(update.latestVersion, pending.version)) {
        throw new Error(`应用内更新清单仍为 v${pending.version}，尚未同步到 v${update.latestVersion}。请稍后重试或直接下载安装。`);
      }
      if (installKind === 'portable') {
        const manifest = portableManifest(pending.rawJson);
        await pending.close();
        pending = null;
        applyInstall({ phase: 'downloading', total: manifest.size ?? null });
        unlisten = await listen<{ phase: string; received: number; total: number | null }>('update_progress', event => {
          const payload = event.payload;
          if (payload.phase === 'downloading') applyInstall({ phase: 'downloading', received: payload.received, total: payload.total ?? manifest.size ?? null });
          else if (payload.phase === 'installing') applyInstall({ phase: 'installing' });
        });
        await invoke('install_portable_update', { url: manifest.url, sha256: manifest.sha256, size: manifest.size ?? null });
        applyInstall({ phase: 'installing' });
      } else {
        let received = 0;
        let total: number | null = null;
        await pending.download(event => {
          if (event.event === 'Started') total = event.data.contentLength ?? null;
          else if (event.event === 'Progress') received += event.data.chunkLength;
          applyInstall({ phase: 'downloading', received, total });
        });
        applyInstall({ phase: 'installing', total });
        await invoke('desktop_command', { op: 'update_prepare', args: {} });
        try {
          await pending.install({ restartAfterInstall: true });
        } catch (failure) {
          void invoke('desktop_command', { op: 'restart_engine', args: {} }).catch(() => undefined);
          throw failure;
        }
        pending = null;
      }
    } catch (failure) {
      if (pending) void pending.close().catch(() => undefined);
      setInstallState({ phase: 'failed', received: 0, total: null, error: errorMessage(failure) });
    } finally {
      unlisten?.();
      installing.current = false;
    }
  }, [info, update, installable, installKind]);

  function dismiss() {
    if (!update) return;
    const next = [...new Set([...dismissed, update.latest.tag_name])].slice(-20);
    setDismissed(next);
    writeStorage(DISMISSED_KEY, JSON.stringify(next));
  }

  const banner = update?.available && !dismissed.includes(update.latest.tag_name) ? update : null;
  return { info, infoError, update, banner, checking, error, checkedAt, checkNow, dismiss, installable, installKind, install, installState };
}

export function InstallStatus({ state, version, bar = false }: { state: InstallState; version: string; bar?: boolean }) {
  if (state.phase === 'idle' || state.phase === 'failed') return null;
  const percent = state.total && state.total > 0 ? Math.min(100, Math.round(state.received / state.total * 100)) : null;
  return <div className="update-install" role="status" aria-live="polite">
    <span>{installSummary(state, version)}</span>
    {bar && state.phase === 'downloading' && <div className="update-progress" role="progressbar" aria-label="更新下载进度" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? undefined} aria-valuetext={percent === null ? '正在下载' : `${percent}%`}>
      <span className={percent === null ? 'indeterminate' : ''} style={percent === null ? undefined : { width: `${percent}%` }} />
    </div>}
  </div>;
}

export function UpdateBanner({ update, currentVersion, installable, installKind, installState, install, onOpen, onDismiss, onError }: {
  update: UpdateCheck;
  currentVersion: string;
  installable: boolean;
  installKind: string;
  installState: InstallState;
  install(): void;
  onOpen(): void;
  onDismiss(): void;
  onError(message: string): void;
}) {
  const busy = installState.phase === 'checking' || installState.phase === 'downloading' || installState.phase === 'installing';
  const download = () => { void openExternal(update.downloadUrl).catch(failure => onError(errorMessage(failure))); };
  const description = busy
    ? installSummary(installState, update.latestVersion)
    : installable
      ? installKind === 'portable'
        ? '应用内更新会下载便携包，替换当前文件夹的程序，完成后自动重启；素材、预设和设置保留。'
        : '应用内更新会下载签名安装程序并静默覆盖，完成后自动重启；配置、预设和音效保留。'
      : '当前构建不支持应用内更新。下载安装程序会覆盖程序文件，配置、预设和音效保留。';
  return <div className="update-banner" role="status">
    <span className="update-banner-icon"><Icon name="download" size={18} /></span>
    <div><strong>发现新版本 v{update.latestVersion}</strong><p>当前 v{currentVersion}。{description}</p>{installState.phase === 'failed' && <p className="inline-error">{installState.error}</p>}</div>
    {installable ? <Button variant="primary" disabled={busy} onClick={install}><Icon name="download" size={14} />{installState.phase === 'failed' ? '重试更新' : '立即更新'}</Button> : <Button variant="primary" onClick={download}><Icon name="download" size={14} />{update.downloadLabel}</Button>}
    <Button disabled={busy} onClick={onOpen}>更新说明</Button>
    <IconButton icon="close" label="稍后提醒此版本" disabled={busy} onClick={onDismiss} />
  </div>;
}

function inline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g).filter(Boolean).map((part, index) => {
    if (part.startsWith('**') && part.endsWith('**')) return <strong key={index}>{part.slice(2, -2)}</strong>;
    if (part.startsWith('`') && part.endsWith('`')) return <code key={index}>{part.slice(1, -1)}</code>;
    return part;
  });
}

function MarkdownNotes({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  let items: string[] = [];
  function flush() {
    if (items.length) { blocks.push(<ul key={`list-${blocks.length}`}>{items.map((item, index) => <li key={index}>{inline(item)}</li>)}</ul>); items = []; }
  }
  for (const raw of text.replace(/\r\n/g, '\n').split('\n')) {
    const line = raw.trimEnd();
    if (/^#{2,4}\s/.test(line)) { flush(); blocks.push(<h4 key={`heading-${blocks.length}`}>{inline(line.replace(/^#{2,4}\s+/, ''))}</h4>); continue; }
    if (/^[-*]\s+/.test(line)) { items.push(line.replace(/^[-*]\s+/, '')); continue; }
    if (!line.trim()) { flush(); continue; }
    flush();
    blocks.push(<p key={`paragraph-${blocks.length}`}>{inline(line)}</p>);
  }
  flush();
  return <div className="release-notes">{blocks}</div>;
}

function LocalNotes({ releases }: { releases: ReleaseNote[] }) {
  if (!releases.length) return <p className="muted">没有可读取的本地更新说明。</p>;
  return <div className="release-history">{releases.slice(0, 8).map(release => <section key={`${release.version}:${release.date ?? ''}`}>
    <h4>v{release.version}{release.date ? <small>{release.date}</small> : null}</h4>
    {release.sections.map(section => <div key={section.title}>
      <h5>{section.title}</h5>
      <ul>{section.items.map((item, index) => <li key={index}>{inline(item)}</li>)}</ul>
    </div>)}
  </section>)}</div>;
}

function BuildFacts({ build }: { build: BuildInfo }) {
  return <dl className="build-facts">
    <div><dt>版本</dt><dd>v{build.version}{build.dirty ? <span className="dirty-chip">开发构建</span> : null}</dd></div>
    <div><dt>提交</dt><dd title={build.commit}>{build.commitShort === 'unknown' ? '未知' : build.commitShort}</dd></div>
    <div><dt>提交时间</dt><dd>{formatStamp(build.commitTime)}</dd></div>
    <div><dt>构建时间</dt><dd>{formatStamp(build.buildTime)}</dd></div>
  </dl>;
}

export function AboutDialog({ info, infoError, update, checking, error, checkedAt, checkNow, installable, installKind, installState, install, close, onError }: {
  info: AppInfo | null;
  infoError: string;
  update: UpdateCheck | null;
  checking: boolean;
  error: string;
  checkedAt: number | null;
  checkNow(): void;
  installable: boolean;
  installKind: string;
  installState: InstallState;
  install(): void;
  close(): void;
  onError(message: string): void;
}) {
  const build = info?.build;
  const currentVersion = build?.version ?? '0.0.0';
  const latestNotes = update?.latest.body?.trim();
  const busy = installState.phase === 'checking' || installState.phase === 'downloading' || installState.phase === 'installing';
  const download = () => { if (update) void openExternal(update.downloadUrl).catch(failure => onError(errorMessage(failure))); };
  return <Dialog title="关于随声麦与更新" close={close}>
    <div className="about-heading">
      <span className="about-symbol"><Icon name="audio" size={30} /></span>
      <div><strong>随声麦 · Onbody Mic</strong><p>游戏语音音频面板 · 本地运行，不上传音频</p></div>
    </div>
    {build ? <BuildFacts build={build} /> : <p className={infoError ? 'inline-error' : 'muted'} role={infoError ? 'alert' : undefined}>{infoError || '正在读取构建信息…'}</p>}

    <section className={`update-card ${update?.available ? 'available' : ''}`}>
      <div className="update-card-heading">
        <Icon name={update?.available ? 'download' : 'clock'} size={18} />
        <div>
          <strong>{checking ? '正在检查更新…' : update?.available ? `新版本 v${update.latestVersion} 可用` : checkedAt ? '当前已是最新版本' : '尚未检查更新'}</strong>
          <p>{update?.available ? `当前 v${currentVersion}，发布于 ${formatStamp(update.latest.published_at)}。${installable ? installKind === 'portable' ? '便携版会在退出后替换当前文件夹的程序并自动重启。' : '安装版会静默安装并自动重启。' : ''}` : checkedAt ? `上次检查：${new Date(checkedAt).toLocaleString('zh-CN', { hour12: false })}。` : '更新检查只读取 GitHub Releases 的公开版本信息。'}</p>
        </div>
        <Button disabled={checking || busy || !info} onClick={checkNow}><Icon name="refresh" size={14} />{checking ? '检查中…' : '检查更新'}</Button>
      </div>
      {error && <p className="inline-error" role="alert">{error}</p>}
      {update?.available && <div className="inline-actions">
        {installable
          ? <Button variant="primary" disabled={busy} onClick={install}><Icon name="download" size={14} />{installState.phase === 'failed' ? '重试更新' : '立即更新'}</Button>
          : <Button variant="primary" onClick={download}><Icon name="download" size={14} />{update.downloadLabel}</Button>}
        {installable && <Button disabled={busy} onClick={download}><Icon name="download" size={13} />{update.downloadLabel}</Button>}
        <Button disabled={busy} onClick={() => { void openExternal(update.releaseUrl).catch(failure => onError(errorMessage(failure))); }}><Icon name="link" size={13} />打开发布页面</Button>
      </div>}
      <InstallStatus state={installState} version={update?.latestVersion ?? currentVersion} bar />
      {installState.phase === 'failed' && <p className="inline-error" role="alert">{installState.error}</p>}
      {update?.available && installKind === 'development' && <p className="muted">开发构建不执行应用内安装，请下载安装程序验证发布流程。</p>}
    </section>

    <h3>{update?.available ? `v${update.latestVersion} 更新说明` : '当前版本更新说明'}</h3>
    {update?.available && latestNotes ? <MarkdownNotes text={latestNotes} /> : <LocalNotes releases={info?.releases ?? []} />}
    {info && <p className="about-links"><button type="button" onClick={() => { void openExternal(info.repository).catch(failure => onError(errorMessage(failure))); }}>项目仓库</button><span aria-hidden="true">·</span><button type="button" onClick={() => { void openExternal(info.releasesUrl).catch(failure => onError(errorMessage(failure))); }}>全部发布</button></p>}
  </Dialog>;
}
