import { useEffect, useRef, useState } from 'react';
import type { ButtonHTMLAttributes, CSSProperties, ReactNode } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { open } from '@tauri-apps/plugin-dialog';
import { Icon } from './icons';
import type { IconName } from './icons';
import { useDesktop } from './context';
import type { ClipItem, PanelKind, RouteId } from './types';
import { errorMessage, nativeRuntime } from './bridge';
export { Icon } from './icons';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> { variant?: 'primary' | 'secondary' | 'ghost' | 'danger' }
export function Button({ variant = 'secondary', className = '', children, ...props }: ButtonProps) {
  return <button type="button" className={`button ${variant} ${className}`} {...props}>{children}</button>;
}
interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> { icon: IconName; label: string; active?: boolean }
export function IconButton({ icon, label, active = false, className = '', ...props }: IconButtonProps) {
  return <button type="button" aria-label={label} title={label} className={`icon-button ${active ? 'active' : ''} ${className}`} {...props}><Icon name={icon} size={17} /></button>;
}
export function Dialog({ title, close, children }: { title: string; close(): void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); return () => ref.current?.close(); }, []);
  return <dialog ref={ref} className="app-dialog" aria-label={title} onCancel={event => { event.preventDefault(); close(); }}><div className="dialog-heading"><h2>{title}</h2><IconButton icon="close" label={`关闭${title}`} onClick={close} /></div><div className="dialog-body">{children}</div></dialog>;
}
export function WindowHeader({ openPanel, children }: { openPanel(kind: PanelKind): void; children?: ReactNode }) {
  const desktop = useDesktop();
  const status = desktop.state?.engine?.status;
  return <header className="window-header"><div className="brand" data-tauri-drag-region><span className="brand-symbol"><Icon name="audio" size={22} /></span><strong data-tauri-drag-region>随声麦</strong><span className="brand-en" data-tauri-drag-region>ONBODY MIC</span></div><div className="titlebar-drag" data-tauri-drag-region /><div className="header-actions">
    {desktop.dirty && <Button variant="ghost" onClick={() => { if (desktop.currentConfig) void desktop.perform('preset_save', { config: desktop.currentConfig }).catch(() => undefined); }}><Icon name="save" size={14} />保存预设</Button>}
    <label className="preset-picker"><Icon name="presets" size={15} /><select aria-label="选择预设" value={desktop.state?.activePreset?.presetId ?? ''} onChange={event => { if (event.target.value) void desktop.requestPreset(event.target.value).catch(failure => desktop.notify(errorMessage(failure))); }}><option value="">选择预设</option>{desktop.state?.presets.map(preset => <option key={preset.presetId} value={preset.presetId} disabled={preset.corrupt}>{preset.name}{preset.corrupt ? '（损坏）' : ''}</option>)}</select><Icon name="chevron" size={12} /></label>
    <div className="header-tools">{children}<IconButton icon="settings" label="设备与预设配置" onClick={() => openPanel('devices')} /></div><Button variant="danger" disabled={!status} className={status?.emergencyLatched ? 'latched' : ''} onClick={() => { void desktop.perform(status?.emergencyLatched ? 'unlock' : 'mute').catch(() => undefined); }}><Icon name={status?.emergencyLatched ? 'shield' : 'muted'} size={16} />{status?.emergencyLatched ? '解除锁存' : '全部静音'}</Button>
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
  return <article className={`route-card compact ${enabled ? 'is-on' : ''}`}><div className="route-heading"><span className="route-icon"><Icon name={info.icon} size={18} /></span><h3>{info.name}</h3><span className={`route-state ${enabled ? 'on' : ''}`}>{enabled ? '已开启' : '关闭'}</span></div><p className="route-source" title={source}>{source}</p>{route === 'app' && <button type="button" className="app-bind-link" disabled={!status || status.switches.appSendEnabled} onClick={pickApp}><Icon name="link" size={12} />{status?.appBinding ? '重新绑定应用' : '绑定应用…'}</button>}<div className="route-switch-line"><span>{info.action}</span><button type="button" role="switch" aria-label={info.action} aria-checked={enabled} disabled={!status || changing || status.emergencyLatched || (route === 'app' && !status.switches.extrasSendEnabled)} className={`toggle ${enabled ? 'checked' : ''}`} onClick={() => { void toggle().catch(() => undefined); }}><span /></button></div>
    {route === 'monitor' && <select className="monitor-select" aria-label="监听模式" disabled={!status} value={status?.monitorMode ?? 'ClipOnly'} onChange={event => { void desktop.perform('route', { kind: 'monitor', enabled, monitorMode: event.target.value }).catch(() => undefined); }}><option value="ClipOnly">仅音效</option><option value="FullEndpoint">实际虚拟麦</option></select>}
    <div className="gain-label"><span>{route === 'monitor' ? '监听音量' : '增益'}</span><span>{status ? Math.round(gain * 100) + '%' : '—'}</span></div><input type="range" className="gain-slider" aria-label={`${info.name}增益`} min="0" max="400" step="1" disabled={!status} value={Math.round(gain * 100)} style={{ '--fill': `${gain * 25}%` } as CSSProperties} onChange={event => { if (status) void desktop.perform('gains', { ...status.gains, [route === 'extras' ? 'clip' : route]: Number(event.target.value) / 100 }).catch(() => undefined); }} />
    {route !== 'monitor' && <div className="meter-line"><div className={`meter ${enabled ? 'active' : ''}`} aria-label={`${info.name}当前峰值`}><span style={{ width: level === null ? '0%' : `${Math.min(100, Math.max(0, level * 100))}%` }} /></div><span>{level === null ? '—' : level > 0 ? `${(20 * Math.log10(level)).toFixed(0)} dB` : '−∞'}</span></div>}
  </article>;
}

export function ClipGrid({ edit }: { edit(clip: ClipItem): void }) {
  const desktop = useDesktop();
  const engine = desktop.state?.engine;
  return <div className="clip-grid">{desktop.filteredClips.map(clip => <article key={clip.clipId} className={`sound-tile ${desktop.selectedClip?.clipId === clip.clipId ? 'selected' : ''} ${!clip.available ? 'missing' : ''}`}><div className="tile-top"><span className="tile-category">{engine?.groups.find(group => group.groupId === clip.groupId)?.name ?? '未分组'}</span><button type="button" className={`favorite-button ${clip.favorite ? 'favorited' : ''}`} aria-label={`${clip.favorite ? '取消收藏' : '收藏'}${clip.displayName}`} aria-pressed={clip.favorite} onClick={() => { void desktop.perform('clip_favorite', { clipId: clip.clipId, value: !clip.favorite }).catch(() => undefined); }}><Icon name="star" size={15} /></button></div><button type="button" className="tile-main" onClick={() => desktop.selectClip(clip.clipId)} onDoubleClick={() => { if (clip.available) void desktop.perform('play', { clipId: clip.clipId, mode: 'send' }).catch(() => undefined); }}><span className="tile-symbol"><Icon name="audio" size={25} /></span><strong title={clip.displayName}>{clip.displayName}</strong><span>{clip.available ? `${clip.format.toUpperCase()} · ${Math.round(clip.gain * 100)}% 增益` : '素材文件缺失'}</span></button><div className="tile-meta"><span>{(clip.durationMs / 1000).toFixed(1)}s</span><button type="button" className="shortcut-chip" onClick={() => { desktop.selectClip(clip.clipId); edit(clip); }}><kbd>{desktop.state?.settings.clipHotkeys[clip.clipId] ?? '未绑定'}</kbd></button></div><div className="tile-actions"><button type="button" disabled={!clip.available || engine?.status.emergencyLatched} onClick={() => { void desktop.perform('play', { clipId: clip.clipId, mode: 'preview' }).catch(() => undefined); }}><Icon name="play" size={13} />试听</button><button type="button" disabled={!clip.available || !engine?.status.switches.extrasSendEnabled || engine?.status.emergencyLatched} onClick={() => { void desktop.perform('play', { clipId: clip.clipId, mode: 'send' }).catch(() => undefined); }}><Icon name="send" size={13} />发送</button><button type="button" aria-label={`编辑${clip.displayName}`} onClick={() => edit(clip)}><Icon name="more" size={14} /></button></div></article>)}
    {desktop.filteredClips.length === 0 && <div className="empty-state"><Icon name={engine?.libraryError ? 'info' : 'music'} size={30} /><strong>{engine?.libraryError ? '音效库不可用' : desktop.query || desktop.favoritesOnly || desktop.groupId ? '没有匹配的音效' : '导入你的第一条音效'}</strong><span>{engine?.libraryError ?? '支持 WAV / MP3，原文件不会被修改。'}</span><ImportButton /></div>}
  </div>;
}

export function ImportButton() {
  const desktop = useDesktop();
  return <Button disabled={!nativeRuntime || !desktop.state?.engine || Boolean(desktop.state.engine.libraryError) || desktop.importing || Boolean(desktop.duplicatePath)} onClick={() => { void open({ multiple: true, directory: false, filters: [{ name: '短音效', extensions: ['wav', 'mp3'] }] }).then(paths => { if (paths) return desktop.importPaths(Array.isArray(paths) ? paths : [paths]); }).catch(failure => desktop.notify(errorMessage(failure))); }}><Icon name="plus" size={15} />{desktop.importing ? '正在导入…' : '导入音效'}</Button>;
}

export function PlaybackStrip() {
  const desktop = useDesktop();
  const status = desktop.state?.engine?.status;
  const active = status?.clip && desktop.state?.engine?.clips.find(clip => clip.clipId === status.clip?.clipId);
  return <div className="playback-strip"><span className="playback-art"><Icon name="audio" size={21} /></span><div className="playback-copy"><strong>{status?.clipLoading ? '正在加载音效' : active?.displayName ?? '音效待命'}</strong><span>{status?.clipLoading ? '停止或全部静音可取消旧请求' : status?.clip ? status.clip.mode === 'Preview' ? '仅本地试听 · 不向外发送' : '经附加音频路径发送' : '本地试听与对外发送清楚分开'}</span></div><span className="playback-state">{status?.clip ? status.clip.mode === 'Preview' ? '试听中' : '发送中' : '空闲'}</span><Button variant="ghost" disabled={!status || (!status.clip && !status.clipLoading)} onClick={() => { void desktop.perform('stop').catch(() => undefined); }}><Icon name="stop" size={14} />停止音效</Button></div>;
}
