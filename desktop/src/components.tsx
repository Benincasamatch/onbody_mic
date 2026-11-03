import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { ButtonHTMLAttributes, CSSProperties, ReactNode } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { open } from '@tauri-apps/plugin-dialog';
import { Icon } from './icons';
import type { IconName } from './icons';
import { useDesktop } from './context';
import type { ClipItem, PanelKind, RouteId } from './types';
import { errorMessage, nativeRuntime } from './bridge';
import { Select } from './Select';
import { descendantGroupIds, groupPath, groupTree } from './library';
import type { GroupNode } from './library';
export { Select } from './Select';
export { Icon } from './icons';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> { variant?: 'primary' | 'secondary' | 'ghost' | 'danger' }
export function Button({ variant = 'secondary', className = '', children, ...props }: ButtonProps) {
  return <button type="button" className={`button ${variant} ${className}`} {...props}>{children}</button>;
}
interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> { icon: IconName; label: string; active?: boolean }
export function IconButton({ icon, label, active = false, className = '', ...props }: IconButtonProps) {
  return <button type="button" aria-label={label} title={label} className={`icon-button ${active ? 'active' : ''} ${className}`} {...props}><Icon name={icon} size={17} /></button>;
}
export function Dialog({ title, close, children, pending = false }: { title: string; close(): void; children: ReactNode; pending?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null);
  useLayoutEffect(() => {
    const dialog = ref.current;
    const previous = document.activeElement;
    dialog?.showModal();
    return () => {
      dialog?.close();
      if (previous instanceof HTMLElement && previous.isConnected) previous.focus({ preventScroll: true });
    };
  }, []);
  return <dialog ref={ref} className="app-dialog" aria-label={title} aria-busy={pending || undefined} onCancel={event => { event.preventDefault(); event.stopPropagation(); if (!pending) close(); }}><div className="dialog-heading"><h2>{title}</h2><IconButton icon="close" label={`关闭${title}`} disabled={pending} onClick={close} /></div><div className="dialog-body">{children}</div></dialog>;
}

export function ConfirmationDialog({ title, close, confirm, children, prepare }: { title: string; close(): void; confirm(): Promise<void>; children: ReactNode; prepare?: () => Promise<string> }) {
  const guard = useRef(false);
  const [pending, setPending] = useState(false);
  const [loading, setLoading] = useState(Boolean(prepare));
  const [detail, setDetail] = useState('');
  const [error, setError] = useState('');
  const [prepared, setPrepared] = useState(!prepare);
  useEffect(() => {
    if (!prepare) return;
    let stopped = false;
    void prepare().then(value => { if (!stopped) { setDetail(value); setPrepared(true); } }).catch(failure => { if (!stopped) setError(errorMessage(failure)); }).finally(() => { if (!stopped) setLoading(false); });
    return () => { stopped = true; };
  }, []);
  async function accept() {
    if (guard.current || loading || !prepared) return;
    guard.current = true; setPending(true); setError('');
    try { await confirm(); close(); }
    catch (failure) { setError(errorMessage(failure)); }
    finally { guard.current = false; setPending(false); }
  }
  async function retry() {
    if (!prepare || guard.current) return;
    guard.current = true; setLoading(true); setError('');
    try { setDetail(await prepare()); setPrepared(true); }
    catch (failure) { setError(errorMessage(failure)); }
    finally { guard.current = false; setLoading(false); }
  }
  function cancel() { if (!guard.current) close(); }
  return <Dialog title={title} close={cancel} pending={pending}><fieldset className="confirmation-content" disabled={pending}>{children}{loading && <p role="status">正在读取引用…</p>}{detail && <p>{detail}</p>}{error && <p className="inline-error" role="alert">{error}</p>}</fieldset><div className="dialog-actions">{!prepared && !loading && <Button onClick={() => { void retry(); }}>重试读取</Button>}<Button autoFocus disabled={pending} onClick={cancel}>取消删除</Button><Button variant="danger" disabled={pending || loading || !prepared} onClick={() => { void accept(); }}>{pending ? '正在删除…' : title}</Button></div></Dialog>;
}
export function WindowHeader({ children, onCreatePreset }: { children?: ReactNode; onCreatePreset?(): void }) {
  const desktop = useDesktop();
  const status = desktop.state?.engine?.status;
  return <header className="window-header"><div className="brand" data-tauri-drag-region><span className="brand-symbol"><Icon name="audio" size={22} /></span><strong data-tauri-drag-region>随声麦</strong><span className="brand-en" data-tauri-drag-region>ONBODY MIC</span></div><div className="titlebar-drag" data-tauri-drag-region /><div className="header-actions">
    {desktop.dirty && <Button variant="ghost" onClick={() => { if (desktop.currentConfig) void desktop.perform('preset_save', { config: desktop.currentConfig }).catch(() => undefined); }}><Icon name="save" size={14} />保存预设</Button>}
    <div className="preset-picker"><Icon name="presets" size={15} /><Select aria-label="选择预设" value={desktop.state?.activePreset?.presetId ?? ''} disabled={!desktop.state} onChange={value => { if (value) void desktop.requestPreset(value).catch(failure => desktop.notify(errorMessage(failure))); }} options={[{ value: '', label: '选择预设' }, ...(desktop.state?.presets.map(preset => ({ value: preset.presetId, label: `${preset.name}${preset.corrupt ? '（损坏）' : ''}`, disabled: preset.corrupt })) ?? [])]} /><IconButton icon="plus" label="新建预设" disabled={!desktop.state} onClick={() => onCreatePreset?.()} /></div>
    <div className="header-tools">{children}</div><Button variant="danger" disabled={!status} className={status?.emergencyLatched ? 'latched' : ''} onClick={() => { void desktop.perform(status?.emergencyLatched ? 'unlock' : 'mute').catch(() => undefined); }}><Icon name={status?.emergencyLatched ? 'shield' : 'muted'} size={16} />{status?.emergencyLatched ? '解除锁存' : '全部静音'}</Button>
    <div className="native-window-controls"><IconButton icon="minimize" label="最小化窗口" disabled={!nativeRuntime} onClick={() => { void getCurrentWindow().minimize().catch(failure => desktop.notify(errorMessage(failure))); }} /><IconButton icon="maximize" label="切换窗口最大化" disabled={!nativeRuntime} onClick={() => { void getCurrentWindow().toggleMaximize().catch(failure => desktop.notify(errorMessage(failure))); }} /><IconButton icon="close" label="关闭到托盘" disabled={!nativeRuntime} onClick={() => { void getCurrentWindow().close().catch(failure => desktop.notify(errorMessage(failure))); }} /></div>
  </div></header>;
}

const routeInfo: Record<RouteId, { name: string; action: string; icon: IconName }> = {
  mic: { name: '真实麦克风', action: '真麦发送', icon: 'mic' }, app: { name: '应用音频', action: '应用发送', icon: 'app' },
  extras: { name: '附加音频', action: '附加发送', icon: 'music' }, monitor: { name: '本地监听', action: '开启监听', icon: 'headphones' },
};
export function RouteCard({ route, pickApp }: { route: RouteId; pickApp(): void }) {
  const desktop = useDesktop();
  const [changing, setChanging] = useState(false);
  const engine = desktop.state?.engine;
  const status = engine?.status;
  const info = routeInfo[route];
  const enabled = Boolean(status && (route === 'mic' ? status.switches.micSendEnabled : route === 'app' ? status.switches.appSendEnabled : route === 'extras' ? status.switches.extrasSendEnabled : status.switches.monitorEnabled));
  const gain = status ? (route === 'extras' ? status.gains.clip : status.gains[route]) : 0;
  const level = engine ? (route === 'mic' ? engine.levels.mic : route === 'app' ? engine.levels.app : route === 'extras' ? engine.levels.clip : null) : null;
  const source = !status ? '音频后端未就绪' : route === 'mic' ? (status.devices?.micDeviceId ? '已配置麦克风' : '未配置麦克风') : route === 'app' ? status.appBinding?.description ?? '尚未确认运行中的应用' : route === 'extras' ? '应用与音效的发送总开关' : status.devices?.monitorDeviceId ? '已配置监听耳机' : '未配置监听耳机';
  async function toggle() {
    setChanging(true);
    try { await desktop.perform('route', { kind: route, enabled: !enabled, monitorMode: status?.monitorMode }); }
    finally { setChanging(false); }
  }
  return <article className={`route-card compact ${enabled ? 'is-on' : ''}`} data-route={route}><div className="route-heading"><span className="route-icon"><Icon name={info.icon} size={18} /></span><h3>{info.name}</h3><span className={`route-state ${enabled ? 'on' : ''}`}>{enabled ? '已开启' : '关闭'}</span></div><p className="route-source" title={source}>{source}</p>{route === 'app' && <button type="button" className="app-bind-link" disabled={!status || status.switches.appSendEnabled} onClick={pickApp}><Icon name="link" size={12} />{status?.appBinding ? '重新绑定应用' : '绑定应用…'}</button>}<div className="route-switch-line"><span>{info.action}</span><button type="button" role="switch" aria-label={info.action} aria-checked={enabled} disabled={!status || changing || status.emergencyLatched || (route === 'app' && !status.switches.extrasSendEnabled)} className={`toggle ${enabled ? 'checked' : ''}`} onClick={() => { void toggle().catch(() => undefined); }}><span /></button></div>
    {route === 'monitor' && <Select className="monitor-select" aria-label="监听模式" disabled={!status} value={status?.monitorMode ?? 'ClipOnly'} onChange={value => { void desktop.perform('route', { kind: 'monitor', enabled, monitorMode: value }).catch(() => undefined); }} options={[{ value: 'ClipOnly', label: '仅音效' }, { value: 'FullEndpoint', label: '实际虚拟麦' }]} />}
    <div className="gain-label"><span>{route === 'monitor' ? '监听音量' : '增益'}</span><span>{status ? Math.round(gain * 100) + '%' : '—'}</span></div><input type="range" className="gain-slider" aria-label={`${info.name}增益`} min="0" max="400" step="1" disabled={!status} value={Math.round(gain * 100)} onChange={event => { if (status) void desktop.perform('gains', { ...status.gains, [route === 'extras' ? 'clip' : route]: Number(event.target.value) / 100 }).catch(() => undefined); }} />
    {route !== 'monitor' && <div className="meter-line"><div className={`meter ${enabled ? 'active' : ''}`} aria-label={`${info.name}当前峰值`}><span style={{ width: level === null ? '0%' : `${Math.min(100, Math.max(0, level * 100))}%` }} /></div><span>{level === null ? '—' : level > 0 ? `${(20 * Math.log10(level)).toFixed(0)} dB` : '−∞'}</span></div>}
  </article>;
}

function ClipWaveform({ clip, playing }: { clip: ClipItem; playing: boolean }) {
  const peaks = clip.peaks ?? [];
  const bins = Math.min(120, peaks.length / 2);
  if (bins === 0) return <span className="tile-symbol"><Icon name="audio" size={25} /></span>;
  const segments: string[] = [];
  for (let index = 0; index < bins; index++) {
    const x = (index + 0.5) * 100 / bins;
    const min = Math.max(-1, Math.min(1, peaks[index * 2] ?? 0));
    const max = Math.max(-1, Math.min(1, peaks[index * 2 + 1] ?? 0));
    segments.push(`M${x.toFixed(2)},${(50 - max * 38).toFixed(2)}V${(50 - min * 38).toFixed(2)}`);
  }
  return <span className={`tile-waveform ${playing ? 'playing' : ''}`}><svg viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true"><path d={segments.join('')} /></svg>{playing && <span className="tile-waveform-progress" />}</span>;
}

export function ClipGrid({ edit, hotkeys }: { edit(clip: ClipItem): void; hotkeys(clip: ClipItem): void }) {
  const desktop = useDesktop();
  const engine = desktop.state?.engine;
  const playingId = engine?.status.clip?.mode === 'Preview' && engine.status.clip.clipId.startsWith('editor-preview:') ? null : engine?.status.clip?.clipId ?? null;
  const playingEpoch = engine?.status.clip?.playbackEpoch ?? 0;
  return <div className="clip-grid">{desktop.filteredClips.map(clip => { const playing = clip.clipId === playingId; return <article key={clip.clipId} className={`sound-tile ${desktop.selectedClip?.clipId === clip.clipId ? 'selected' : ''} ${!clip.available ? 'missing' : ''} ${playing ? 'playing' : ''}`}><div className="tile-top"><span className="tile-category" title={groupPath(engine?.groups ?? [], clip.groupId).map(group => group.name).join(' / ') || '未分组'}>{groupPath(engine?.groups ?? [], clip.groupId).map(group => group.name).join(' / ') || '未分组'}</span><button type="button" className={`favorite-button ${clip.favorite ? 'favorited' : ''}`} aria-label={`${clip.favorite ? '取消收藏' : '收藏'}${clip.displayName}`} aria-pressed={clip.favorite} onClick={() => { void desktop.perform('clip_favorite', { clipId: clip.clipId, value: !clip.favorite }).catch(() => undefined); }}><Icon name="star" size={15} /></button></div><button type="button" className="tile-main" onClick={() => { desktop.selectClip(clip.clipId); edit(clip); }} onDoubleClick={() => { if (clip.available) void desktop.perform('play', { clipId: clip.clipId, mode: 'send' }).catch(() => undefined); }}><ClipWaveform clip={clip} playing={playing} key={playing ? `playing-${playingEpoch}` : clip.clipId} /><strong title={clip.displayName}>{clip.displayName}</strong><span>{clip.available ? Math.round(clip.gain * 100) === 100 ? clip.format.toUpperCase() : `${clip.format.toUpperCase()} · ${Math.round(clip.gain * 100)}% 增益` : '素材文件缺失'}</span></button><div className="tile-meta"><span>{(clip.durationMs / 1000).toFixed(1)}s</span><button type="button" className="shortcut-chip" aria-label={`设置${clip.displayName}播放热键`} title="快速添加快捷键" onClick={() => { desktop.selectClip(clip.clipId); hotkeys(clip); }}>{desktop.state?.settings.clipHotkeys[clip.clipId] ? <kbd>{desktop.state.settings.clipHotkeys[clip.clipId]}</kbd> : <><Icon name="keyboard" size={12} /><span>快捷</span></>}</button></div><div className="tile-actions"><button type="button" disabled={!clip.available || engine?.status.emergencyLatched} onClick={() => { void desktop.perform('play', { clipId: clip.clipId, mode: 'preview' }).catch(() => undefined); }}><Icon name="play" size={13} />试听</button><button type="button" disabled={!clip.available || !engine?.status.switches.extrasSendEnabled || engine?.status.emergencyLatched} onClick={() => { void desktop.perform('play', { clipId: clip.clipId, mode: 'send' }).catch(() => undefined); }}><Icon name="send" size={13} />发送</button></div></article>; })}
    {desktop.filteredClips.length === 0 && <div className="empty-state"><Icon name={engine?.libraryError ? 'info' : 'music'} size={30} /><strong>{engine?.libraryError ? '音效库不可用' : desktop.query || desktop.favoritesOnly || desktop.groupId ? '没有匹配的音效' : '导入你的第一条音效'}</strong><span>{engine?.libraryError ?? '支持 WAV / MP3，原文件不会被修改。'}</span><ImportButton /></div>}
  </div>;
}

export function ImportButton() {
  const desktop = useDesktop();
  const disabled = !nativeRuntime || !desktop.state?.engine || Boolean(desktop.state.engine.libraryError) || desktop.importing || Boolean(desktop.duplicatePath) || Boolean(desktop.pendingAudioImport);
  function pick(directory: boolean) {
    void open({ multiple: true, directory, ...(!directory ? { filters: [{ name: '音频（长文件可裁剪）', extensions: ['wav', 'mp3'] }] } : {}) }).then(paths => { if (paths) return desktop.importPaths(Array.isArray(paths) ? paths : [paths]); }).catch(failure => desktop.notify(errorMessage(failure)));
  }
  return <div className="import-actions"><Button disabled={disabled} onClick={() => pick(false)}><Icon name="plus" size={15} />{desktop.importing ? '正在导入…' : '导入音效'}</Button><Button disabled={disabled} onClick={() => pick(true)}><Icon name="folder" size={15} />导入文件夹</Button></div>;
}

export function GroupNavigation() {
  const desktop = useDesktop();
  const groups = desktop.state?.engine?.groups ?? [];
  const clips = desktop.state?.engine?.clips ?? [];
  function branch(nodes: GroupNode[], depth: number): ReactNode {
    return nodes.map(node => {
      const ids = descendantGroupIds(groups, node.group.groupId);
      const path = groupPath(groups, node.group.groupId).map(group => group.name).join(' / ');
      return <li key={node.group.groupId}><button type="button" title={path} aria-label={path} aria-current={desktop.groupId === node.group.groupId ? 'page' : undefined} style={{ '--group-depth': depth } as CSSProperties} className={desktop.groupId === node.group.groupId ? 'selected' : ''} onClick={() => { desktop.setFavoritesOnly(false); desktop.setGroupId(node.group.groupId); }}><Icon name="folder" size={17} /><span>{node.group.name}</span><small>{clips.filter(clip => clip.groupId && ids.has(clip.groupId)).length}</small></button>{node.children.length > 0 && <ul>{branch(node.children, depth + 1)}</ul>}</li>;
    });
  }
  return <ul className="group-tree">{branch(groupTree(groups), 0)}</ul>;
}

export function PlaybackStrip() {
  const desktop = useDesktop();
  const status = desktop.state?.engine?.status;
  const active = status?.clip && desktop.state?.engine?.clips.find(clip => clip.clipId === status.clip?.clipId);
  const editorPreview = status?.clip?.mode === 'Preview' && status.clip.clipId.startsWith('editor-preview:');
  return <div className="playback-strip"><span className="playback-art"><Icon name="audio" size={21} /></span><div className="playback-copy"><strong>{status?.clipLoading ? '正在加载音效' : editorPreview ? '编辑器选区试听' : active?.displayName ?? '音效待命'}</strong></div><span className="playback-state">{status?.clip ? status.clip.mode === 'Preview' ? '试听中' : '发送中' : '空闲'}</span><Button variant="ghost" disabled={!status || (!status.clip && !status.clipLoading)} onClick={() => { void desktop.perform('stop').catch(() => undefined); }}><Icon name="stop" size={14} />停止音效</Button></div>;
}
