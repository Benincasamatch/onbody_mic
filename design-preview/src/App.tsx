import { useEffect, useState } from 'react';
import { LAYOUTS } from './model';
import type { LayoutId } from './model';
import { usePreview } from './preview';
import { Button, Icon, PreviewDialog } from './ui';
import Workbench from './layouts/Workbench';
import Console from './layouts/Console';
import Soundboard from './layouts/Soundboard';

export default function App() {
  const demo = usePreview();
  const [layout, setLayout] = useState<LayoutId>(() => {
    const requested = new URLSearchParams(window.location.search).get('layout');
    return LAYOUTS.find(candidate => candidate.id === requested)?.id ?? 'soundboard';
  });

  useEffect(() => {
    const url = new URL(window.location.href);
    url.searchParams.set('layout', layout);
    window.history.replaceState(null, '', url);
    document.title = `随声麦 · ${LAYOUTS.find(candidate => candidate.id === layout)?.name} · 设计预览`;
  }, [layout]);

  useEffect(() => {
    function focusSearch(event: KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        const search = document.querySelector<HTMLInputElement>('[data-preview-search]');
        if (search) { event.preventDefault(); search.focus(); }
      }
    }
    window.addEventListener('keydown', focusSearch);
    return () => window.removeEventListener('keydown', focusSearch);
  }, []);

  function chooseLayout(next: LayoutId) {
    setLayout(next);
    demo.setSection('overview');
    demo.closePanel();
  }

  return <div className="preview-root" data-effects={demo.reducedEffects ? 'reduced' : 'glass'}>
    <div className="preview-bar">
      <div className="preview-intro"><span className="eyebrow">TERN · STENCIL / ONBODY</span><h2>音效台已采用 · 其他布局仅供参考</h2></div>
      <nav className="preview-tabs" aria-label="界面布局示意">{LAYOUTS.map(candidate => <button type="button" key={candidate.id} className={`preview-tab ${layout === candidate.id ? 'selected' : ''}`} aria-pressed={layout === candidate.id} onClick={() => chooseLayout(candidate.id)}><Icon name={candidate.icon} size={17} /><span><strong>{candidate.code} · {candidate.name}</strong><small>{candidate.description}</small></span></button>)}</nav>
      <div className="preview-tools"><Button variant="ghost" onClick={demo.toggleEffects} aria-pressed={!demo.reducedEffects} title="切换玻璃与不透明外观"><Icon name="sliders" size={15} /><span className="tool-text">{demo.reducedEffects ? '不透明' : '玻璃效果'}</span></Button><Button variant="ghost" onClick={demo.reset} title="重置所有示例状态"><Icon name="refresh" size={15} /><span className="tool-text">重置示例</span></Button></div>
    </div>
    {layout === 'workbench' ? <Workbench /> : layout === 'console' ? <Console /> : <Soundboard />}
    <PreviewDialog />
    {demo.notification && <div className="toast" role="status">{demo.notification}</div>}
  </div>;
}
