import { useState } from 'react';
import type { GroupId, PanelKind, RouteId } from '../model';
import { usePreview } from '../preview';
import { Button, ClipGrid, Icon, IconButton, LibraryToolbar, PageHeading, PlaybackStrip, RouteCard, StatusFooter, WindowHeader } from '../ui';
import './Soundboard.css';

const categories: readonly { id: GroupId; label: string; icon: 'audio' | 'star' | 'app' | 'mic' | 'headphones' }[] = [
  { id: 'all', label: '全部音效', icon: 'audio' },
  { id: 'favorites', label: '我的收藏', icon: 'star' },
  { id: '游戏', label: '游戏', icon: 'app' },
  { id: '人声', label: '人声', icon: 'mic' },
  { id: '氛围', label: '氛围', icon: 'headphones' },
];
const utilities: readonly { kind: PanelKind; label: string; icon: 'presets' | 'settings' | 'keyboard' | 'activity' }[] = [
  { kind: 'presets', label: '预设管理', icon: 'presets' },
  { kind: 'devices', label: '设备设置', icon: 'settings' },
  { kind: 'hotkeys', label: '全局热键', icon: 'keyboard' },
  { kind: 'diagnostics', label: '诊断与状态', icon: 'activity' },
];
const routes: readonly RouteId[] = ['mic', 'app', 'extras', 'monitor'];

export default function Soundboard() {
  const demo = usePreview();
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [mixerVisible, setMixerVisible] = useState(true);

  return <div className={`app-window soundboard ${sidebarCollapsed ? 'sidebar-collapsed' : ''} ${mixerVisible ? '' : 'mixer-hidden'}`}>
    <WindowHeader>
      <IconButton icon={sidebarCollapsed ? 'expand' : 'collapse'} label={sidebarCollapsed ? '展开音效分类' : '折叠音效分类'} onClick={() => setSidebarCollapsed(current => !current)} aria-expanded={!sidebarCollapsed} />
      <IconButton icon="sliders" label={mixerVisible ? '收起混音控件' : '显示混音控件'} onClick={() => setMixerVisible(current => !current)} active={mixerVisible} aria-expanded={mixerVisible} />
    </WindowHeader>
    <div className="soundboard-body">
      <aside className="soundboard-sidebar" aria-label="音效分类与设置">
        <div className="soundboard-collection"><span className="eyebrow">YOUR COLLECTION</span><h2>声音收藏夹</h2><p>把常用声音放在手边</p></div>
        <nav className="soundboard-categories" aria-label="示例音效分类">{categories.map(category => {
          const count = category.id === 'all' ? demo.clips.length : category.id === 'favorites' ? demo.favorites.size : demo.clips.filter(clip => clip.group === category.id).length;
          return <button type="button" key={category.id} className={demo.group === category.id ? 'selected' : ''} aria-pressed={demo.group === category.id} aria-label={`${category.label}，${count} 条示例音效`} title={category.label} onClick={() => demo.setGroup(category.id)}><Icon name={category.icon} size={17} /><span className="soundboard-nav-label">{category.label}</span><small>{count}</small></button>;
        })}</nav>
        <div className="soundboard-sidebar-divider" />
        <span className="soundboard-section-label">管理</span>
        <nav className="soundboard-utilities" aria-label="音效台设置">{utilities.map(utility => <button type="button" key={utility.kind} aria-label={utility.label} title={utility.label} onClick={() => demo.openPanel(utility.kind)}><Icon name={utility.icon} size={16} /><span className="soundboard-nav-label">{utility.label}</span></button>)}</nav>
        <div className="soundboard-rail-note"><Icon name="shield" size={16} /><p>只发送明确开启的声音。<br />本页全部为设计示例。</p></div>
      </aside>
      <main className="soundboard-main" aria-label="音效台">
        <PageHeading eyebrow="SOUNDBOARD / QUICK ACCESS" title={demo.group === 'favorites' ? '我的收藏' : demo.group === 'all' ? '让每个时刻都有声音' : `${demo.group}音效`} description="常用音效一触即达，试听与发送清楚分开。" actions={<IconButton icon="inspector" label="查看当前音效详情" onClick={() => demo.openPanel('clip')} />} />
        <div className="soundboard-library-top"><LibraryToolbar showGroups={false} /><div className="soundboard-library-meta"><span>{demo.filteredClips.length} 条示例音效</span><span><Icon name="grid" size={12} />快捷网格</span></div></div>
        <ClipGrid />
        <PlaybackStrip />
      </main>
      {mixerVisible && <aside className="soundboard-mixer" aria-label="发送与监听控件">
        <div className="soundboard-mixer-heading"><div><span className="eyebrow">MIXER</span><h2>发送与监听</h2></div><span className="pill">示例</span></div>
        <div className="soundboard-route-stack">{routes.map(route => <RouteCard key={route} route={route} compact />)}</div>
        <div className="soundboard-mixer-bottom"><Button variant="ghost" onClick={() => demo.openPanel('devices')}><Icon name="settings" size={14} />设备与监听设置<Icon name="link" size={12} /></Button><p>状态来自示例，不代表真实端点。</p></div>
      </aside>}
    </div>
    <StatusFooter />
  </div>;
}
