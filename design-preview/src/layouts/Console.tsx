import { usePreview } from '../preview';
import {
  WindowHeader,
  StatusFooter,
  Icon,
  IconButton,
  Button,
  PageHeading,
  Panel,
  RouteCard,
  LibraryToolbar,
  ClipTable,
  PlaybackStrip,
} from '../ui';
import './Console.css';

export default function Console() {
  const { clips, filteredClips, openPanel } = usePreview();

  return (
    <div className="app-window console-layout">
      <WindowHeader>
        <IconButton
          icon="settings"
          label="设备设置"
          onClick={() => openPanel('devices')}
        />
        <IconButton
          icon="activity"
          label="打开诊断面板"
          onClick={() => openPanel('diagnostics')}
        />
      </WindowHeader>

      <main className="console-body">
        <PageHeading
          eyebrow="布局 B · CONSOLE"
          title="单屏控制台"
          description="四路控制始终在眼前，音效列表保留完整空间。"
          actions={<span className="pill console-demo-label">离线布局预览</span>}
        />

        <section className="console-routes" aria-label="四路音频控制">
          <RouteCard route="mic" compact />
          <RouteCard route="app" compact />
          <RouteCard route="extras" compact />
          <RouteCard route="monitor" compact />
        </section>

        <Panel className="console-library">
          <div className="console-library-heading">
            <div className="console-library-title">
              <Icon name="folder" size={17} />
              <h2>音效库</h2>
              <span className="console-library-count">
                {filteredClips.length} / {clips.length} 个示例
              </span>
            </div>
            <nav className="console-utilities" aria-label="辅助操作">
              <Button variant="ghost" onClick={() => openPanel('presets')}>
                <Icon name="presets" size={15} />
                <span>预设</span>
              </Button>
              <Button variant="ghost" onClick={() => openPanel('devices')}>
                <Icon name="headphones" size={15} />
                <span>设备</span>
              </Button>
              <Button variant="ghost" onClick={() => openPanel('hotkeys')}>
                <Icon name="keyboard" size={15} />
                <span>快捷键</span>
              </Button>
              <Button variant="ghost" onClick={() => openPanel('diagnostics')}>
                <Icon name="activity" size={15} />
                <span>诊断</span>
              </Button>
            </nav>
          </div>
          <LibraryToolbar showGroups compact />
          <ClipTable dense />
        </Panel>

        <div className="console-playback">
          <PlaybackStrip compact />
        </div>
      </main>

      <StatusFooter />
    </div>
  );
}
