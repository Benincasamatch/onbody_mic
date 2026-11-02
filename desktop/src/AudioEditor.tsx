import { useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent, PointerEvent } from 'react';
import { Button, Dialog, Icon, Select } from './components';
import { useDesktop } from './context';
import { desktopCommand, errorMessage } from './bridge';
import { groupOptions } from './library';
import type { AudioInspection, AudioSelection, AudioSource, ImportResult } from './types';

function seconds(milliseconds: number) { return String(milliseconds / 1000); }
function clock(milliseconds: number) {
  const hundredths = Math.round(milliseconds / 10);
  const minutes = Math.floor(hundredths / 6000);
  return `${minutes}:${((hundredths - minutes * 6000) / 100).toFixed(2).padStart(5, '0')}`;
}

interface AudioEditorProps {
  source: AudioSource;
  initialInspection?: AudioInspection | null;
  displayName: string;
  groupId: string | null;
  reason?: string;
  remaining?: number;
  close(): void | Promise<void>;
  cancelQueue?: () => void;
  save(selection: AudioSelection, allowDuplicate: boolean): Promise<ImportResult>;
}

export function AudioEditorDialog({ source, initialInspection, displayName, groupId, reason, remaining, close, cancelQueue, save }: AudioEditorProps) {
  const desktop = useDesktop();
  const [inspection, setInspection] = useState<AudioInspection | null>(initialInspection ?? null);
  const [loading, setLoading] = useState(!initialInspection);
  const [startText, setStartText] = useState('0');
  const [endText, setEndText] = useState(initialInspection ? seconds(Math.min(initialInspection.durationMs, 60000)) : '');
  const [name, setName] = useState(`${displayName}（裁剪）`);
  const [group, setGroup] = useState(groupId ?? '');
  const [busy, setBusy] = useState<'preview' | 'stop' | 'save' | 'close' | null>(null);
  const [error, setError] = useState('');
  const [duplicate, setDuplicate] = useState<ImportResult | null>(null);
  const [previewRequested, setPreviewRequested] = useState(false);
  const [previewRange, setPreviewRange] = useState<{ startMs: number; endMs: number } | null>(null);
  const operation = useRef(false);
  const operationVersion = useRef(0);
  const mounted = useRef(true);
  const previewOwned = useRef(false);
  const waveform = useRef<HTMLDivElement>(null);
  const drag = useRef<'start' | 'end' | null>(null);
  const inspectSerial = useRef(0);
  const startMs = startText.trim() ? Number(startText) * 1000 : NaN;
  const parsedEndMs = endText.trim() ? Number(endText) * 1000 : NaN;
  const durationMs = inspection?.durationMs ?? 0;
  const endLimitMs = Math.min(durationMs, startMs + 60000);
  const endMs = parsedEndMs > endLimitMs && parsedEndMs - endLimitMs < 0.000001 ? endLimitMs : parsedEndMs;
  const sampleMs = inspection ? 1000 / inspection.sampleRate : 1;
  const valid = Boolean(inspection && Number.isFinite(startMs) && Number.isFinite(endMs) && startMs >= 0 && endMs <= durationMs && endMs > startMs && endMs - startMs <= 60000);
  const locked = Boolean(busy || duplicate || loading);
  const status = desktop.state?.engine?.status;
  const previewActive = previewRequested && Boolean(status?.clip?.mode === 'Preview' && status.clip.clipId.startsWith('editor-preview:'));
  const rangeError = inspection && !valid ? '起点、终点必须在源文件内，且选区时长须大于 0、不超过 60 秒。' : '';

  async function inspect() {
    const serial = ++inspectSerial.current;
    setLoading(true); setError('');
    try {
      const result = await desktopCommand<AudioInspection>('inspect_audio', source);
      if (!mounted.current || serial !== inspectSerial.current) return;
      setInspection(result); setStartText('0'); setEndText(seconds(Math.min(result.durationMs, 60000))); setDuplicate(null);
    } catch (failure) { if (mounted.current && serial === inspectSerial.current) setError(errorMessage(failure)); }
    finally { if (mounted.current && serial === inspectSerial.current) setLoading(false); }
  }

  useEffect(() => {
    mounted.current = true;
    if (!initialInspection) void inspect();
    return () => {
      mounted.current = false;
      inspectSerial.current++;
      if (previewOwned.current) {
        previewOwned.current = false;
        void desktopCommand('release_audio_preview').catch(failure => desktop.notify(errorMessage(failure)));
      }
    };
  }, []);

  const waveformPath = useMemo(() => {
    if (!inspection) return '';
    const bins = Math.floor(inspection.peaks.length / 2);
    const segments: string[] = [];
    for (let index = 0; index < bins; index++) {
      const x = (index + 0.5) * 1000 / bins;
      const min = Math.max(-1, Math.min(1, inspection.peaks[index * 2]));
      const max = Math.max(-1, Math.min(1, inspection.peaks[index * 2 + 1]));
      segments.push(`M${x.toFixed(2)},${(90 - max * 78).toFixed(2)}V${(90 - min * 78).toFixed(2)}`);
    }
    return segments.join('');
  }, [inspection]);

  async function release() {
    if (!previewOwned.current) return;
    await desktopCommand('release_audio_preview');
    previewOwned.current = false;
    if (mounted.current) { setPreviewRequested(false); setPreviewRange(null); }
  }

  async function finish(cancelAll = false) {
    if (operation.current && busy !== 'preview') return;
    ++operationVersion.current;
    operation.current = true; setBusy('close'); setError('');
    try {
      await release();
      if (cancelAll && cancelQueue) cancelQueue();
      else await close();
    } catch (failure) { if (mounted.current) setError(errorMessage(failure)); }
    finally { operation.current = false; if (mounted.current) setBusy(null); }
  }

  async function preview() {
    if (operation.current || !valid || !inspection || loading) return;
    const version = ++operationVersion.current;
    operation.current = true; setBusy('preview'); setError('');
    try {
      await release();
      if (!mounted.current || version !== operationVersion.current) return;
      previewOwned.current = true;
      const accepted = await desktopCommand<boolean>('preview_audio', { ...source, sourceVersion: inspection.sourceVersion, startMs, endMs });
      if (!mounted.current || version !== operationVersion.current) return;
      if (!accepted) throw new Error('后端未接受选区试听。');
      setPreviewRequested(true); setPreviewRange({ startMs, endMs });
      await desktop.refresh();
    } catch (failure) { if (mounted.current && version === operationVersion.current) setError(errorMessage(failure)); }
    finally { if (version === operationVersion.current) { operation.current = false; if (mounted.current) setBusy(null); } }
  }

  async function stop() {
    if (operation.current && busy !== 'preview') return;
    ++operationVersion.current;
    operation.current = true; setBusy('stop'); setError('');
    try { await release(); }
    catch (failure) { if (mounted.current) setError(errorMessage(failure)); }
    finally { operation.current = false; if (mounted.current) setBusy(null); }
  }

  async function reload() {
    if (operation.current || loading) return;
    operation.current = true; setBusy('stop'); setError('');
    try { await release(); setInspection(null); void inspect(); }
    catch (failure) { if (mounted.current) setError(errorMessage(failure)); }
    finally { operation.current = false; if (mounted.current) setBusy(null); }
  }

  async function submit(allowDuplicate = false) {
    if (operation.current || loading || !valid || !inspection || !name.trim()) return;
    operation.current = true; setBusy('save'); setError('');
    try {
      await release();
      const result = await save({ sourceVersion: inspection.sourceVersion, startMs, endMs, displayName: name.trim(), groupId: group || null }, allowDuplicate);
      if (result.outcome === 'Duplicate') { if (mounted.current) setDuplicate(result); }
      else desktop.notify('已创建独立的裁剪音效；源文件和原音效未修改。');
    } catch (failure) { if (mounted.current) setError(errorMessage(failure)); }
    finally { operation.current = false; if (mounted.current) setBusy(null); }
  }

  function setBoundary(boundary: 'start' | 'end', value: number) {
    const currentStart = Number.isFinite(startMs) ? Math.max(0, Math.min(durationMs - sampleMs, startMs)) : 0;
    const currentEnd = Number.isFinite(endMs) ? Math.max(sampleMs, Math.min(durationMs, endMs)) : durationMs;
    const clamped = boundary === 'start' ? Math.max(0, Math.min(currentEnd - sampleMs, value)) : Math.max(currentStart + sampleMs, Math.min(durationMs, value));
    if (boundary === 'start') setStartText(seconds(clamped)); else setEndText(seconds(clamped));
  }

  function pointerValue(event: PointerEvent<HTMLElement>) {
    const rect = waveform.current?.getBoundingClientRect();
    return rect && rect.width ? Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)) * durationMs : 0;
  }

  function handleKey(event: KeyboardEvent<HTMLButtonElement>, boundary: 'start' | 'end') {
    const current = boundary === 'start' ? startMs : endMs;
    const step = event.shiftKey ? 1000 : 10;
    let next: number;
    switch (event.key) {
      case 'ArrowLeft': case 'ArrowDown': next = current - step; break;
      case 'ArrowRight': case 'ArrowUp': next = current + step; break;
      case 'PageDown': next = current - 1000; break;
      case 'PageUp': next = current + 1000; break;
      case 'Home': next = 0; break;
      case 'End': next = durationMs; break;
      default: return;
    }
    event.preventDefault(); setBoundary(boundary, next);
  }

  function jump(event: PointerEvent<HTMLDivElement>) {
    if (locked || !inspection || event.target !== event.currentTarget) return;
    const length = valid ? endMs - startMs : Math.min(60000, durationMs);
    const start = Math.max(0, Math.min(durationMs - length, pointerValue(event)));
    setStartText(seconds(start)); setEndText(seconds(start + length));
  }

  const visualStart = Number.isFinite(startMs) && durationMs ? Math.max(0, Math.min(100, startMs / durationMs * 100)) : 0;
  const visualEnd = Number.isFinite(endMs) && durationMs ? Math.max(0, Math.min(100, endMs / durationMs * 100)) : 0;
  const groups = desktop.state?.engine?.groups ?? [];
  return <Dialog title={source.clipId ? '编辑音频 · 保存为新音效' : '裁剪后导入音效'} close={() => { void finish(); }} pending={Boolean(busy && busy !== 'preview')}>
    <p className="dialog-description">{reason || '拖动选区边界或填写秒数。保存始终创建新音效，不覆盖原条目，不修改源文件。'}</p>
    {'path' in source && <code className="path-text">{source.path}</code>}
    {remaining !== undefined && <p className="muted">导入队列已暂停，后面还有 {remaining} 个文件。关闭或跳过会继续下一项；取消剩余导入会清空未处理队列，已导入内容保留。</p>}
    {loading && <p role="status">正在读取真实音频并分析波形… 大文件可能需要一些时间。</p>}
    {inspection && <>
      <div className="audio-source-info"><span>{clock(durationMs)} · {inspection.sampleRate.toLocaleString()} Hz · {inspection.channels} 声道</span><span>选区 {valid ? ((endMs - startMs) / 1000).toFixed(3) : '—'} 秒 / 最多 60 秒</span></div>
      <div className="waveform-frame">
        <div ref={waveform} className={`audio-waveform ${locked ? 'locked' : ''}`} onPointerDown={jump} aria-label="音频波形；点击空白位置移动选区">
          <svg viewBox="0 0 1000 180" preserveAspectRatio="none" aria-hidden="true"><line x1="0" y1="90" x2="1000" y2="90" className="waveform-center" /><path d={waveformPath} className="waveform-peaks" /></svg>
          <div className={`waveform-selection ${valid ? '' : 'invalid'}`} style={{ left: `${visualStart}%`, width: `${Math.max(0, visualEnd - visualStart)}%` }} />
          {(['start', 'end'] as const).map(boundary => <button key={boundary} type="button" className={`waveform-handle ${boundary}`} style={{ left: `${boundary === 'start' ? visualStart : visualEnd}%` }} role="slider" aria-label={boundary === 'start' ? '选区起点' : '选区终点'} aria-valuemin={boundary === 'start' ? 0 : Math.max(0, (Number.isFinite(startMs) ? startMs : 0) / 1000)} aria-valuemax={boundary === 'end' ? durationMs / 1000 : (Number.isFinite(endMs) ? endMs : durationMs) / 1000} aria-valuenow={(Number.isFinite(boundary === 'start' ? startMs : endMs) ? boundary === 'start' ? startMs : endMs : 0) / 1000} aria-valuetext={`${boundary === 'start' ? startText : endText} 秒`} disabled={locked} onKeyDown={event => handleKey(event, boundary)} onPointerDown={event => { if (locked) return; event.preventDefault(); event.stopPropagation(); drag.current = boundary; event.currentTarget.setPointerCapture(event.pointerId); }} onPointerMove={event => { if (drag.current === boundary && !locked) setBoundary(boundary, pointerValue(event)); }} onPointerUp={event => { drag.current = null; if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId); }} onPointerCancel={() => { drag.current = null; }} onLostPointerCapture={() => { drag.current = null; }}><span>{boundary === 'start' ? '起' : '止'}</span></button>)}
        </div>
        <div className="waveform-axis" aria-label="波形时间轴">{Array.from({ length: 5 }, (_, index) => <span key={index}>{clock(durationMs * index / 4)}</span>)}</div>
      </div>
      <p className="form-notice waveform-help">点击波形移动整个选区；拖动起 / 止边界。边界获得焦点后方向键调整 0.01 秒，Shift + 方向键或 PageUp / PageDown 调整 1 秒，Home / End 到边界。</p>
      <div className="form-two-columns"><label className="form-field">起点（秒）<input type="number" aria-label="选区起点秒数" min="0" max={durationMs / 1000} step="0.001" value={startText} disabled={locked} onChange={event => setStartText(event.target.value)} aria-invalid={!valid || undefined} /></label><label className="form-field">终点（秒）<input type="number" aria-label="选区终点秒数" min="0" max={durationMs / 1000} step="0.001" value={endText} disabled={locked} onChange={event => setEndText(event.target.value)} aria-invalid={!valid || undefined} /></label></div>
      {rangeError && <p className="inline-error" role="alert">{rangeError}</p>}
      <div className="audio-preview-actions"><Button disabled={locked || !valid} onClick={() => { void preview(); }}><Icon name="headphones" size={14} />{busy === 'preview' ? '正在准备试听…' : '本地试听选区'}</Button><Button disabled={Boolean(busy && busy !== 'preview') || (!previewOwned.current && busy !== 'preview')} onClick={() => { void stop(); }}><Icon name="stop" size={14} />停止选区试听</Button><span role="status">{busy === 'preview' ? '后端正在准备选区；可停止或关闭来取消' : previewActive && previewRange ? `本地试听 ${clock(previewRange.startMs)}–${clock(previewRange.endMs)} · 不对外发送${previewRange.startMs !== startMs || previewRange.endMs !== endMs ? ' · 当前边界修改尚未试听' : ''}` : previewRequested ? '试听请求已提交；当前后端未显示选区播放' : '仅通过真实音频后端试听，不对外发送'}</span></div>
      <label className="form-field">新音效名称<input aria-label="裁剪音效名称" required value={name} disabled={locked} onChange={event => setName(event.target.value)} /></label>
      <label className="form-field">保存到分组<Select aria-label="裁剪音效分组" value={group} disabled={locked || remaining !== undefined} onChange={setGroup} options={[{ value: '', label: '未分组' }, ...groupOptions(groups)]} /></label>
    </>}
    {error && <p className="inline-error" role="alert">{error}</p>}
    {!loading && <Button disabled={Boolean(busy) || Boolean(duplicate)} onClick={() => { void reload(); }}>重新读取源文件与波形</Button>}
    {duplicate && <div className="audio-duplicate" role="status"><p>选区内容与「{duplicate.existingDuplicate?.displayName ?? duplicate.item.displayName}」重复。已有条目保持不变；创建副本会得到独立的新音效。</p><div className="inline-actions"><Button disabled={Boolean(busy)} onClick={() => setDuplicate(null)}>返回调整选区</Button><Button disabled={Boolean(busy)} onClick={() => { void finish(); }}>{remaining !== undefined ? '跳过此文件' : '放弃新副本'}</Button><Button variant="primary" disabled={Boolean(busy)} onClick={() => { void submit(true); }}>{busy === 'save' ? '正在创建…' : '仍创建新副本'}</Button></div></div>}
    <div className="dialog-actions">{cancelQueue && <Button disabled={Boolean(busy && busy !== 'preview')} onClick={() => { void finish(true); }}>取消剩余导入</Button>}<Button disabled={Boolean(busy && busy !== 'preview')} onClick={() => { void finish(); }}>{remaining !== undefined ? '跳过此文件并继续' : '取消'}</Button>{!duplicate && <Button variant="primary" disabled={Boolean(busy) || loading || !valid || !name.trim()} onClick={() => { void submit(); }}>{busy === 'save' ? '正在导出并导入…' : '保存为新音效'}</Button>}</div>
  </Dialog>;
}
