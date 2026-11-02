import { useState } from 'react';
import { usePreview } from '../preview';
import {
  Button,
  ClipTable,
  Icon,
  IconButton,
  Inspector,
  LibraryToolbar,
  PageHeading,
  PlaybackStrip,
  RouteCard,
  StatusFooter,
  UtilityPane,
  WindowHeader,
} from '../ui';
import type { SectionId } from '../model';
import './Workbench.css';

const NAVIGATION = [
  { id: 'overview', label: '总览', icon: 'dashboard', description: '先确认路由，再选择要播放的音效。' },
  { id: 'library', label: '音效库', icon: 'folder', description: '筛选、收藏和试听示例素材，整理你的常用音效。' },
  { id: 'presets', label: '预设', icon: 'presets', description: '切换示例配置，查看不同场景的路由安排。' },
  { id: 'devices', label: '设备', icon: 'headphones', description: '查看示例输入、虚拟麦克风和本地监听设备。' },
  { id: 'hotkeys', label: '热键', icon: 'keyboard', description: '查看示例快捷键与常用操作的对应关系。' },
  { id: 'diagnostics', label: '诊断', icon: 'activity', description: '查看预览环境说明与路由状态，不连接真实音频。' },
] as const satisfies readonly { id: SectionId; label: string; icon: string; description: string }[];

export default function Workbench() {
  const { section, setSection, filteredClips, clips, openPanel } = usePreview();
  const [sidebarCollapsed, setSidebarCollapsed] = useState(
    () => typeof window !== 'undefined' && window.matchMedia('(max-width: 640px)').matches,
  );
  const [inspectorCollapsed, setInspectorCollapsed] = useState(false);
  const activePage = NAVIGATION.find((item) => item.id === section)!;
  const isLibraryPage = section === 'overview' || section === 'library';

  return (
    <div className={`app-window workbench${sidebarCollapsed ? ' workbench--sidebar-collapsed' : ''}${inspectorCollapsed ? ' workbench--inspector-collapsed' : ''}`}>
      <WindowHeader>
        <IconButton
          icon={sidebarCollapsed ? 'expand' : 'collapse'}
          label={sidebarCollapsed ? '展开导航' : '收起导航'}
          onClick={() => setSidebarCollapsed((collapsed) => !collapsed)}
          active={!sidebarCollapsed}
        />
        <IconButton
          className="workbench-inspector-toggle"
          icon="inspector"
          label={inspectorCollapsed ? '展开属性检查器' : '收起属性检查器'}
          onClick={() => setInspectorCollapsed((collapsed) => !collapsed)}
          active={!inspectorCollapsed}
        />
        <IconButton
          className="workbench-properties-dialog"
          icon="inspector"
          label="打开音效属性对话框"
          onClick={() => openPanel('clip')}
        />
      </WindowHeader>

      <div className="workbench-body">
        <aside className="workbench-sidebar" aria-label="工作区导航">
          <div className="workbench-sidebar-heading">
            <span className="section-label">工作区</span>
            <span className="workbench-sidebar-mark" aria-hidden="true">A</span>
          </div>
          <nav className="workbench-navigation" aria-label="主要页面">
            {NAVIGATION.map((item) => (
              <button
                key={item.id}
                type="button"
                className={`workbench-nav-item${section === item.id ? ' is-selected' : ''}`}
                aria-label={item.label}
                aria-current={section === item.id ? 'page' : undefined}
                title={sidebarCollapsed ? item.label : undefined}
                onClick={() => setSection(item.id)}
              >
                <Icon name={item.icon} size={18} />
                <span className="workbench-nav-label">{item.label}</span>
                {section === item.id && <span className="workbench-nav-indicator" aria-hidden="true" />}
              </button>
            ))}
          </nav>
          <div className="workbench-sidebar-note">
            <Icon name="dashboard" size={17} />
            <div>
              <span>工作台布局</span>
              <small>导航 · 路由 · 音效</small>
            </div>
          </div>
        </aside>

        <main className={`workbench-main${isLibraryPage ? ' workbench-main--library' : ' workbench-main--utility'}`} aria-label={activePage.label}>
          <PageHeading
            eyebrow={section === 'overview' ? '总览 / 示例工作区' : '工作区 / 示例页面'}
            title={section === 'overview' ? '工作台' : activePage.label}
            description={activePage.description}
            actions={section === 'overview' ? (
              <Button variant="ghost" onClick={() => openPanel('devices')}>
                <Icon name="settings" size={15} />
                <span>设备配置</span>
              </Button>
            ) : undefined}
          />

          {section === 'overview' && (
            <section className="workbench-routing" aria-label="示例音频路由">
              <RouteCard route="mic" compact />
              <RouteCard route="app" compact />
              <RouteCard route="extras" compact />
              <RouteCard route="monitor" compact />
            </section>
          )}

          {section === 'overview' || section === 'library' ? (
            <>
              <section className="workbench-library" aria-label="示例音效库">
                <div className="workbench-library-heading">
                  <div className="workbench-library-title">
                    <h2>{section === 'overview' ? '音效库' : '全部音效'}</h2>
                    <span>{filteredClips.length} / {clips.length} 段示例</span>
                  </div>
                  {section === 'overview' && (
                    <Button variant="ghost" onClick={() => setSection('library')}>
                      <span>查看全部</span>
                      <Icon name="chevron" size={14} />
                    </Button>
                  )}
                </div>
                <LibraryToolbar showGroups compact={section === 'overview'} />
                <div className="workbench-library-list">
                  <ClipTable dense={section === 'overview'} />
                </div>
              </section>
              <PlaybackStrip compact />
            </>
          ) : (
            <div className="workbench-utility-content">
              <UtilityPane kind={section} />
            </div>
          )}
        </main>

        <aside className="workbench-inspector-pane" aria-label="音效属性检查器" hidden={inspectorCollapsed}>
          <div className="workbench-inspector-heading">
            <div>
              <Icon name="inspector" size={16} />
              <span>属性检查器</span>
            </div>
            <IconButton
              icon="close"
              label="收起属性检查器"
              onClick={() => setInspectorCollapsed(true)}
            />
          </div>
          <div className="workbench-inspector-content">
            <Inspector />
          </div>
        </aside>
      </div>
      <StatusFooter />
    </div>
  );
}
