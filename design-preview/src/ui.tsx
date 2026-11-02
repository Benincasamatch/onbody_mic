import { useEffect, useRef } from 'react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Icon } from './icons';
import type { IconName } from './icons';
import { PRESETS, ROUTE_INFO } from './model';
import type { GroupId, PanelKind, RouteId } from './model';
import { usePreview } from './preview';
export { Icon } from './icons';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
}
export function Button({ variant = 'secondary', className = '', children, ...props }: ButtonProps) {
  return <button type="button" className={`button ${variant} ${className}`} {...props}>{children}</button>;
}

interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  icon: IconName;
  label: string;
  active?: boolean;
}
export function IconButton({ icon, label, active = false, className = '', ...props }: IconButtonProps) {
  return <button type="button" className={`icon-button ${active ? 'active' : ''} ${className}`} aria-label={label} title={label} {...props}><Icon name={icon} size={17} /></button>;
}

export function Panel({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <section className={`panel ${className}`}>{children}</section>;
}

export function WindowHeader({ children }: { children?: ReactNode }) {
  const demo = usePreview();
  return <header className="window-header">
    <div className="brand"><span className="brand-symbol"><Icon name="audio" size={21} /></span><strong>随声麦</strong><span className="brand-en">ONBODY MIC</span></div>
    <span className="window-preview-label">设计预览</span>
    <div className="header-actions">
      <label className="preset-picker"><Icon name="presets" size={15} /><select aria-label="选择示例预设" value={demo.presetId} onChange={event => demo.setPreset(event.target.value)}>{PRESETS.map(preset => <option key={preset.id} value={preset.id}>{preset.name}</option>)}</select><Icon name="chevron" size={13} /></label>
      <div className="header-tools">{children}</div>
      <Button variant="danger" className={demo.emergency ? 'latched' : ''} onClick={demo.toggleEmergency}><Icon name={demo.emergency ? 'shield' : 'muted'} size={16} />{demo.emergency ? '解除锁存' : '全部静音'}</Button>
    </div>
  </header>;
}

export function StatusFooter() {
  const demo = usePreview();
  const activeCount = Number(demo.enabled.mic) + Number(demo.enabled.app) + Number(demo.enabled.extras);
  return <footer className="status-footer">
    <span className={`footer-state ${demo.emergency ? 'error' : ''}`}><span className="status-dot" />{demo.emergency ? '模拟：静音已锁存' : activeCount ? `模拟：${activeCount} 路开关已开启` : '模拟：所有发送关闭'}</span>
    <span className="footer-preview">未连接真实音频引擎 · 示例内容不会发声或保存</span>
    <button type="button" className="footer-link" onClick={() => demo.openPanel('diagnostics')}><Icon name="activity" size={13} />诊断</button>
  </footer>;
}

export function PageHeading({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description?: string; actions?: ReactNode }) {
  return <div className="page-heading"><div>{eyebrow && <span className="eyebrow">{eyebrow}</span>}<h1>{title}</h1>{description && <p>{description}</p>}</div>{actions && <div className="inline-actions">{actions}</div>}</div>;
}

export function RouteCard({ route, compact = false }: { route: RouteId; compact?: boolean }) {
  const demo = usePreview();
  const info = ROUTE_INFO[route];
  const active = demo.enabled[route];
  const level = route === 'mic' ? 38 : route === 'app' ? 25 : route === 'monitor' ? 32 : 45;
  return <article className={`route-card ${compact ? 'compact' : ''} ${active ? 'is-on' : ''}`}>
    <div className="route-heading"><span className={`route-icon route-${route}`}><Icon name={info.icon} size={18} /></span><h3>{info.name}</h3><span className={`route-state ${active ? 'on' : ''}`}>{active ? '模拟开启' : '关闭'}</span></div>
    <p className="route-source" title={info.source}>{info.source}</p>
    <div className="route-switch-line"><span>{info.action}</span><button type="button" role="switch" aria-checked={active} aria-label={`${info.action}（示意）`} className={`toggle ${active ? 'checked' : ''}`} onClick={() => demo.toggleRoute(route)}><span /></button></div>
    {route === 'monitor' && <select className="monitor-select" aria-label="示例监听模式" value={demo.monitorMode} onChange={event => demo.setMonitorMode(event.target.value)}><option value="ClipOnly">仅音效</option><option value="FullEndpoint">实际虚拟麦</option></select>}
    <div className="gain-label"><span>{route === 'monitor' ? '监听音量' : '增益'}</span><span>{demo.gains[route]}<small>%</small></span></div>
    <input className="gain-slider" aria-label={`${info.name}增益（示意）`} type="range" min="0" max="400" step="1" value={demo.gains[route]} onChange={event => demo.setGain(route, Number(event.target.value))} style={{ '--fill': `${demo.gains[route] / 4}%` } as React.CSSProperties} />
    <div className="meter-line"><div className={`meter ${active ? 'active' : ''}`} aria-label="电平示意，不代表设备测量"><span style={{ width: active ? `${level}%` : '0%' }} /></div><span>{active ? '示例' : '—'}</span></div>
  </article>;
}

const groups: readonly { id: GroupId; name: string }[] = [
  { id: 'all', name: '全部' }, { id: 'favorites', name: '收藏' },
  { id: '游戏', name: '游戏' }, { id: '人声', name: '人声' }, { id: '氛围', name: '氛围' },
];

export function LibraryToolbar({ showGroups = true, compact = false }: { showGroups?: boolean; compact?: boolean }) {
  const demo = usePreview();
  return <div className={`library-toolbar ${compact ? 'compact' : ''}`}>
    <div className="library-search-row"><label className="search-field"><Icon name="search" size={16} /><input data-preview-search aria-label="搜索示例音效" placeholder="搜索音效名称…" value={demo.query} onChange={event => demo.setQuery(event.target.value)} /><kbd>Ctrl K</kbd></label><Button onClick={() => demo.openPanel('import')}><Icon name="plus" size={15} />导入音效</Button></div>
    {showGroups && <div className="library-filter-row"><div className="filter-tabs">{groups.map(group => <button type="button" key={group.id} className={demo.group === group.id ? 'selected' : ''} aria-pressed={demo.group === group.id} onClick={() => demo.setGroup(group.id)}>{group.id === 'favorites' && <Icon name="star" size={12} />}{group.name}</button>)}</div><span className="library-count">{demo.filteredClips.length} 条示例音效</span></div>}
  </div>;
}

export function ClipTable({ dense = false }: { dense?: boolean }) {
  const demo = usePreview();
  return <div className={`clip-table-wrap ${dense ? 'dense' : ''}`}><table className="clip-table"><thead><tr><th className="favorite-column"><span className="visually-hidden">收藏</span></th><th>音效名称</th><th>分类</th><th>时长</th><th>快捷键</th><th className="actions-column">操作</th></tr></thead><tbody>
    {demo.filteredClips.map(clip => <tr key={clip.id} className={demo.selectedClip.id === clip.id ? 'selected' : ''}>
      <td><button type="button" className={`favorite-button ${demo.favorites.has(clip.id) ? 'favorited' : ''}`} aria-label={`${demo.favorites.has(clip.id) ? '取消收藏' : '收藏'}${clip.name}`} aria-pressed={demo.favorites.has(clip.id)} onClick={() => demo.toggleFavorite(clip.id)}><Icon name="star" size={15} /></button></td>
      <td><button type="button" className="clip-name-button" onClick={() => demo.selectClip(clip.id)}><span className="file-symbol"><Icon name="audio" size={15} /></span><span><strong>{clip.name}</strong>{!dense && <small>{clip.subtitle}</small>}</span>{demo.playback?.clipId === clip.id && <span className="playing-marker">{demo.playback.mode === 'preview' ? '模拟试听' : '模拟发送'}</span>}</button></td>
      <td><span className="category-tag">{clip.group}</span></td><td className="duration">{clip.duration}s</td><td><kbd>{clip.shortcut}</kbd></td>
      <td><div className="clip-actions"><IconButton icon="play" label={`试听${clip.name}（示意）`} onClick={() => demo.playClip(clip.id, 'preview')} /><IconButton icon="send" label={`发送${clip.name}（示意）`} disabled={!demo.enabled.extras || demo.emergency} onClick={() => demo.playClip(clip.id, 'send')} /></div></td>
    </tr>)}
    {demo.filteredClips.length === 0 && <tr><td colSpan={6}><div className="empty-state"><Icon name="search" size={26} /><strong>没有匹配的示例音效</strong><span>换个关键词，或切换分类试试。</span></div></td></tr>}
  </tbody></table></div>;
}

export function ClipGrid() {
  const demo = usePreview();
  return <div className="clip-grid">{demo.filteredClips.map((clip, index) => <article key={clip.id} className={`sound-tile ${demo.selectedClip.id === clip.id ? 'selected' : ''}`}>
    <div className="tile-top"><span className="tile-category">{clip.group}</span><button type="button" className={`favorite-button ${demo.favorites.has(clip.id) ? 'favorited' : ''}`} aria-label={`${demo.favorites.has(clip.id) ? '取消收藏' : '收藏'}${clip.name}`} aria-pressed={demo.favorites.has(clip.id)} onClick={() => demo.toggleFavorite(clip.id)}><Icon name="star" size={15} /></button></div>
    <button type="button" className="tile-main" onClick={() => demo.selectClip(clip.id)}><span className={`tile-symbol tint-${index % 3}`}><Icon name={clip.group === '人声' ? 'mic' : clip.group === '氛围' ? 'headphones' : 'audio'} size={25} /></span><strong>{clip.name}</strong><span>{clip.subtitle}</span></button>
    <div className="tile-meta"><span>{clip.duration}s</span><kbd>{clip.shortcut}</kbd></div>
    <div className="tile-actions"><button type="button" onClick={() => demo.playClip(clip.id, 'preview')}><Icon name="play" size={13} />试听</button><button type="button" disabled={!demo.enabled.extras || demo.emergency} onClick={() => demo.playClip(clip.id, 'send')}><Icon name="send" size={13} />发送</button></div>
  </article>)}{demo.filteredClips.length === 0 && <div className="empty-state"><Icon name="search" size={26} /><strong>没有匹配的示例音效</strong><span>试试其他关键词或分类。</span></div>}</div>;
}

export function PlaybackStrip({ compact = false }: { compact?: boolean }) {
  const demo = usePreview();
  const playing = demo.playback && demo.clips.find(clip => clip.id === demo.playback?.clipId);
  return <div className={`playback-strip ${compact ? 'compact' : ''} ${playing ? 'has-playback' : ''}`}>
    <span className="playback-art"><Icon name={playing ? 'audio' : 'music'} size={21} /></span>
    <div className="playback-copy"><strong>{playing ? playing.name : '音效待命'}</strong><span>{playing ? `${demo.playback?.mode === 'preview' ? '本地试听' : '发送'}状态示意 · 无真实音频` : '选一条音效，先试听再发送'}</span></div>
    <div className="playback-state"><span className="status-dot" />{playing ? '仅模拟状态' : '空闲'}</div>
    <Button variant="ghost" disabled={!playing} onClick={demo.stopClip}><Icon name="stop" size={14} />停止音效</Button>
  </div>;
}

export function Inspector() {
  const demo = usePreview();
  return <aside className="inspector">
    <div className="inspector-section"><div className="inspector-title"><span className="section-label">音效详情</span><IconButton icon="more" label="查看示例音效详情" onClick={() => demo.openPanel('clip')} /></div><div className="inspector-art"><Icon name="audio" size={38} /><span>示例素材</span></div><h2>{demo.selectedClip.name}</h2><p>{demo.selectedClip.subtitle}</p><div className="detail-facts"><span>WAV · 示意</span><span>{demo.selectedClip.duration}s</span><span>{demo.selectedClip.group}</span></div><div className="inspector-buttons"><Button variant="primary" onClick={() => demo.playClip(demo.selectedClip.id, 'preview')}><Icon name="play" size={14} />本地试听</Button><IconButton icon="star" label="切换当前示例音效收藏" active={demo.favorites.has(demo.selectedClip.id)} onClick={() => demo.toggleFavorite(demo.selectedClip.id)} /></div></div>
    <div className="inspector-section"><div className="gain-label"><span>单条音效增益</span><span>{demo.clipGain}%</span></div><input type="range" className="gain-slider" aria-label="选中音效增益（示意）" min="0" max="400" step="1" value={demo.clipGain} onChange={event => demo.setClipGain(Number(event.target.value))} style={{ '--fill': `${demo.clipGain / 4}%` } as React.CSSProperties} /><div className="property-row"><span>播放热键</span><kbd>{demo.selectedClip.shortcut}</kbd></div><Button variant="ghost" className="wide-button" onClick={() => demo.openPanel('hotkeys')}><Icon name="keyboard" size={15} />管理热键<Icon name="link" size={13} /></Button></div>
    <div className="inspector-section"><span className="section-label">输出与安全</span><div className="device-summary"><span className="summary-icon"><Icon name="monitor" size={17} /></span><div><strong>虚拟音频线缆</strong><span>示例端点 · 未连接</span></div><Icon name="info" size={14} /></div><div className="device-summary"><span className="summary-icon"><Icon name="headphones" size={17} /></span><div><strong>本地耳机</strong><span>示例设备 · 未连接</span></div></div><Button variant="ghost" className="wide-button" onClick={() => demo.openPanel('devices')}><Icon name="settings" size={15} />设备设置<Icon name="link" size={13} /></Button></div>
    <div className="safety-note"><Icon name="shield" size={17} /><p>切换预设后，发送与监听默认关闭。试听不会送入虚拟麦克风。</p></div>
  </aside>;
}

const panelTitles: Record<PanelKind, string> = { presets: '预设管理', devices: '设备设置', hotkeys: '全局热键', diagnostics: '诊断与状态', import: '导入音效', clip: '音效详情' };
const DEVICE_ROWS = [
  { label: '真实麦克风', icon: 'mic' as const, value: 'USB 耳麦 · 示例设备' },
  { label: '虚拟输出', icon: 'monitor' as const, value: '虚拟音频线缆 · 示例端点' },
  { label: '虚拟录音端点', icon: 'app' as const, value: '线缆 Capture · 示例端点' },
  { label: '本地监听', icon: 'headphones' as const, value: '佩戴的耳机 · 示例设备' },
];

export function UtilityPane({ kind }: { kind: PanelKind }) {
  const demo = usePreview();
  if (kind === 'clip') return <Inspector />;
  return <div className={`utility-pane utility-${kind}`}>
    <PageHeading eyebrow="SETTINGS / PREVIEW" title={panelTitles[kind]} description="示例内容仅用于设计比较，不读取设备或写入配置。" />
    {kind === 'devices' && <><div className="utility-form">{DEVICE_ROWS.map(device => <label key={device.label} className="device-field"><span><Icon name={device.icon} size={17} />{device.label}</span><select aria-label={`${device.label}示意选择`} defaultValue={device.value}><option>{device.value}</option><option>暂不配置</option></select><small>未连接真实音频引擎</small></label>)}</div><div className="form-notice"><Icon name="shield" size={17} /><span>真实实现会明确校验端点；不会自动改用系统默认设备。</span></div><Button onClick={() => demo.notify('这里只演示设备设置界面，不枚举本机设备。')}><Icon name="refresh" size={15} />查看刷新反馈</Button></>}
    {kind === 'presets' && <><div className="preset-cards">{PRESETS.map(preset => <button type="button" className={`preset-card ${demo.presetId === preset.id ? 'selected' : ''}`} key={preset.id} onClick={() => demo.setPreset(preset.id)}><Icon name={preset.icon} size={23} /><strong>{preset.name}</strong><span>{preset.description}</span><small>{demo.presetId === preset.id ? '当前示例预设' : '点击切换示例'}</small></button>)}</div><div className="form-notice"><Icon name="info" size={17} /><span>切换仅改变预览状态。配置与预设的 Rust 迁移尚未开始。</span></div></>}
    {kind === 'hotkeys' && <><div className="hotkey-list">{[{ name: '全部静音', key: 'Ctrl Alt M' }, { name: '真麦发送开关', key: 'Ctrl Alt 1' }, { name: '附加音频开关', key: 'Ctrl Alt 2' }, { name: '停止音效', key: 'Ctrl Alt S' }, { name: '当前音效播放', key: demo.selectedClip.shortcut }].map(binding => <div key={binding.name}><span><Icon name="keyboard" size={16} />{binding.name}</span><kbd>{binding.key}</kbd></div>)}</div><div className="form-notice"><Icon name="info" size={17} /><span>这些是示例绑定，没有注册系统热键。Ctrl K 仅在此网页中聚焦搜索。</span></div></>}
    {kind === 'diagnostics' && <><div className="diagnostic-status"><Icon name="info" size={22} /><div><strong>设计预览未连接音频引擎</strong><p>没有采集、混音、设备检测或真实性能测量。</p></div></div><div className="metric-preview">{['当前峰值', '缓冲欠载', '设备故障'].map(metric => <div key={metric}><span>{metric}</span><strong>—</strong><small>无真实数据</small></div>)}</div><div className="form-notice"><Icon name="shield" size={17} /><span>正式界面将以引擎回执显示状态，不以按钮颜色判断远端是否收到。</span></div></>}
    {kind === 'import' && <><button type="button" className="import-area" onClick={() => demo.notify('这是导入区域的设计示意，不读取或导入本机文件。')}><span><Icon name="upload" size={30} /></span><strong>把短音效放到这里</strong><p>WAV / MP3 · 单条不超过 60 秒 / 32 MB</p><small>点击查看示意反馈，不会打开或读取本机文件</small></button><div className="form-notice"><Icon name="folder" size={17} /><span>导入时保留原文件，只管理素材副本；同内容重复导入应明确提示。</span></div></>}
  </div>;
}

export function PreviewDialog() {
  const demo = usePreview();
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (demo.panel && !dialog.open) dialog.showModal();
    else if (!demo.panel && dialog.open) dialog.close();
  }, [demo.panel]);
  return <dialog ref={ref} className="preview-dialog" aria-label={demo.panel ? panelTitles[demo.panel] : '设计预览面板'} onCancel={event => { event.preventDefault(); demo.closePanel(); }} onClick={event => { if (event.target === event.currentTarget) demo.closePanel(); }}>
    <div className="dialog-top"><span className="eyebrow">ONBODY / DESIGN PREVIEW</span><IconButton icon="close" label="关闭预览面板" onClick={demo.closePanel} /></div>
    {demo.panel && <UtilityPane kind={demo.panel} />}
  </dialog>;
}
