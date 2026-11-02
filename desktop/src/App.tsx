import { useEffect, useState } from 'react';
import { getCurrentWindow } from '@tauri-apps/api/window';
import { Button, ClipGrid, GroupNavigation, Icon, IconButton, ImportButton, PlaybackStrip, RouteCard, Select, WindowHeader } from './components';
import { AppPickerDialog, ClipDialog, DeviceDialog, DiagnosticsDialog, GroupsDialog, HotkeysDialog, PendingDialogs, PresetsDialog } from './dialogs';
import { useDesktop } from './context';
import { errorMessage, nativeRuntime } from './bridge';
import type { ClipItem, PanelKind, PresetConfig, RouteId } from './types';
import { groupPath } from './library';

const routes: readonly RouteId[] = ['mic', 'app', 'extras', 'monitor'];

export default function App() {
  const desktop = useDesktop();
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [mixerVisible, setMixerVisible] = useState(true);
  const [panel, setPanel] = useState<PanelKind | 'app' | 'groups' | null>(null);
  const [presetEditing, setPresetEditing] = useState<PresetConfig | null | undefined>(undefined);
  const [clipEditing, setClipEditing] = useState<ClipItem | null>(null);
  const engine = desktop.state?.engine;
  const clips = engine?.clips ?? [];
  const groups = engine?.groups ?? [];
  const status = engine?.status;
  const selectedGroup = groups.find(group => group.groupId === desktop.groupId);
  const title = desktop.favoritesOnly ? '我的收藏' : selectedGroup?.name ?? '音效台';
  const statusText = !engine ? desktop.state?.engineError || desktop.error ? '音频后端不可用' : '正在启动音频后端' : status?.emergencyLatched ? '全部静音已锁存' : status?.outputLost ? '虚拟输出已失效' : status?.clip?.mode === 'Preview' ? '仅本地试听' : status?.switches.anySendEnabled || status?.clip?.mode === 'Send' ? '本程序发送已开启' : status?.phase === 'Unconfigured' ? '尚未配置设备' : '本程序发送已关闭';

  useEffect(() => {
    if (!nativeRuntime) return;
    let stopped = false;
    let unsubscribe: (() => void) | undefined;
    void getCurrentWindow().onDragDropEvent(event => {
      if (event.payload.type === 'drop') void desktop.importPaths(event.payload.paths).catch(failure => desktop.notify(errorMessage(failure)));
    }).then(dispose => { if (stopped) dispose(); else unsubscribe = dispose; }).catch(failure => desktop.notify(errorMessage(failure)));
    return () => { stopped = true; unsubscribe?.(); };
  }, []);

  useEffect(() => {
    function search(event: globalThis.KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        const input = document.querySelector<HTMLInputElement>('[data-search-input]');
        if (input) { event.preventDefault(); input.focus(); }
      }
    }
    window.addEventListener('keydown', search);
    return () => window.removeEventListener('keydown', search);
  }, []);

  function openPanel(kind: PanelKind) { setPresetEditing(undefined); setPanel(kind); }

  return <div className={`app-window soundboard ${sidebarCollapsed ? 'sidebar-collapsed' : ''} ${mixerVisible ? '' : 'mixer-hidden'}`} data-effects={desktop.state?.settings.ui.reduceTransparency ? 'reduced' : 'glass'}>
    <WindowHeader openPanel={openPanel}><IconButton icon={sidebarCollapsed ? 'expand' : 'collapse'} label={sidebarCollapsed ? '展开分类导航' : '收起分类导航'} onClick={() => setSidebarCollapsed(current => !current)} aria-expanded={!sidebarCollapsed} /><IconButton icon="sliders" label={mixerVisible ? '收起发送与监听' : '显示发送与监听'} active={mixerVisible} onClick={() => setMixerVisible(current => !current)} aria-expanded={mixerVisible} /></WindowHeader>
    <div className="soundboard-body"><aside className="soundboard-sidebar" aria-label="分类与管理"><div className="sidebar-heading"><span className="eyebrow">YOUR COLLECTION</span><h2>声音收藏夹</h2><p>常用声音，随时在手边</p></div><nav className="category-nav" aria-label="音效分类"><button type="button" aria-label="全部音效" className={!desktop.favoritesOnly && !desktop.groupId ? 'selected' : ''} onClick={() => { desktop.setFavoritesOnly(false); desktop.setGroupId(null); }}><Icon name="audio" size={17} /><span>全部音效</span><small>{clips.length}</small></button><button type="button" aria-label="我的收藏" className={desktop.favoritesOnly ? 'selected' : ''} onClick={() => { desktop.setFavoritesOnly(true); desktop.setGroupId(null); }}><Icon name="star" size={17} /><span>我的收藏</span><small>{clips.filter(clip => clip.favorite).length}</small></button><GroupNavigation /></nav><button type="button" className="manage-groups" onClick={() => setPanel('groups')} disabled={!engine}><Icon name="plus" size={13} /><span>管理分组</span></button><div className="sidebar-divider" /><span className="sidebar-section-label">管理</span><nav className="utility-nav" aria-label="设置"><button type="button" onClick={() => openPanel('presets')}><Icon name="presets" size={17} /><span>预设管理</span></button><button type="button" onClick={() => openPanel('devices')}><Icon name="settings" size={17} /><span>设备设置</span></button><button type="button" onClick={() => openPanel('hotkeys')}><Icon name="keyboard" size={17} /><span>全局热键</span></button><button type="button" onClick={() => openPanel('diagnostics')}><Icon name="activity" size={17} /><span>诊断与状态</span></button></nav><div className="sidebar-note"><Icon name="shield" size={16} /><p>只有明确开启的声音<br />才会进入发送路径。</p></div></aside>
    <main className="soundboard-main"><div className="page-heading"><div><span className="eyebrow">SOUNDBOARD / QUICK ACCESS</span><h1>{title}</h1>{selectedGroup ? <nav className="group-breadcrumbs" aria-label="当前分组路径"><button type="button" onClick={() => desktop.setGroupId(null)}>全部音效</button>{groupPath(groups, selectedGroup.groupId).map(group => <span key={group.groupId}><span aria-hidden="true">/</span><button type="button" aria-current={group.groupId === selectedGroup.groupId ? 'page' : undefined} onClick={() => desktop.setGroupId(group.groupId)}>{group.name}</button></span>)}</nav> : <p>本地试听与对外发送清楚分开。</p>}</div><IconButton icon="sliders" label="切换透明材质" active={!desktop.state?.settings.ui.reduceTransparency} disabled={!desktop.state} onClick={() => { void desktop.perform('set_transparency', { reduced: !desktop.state?.settings.ui.reduceTransparency }).catch(() => undefined); }} /></div>
      {(desktop.error || desktop.state?.engineError) && <div className="connection-error" role="alert"><Icon name="info" size={19} /><div><strong>音频后端不可用</strong><p>{desktop.error ?? desktop.state?.engineError}</p></div><Button onClick={() => { void desktop.perform('restart_engine').catch(() => undefined); }} disabled={!nativeRuntime}>重启后端</Button></div>}
      {desktop.state?.settingsError && <div className="connection-error" role="alert"><Icon name="info" size={17} /><p>配置文件无法读取，当前使用未激活状态；原文件未自动修复。{errorMessage(desktop.state.settingsError)}</p></div>}
      {engine && !desktop.state?.activePreset && <div className="setup-banner"><Icon name="settings" size={18} /><span>先选择麦克风、虚拟输出和监听耳机，保存第一个预设。</span><Button variant="primary" onClick={() => { setPresetEditing(null); setPanel('devices'); }}>首次配置</Button></div>}
      <div className="library-toolbar"><div className="library-search-row"><label className="search-field"><Icon name="search" size={16} /><input data-search-input aria-label="搜索音效" placeholder="搜索音效名称…" value={desktop.query} onChange={event => desktop.setQuery(event.target.value)} /><kbd>Ctrl K</kbd></label><ImportButton /></div><div className="library-meta"><span>{desktop.filteredClips.length} 条音效{desktop.importing ? ' · 正在导入' : ''}</span><div className="inline-actions"><label className="sort-picker">排序<Select aria-label="音效排序" value={desktop.state?.settings.ui.clipSort ?? 'name'} disabled={!desktop.state} onChange={value => { if (desktop.state) void desktop.perform('settings_save', { settings: { ...desktop.state.settings, ui: { ...desktop.state.settings.ui, clipSort: value } } }).catch(() => undefined); }} options={[{ value: 'name', label: '名称' }, { value: 'importTime', label: '导入时间' }, { value: 'libraryOrder', label: '文件夹 / 导入顺序' }]} /></label><IconButton icon="refresh" label="刷新音效与状态" onClick={() => { void desktop.refresh(); }} /></div></div></div>
      <ClipGrid edit={clip => { setClipEditing(clip); setPanel('clip'); }} /><PlaybackStrip />
    </main>{mixerVisible && <aside className="soundboard-mixer" aria-label="发送与监听"><div className="mixer-heading"><span className="eyebrow">MIXER</span><h2>发送与监听</h2></div><div className="route-stack">{routes.map(route => <RouteCard key={route} route={route} pickApp={() => setPanel('app')} />)}</div><Button variant="ghost" className="wide-button" onClick={() => openPanel('devices')}><Icon name="settings" size={14} />设备与监听设置<Icon name="link" size={12} /></Button></aside>}</div>
    <footer className="status-footer"><span className={`footer-state ${status?.emergencyLatched || status?.outputLost || !engine ? 'warning' : ''}`}><span className="status-dot" />{statusText}</span>{desktop.dirty && <span className="dirty-note">预设未保存</span>}<span className="footer-note">状态不代表 KOOK 或远端已收到</span><button type="button" onClick={() => openPanel('diagnostics')}><Icon name="activity" size={13} />诊断</button></footer>
    {panel === 'devices' && <DeviceDialog close={() => setPanel(null)} existing={presetEditing === null ? null : presetEditing} />}
    {panel === 'presets' && <PresetsDialog close={() => setPanel(null)} create={() => { setPresetEditing(null); setPanel('devices'); }} edit={config => { setPresetEditing(config); setPanel('devices'); }} />}
    {panel === 'app' && <AppPickerDialog close={() => setPanel(null)} />}
    {panel === 'groups' && <GroupsDialog close={() => setPanel(null)} />}
    {panel === 'hotkeys' && <HotkeysDialog close={() => setPanel(null)} />}
    {panel === 'diagnostics' && <DiagnosticsDialog close={() => setPanel(null)} />}
    {panel === 'clip' && clipEditing && <ClipDialog key={clipEditing.clipId} clip={clipEditing} close={() => setPanel(null)} hotkeys={() => setPanel('hotkeys')} />}
    <PendingDialogs />{desktop.toast && <div className="toast" role="status">{desktop.toast}</div>}
  </div>;
}
