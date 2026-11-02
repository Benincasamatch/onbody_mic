import { useEffect, useRef, useState } from 'react';
import type { FormEvent, KeyboardEvent } from 'react';
import { revealItemInDir } from '@tauri-apps/plugin-opener';
import { Button, ConfirmationDialog, Dialog, Icon, IconButton, Select } from './components';
import { useDesktop } from './context';
import { desktopCommand, errorMessage } from './bridge';
import type { AppInstance, AudioDevice, ClipItem, HotkeySection, PresetApplyResult, PresetConfig, SettingsData } from './types';
import { descendantGroupIds, groupOptions, groupPath } from './library';

export function DeviceDialog({ close, existing }: { close(): void; existing?: PresetConfig | null }) {
  const desktop = useDesktop();
  const [devices, setDevices] = useState<AudioDevice[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [draft, setDraft] = useState<PresetConfig>(() => {
    if (existing === null && desktop.currentConfig) return { ...desktop.currentConfig, presetId: '', name: '' };
    if (existing) return existing;
    if (existing === undefined && desktop.currentConfig) return desktop.currentConfig;
    return { presetId: '', name: '', devices: { micDeviceId: '', virtualRenderDeviceId: '', virtualCaptureDeviceId: null, monitorDeviceId: '', application: null }, micGain: 1, appGain: 1, clipGain: 1, monitor: { mode: 'ClipOnly', gain: 1 }, clipLayout: [], libraryFavoritesOnly: false, libraryGroupId: null };
  });
  useEffect(() => {
    let cancelled = false;
    void desktopCommand<AudioDevice[]>('devices').then(items => { if (!cancelled) setDevices(items); }).catch(failure => { if (!cancelled) setError(errorMessage(failure)); }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);
  async function reload() { setDevices(await desktop.perform<AudioDevice[]>('devices')); }
  async function save(event: FormEvent) {
    event.preventDefault();
    if (draft.devices.virtualRenderDeviceId === draft.devices.monitorDeviceId) { setError('虚拟输出与本地监听不能选择同一个端点。'); return; }
    if (draft.monitor.mode === 'FullEndpoint' && !draft.devices.virtualCaptureDeviceId) { setError('实际虚拟麦监听需要明确选择 Capture 端点。'); return; }
    setSaving(true); setError('');
    try {
      const config = draft.presetId ? (await desktop.perform('preset_save', { config: draft }), draft) : await desktop.perform<PresetConfig>('preset_create', { config: draft });
      const result = await desktop.perform<PresetApplyResult>('preset_apply', { presetId: config.presetId });
      if (!result.accepted) throw new Error(result.rejection ?? '预设应用被拒绝');
      if (result.outputAttachRejection) desktop.notify(`配置已应用，输出尚不可用：${result.outputAttachRejection}`);
      close();
    } catch (failure) { setError(errorMessage(failure)); }
    finally { setSaving(false); }
  }
  const fields = [
    { key: 'micDeviceId' as const, label: '真实麦克风', flow: 'Capture' },
    { key: 'virtualRenderDeviceId' as const, label: '虚拟输出 · Render', flow: 'Render' },
    { key: 'virtualCaptureDeviceId' as const, label: '虚拟录音端点 · Capture', flow: 'Capture' },
    { key: 'monitorDeviceId' as const, label: '本地监听耳机', flow: 'Render' },
  ];
  return <Dialog title={draft.presetId ? '设备与预设配置' : '创建预设与首次配置'} close={close}><form onSubmit={event => { void save(event); }}><p className="dialog-description">选择真实端点。保存并应用后所有发送与监听关闭，不会更改系统默认设备或 KOOK 输入。</p><label className="form-field">预设名称<input aria-label="预设名称" required value={draft.name} onChange={event => setDraft(current => ({ ...current, name: event.target.value }))} /></label><div className="device-fields">{fields.map(field => {
    const selected = devices.find(device => device.id === draft.devices[field.key]);
    return <div key={field.key}><label className="form-field">{field.label}<Select aria-label={field.label} required={field.key !== 'virtualCaptureDeviceId'} disabled={loading} value={draft.devices[field.key] ?? ''} onChange={value => setDraft(current => ({ ...current, devices: { ...current.devices, [field.key]: value || null } }))} options={[{ value: '', label: loading ? '正在枚举设备…' : field.key === 'virtualCaptureDeviceId' ? '暂不配置（仅音效监听可用）' : '请选择设备' }, ...(draft.devices[field.key] && !selected ? [{ value: draft.devices[field.key] ?? '', label: '已保存端点当前不可用' }] : []), ...devices.filter(device => device.dataFlow === field.flow).map(device => ({ value: device.id, label: `${device.friendlyName} · ${device.state}${device.isDefault ? ' · 系统默认' : ''}` }))]} /></label>{selected?.state === 'Disabled' && <Button variant="ghost" onClick={() => { void desktop.perform<AudioDevice[]>('device_enable', { deviceId: selected.id }).then(setDevices).catch(failure => setError(errorMessage(failure))); }}><Icon name="power" size={13} />启用选中的禁用设备</Button>}</div>;
  })}</div><label className="form-field">监听偏好<Select aria-label="预设监听模式" value={draft.monitor.mode} onChange={value => setDraft(current => ({ ...current, monitor: { ...current.monitor, mode: value as 'ClipOnly' | 'FullEndpoint' } }))} options={[{ value: 'ClipOnly', label: '仅音效' }, { value: 'FullEndpoint', label: '实际虚拟麦' }]} /></label><p className="form-notice">应用按进程树捕获；切预设后仍需重新确认运行中的进程。程序不安装音频驱动。</p>{error && <p className="inline-error" role="alert">{error}</p>}<div className="dialog-actions"><Button variant="ghost" onClick={() => { void reload().catch(failure => setError(errorMessage(failure))); }}><Icon name="refresh" size={14} />刷新设备</Button><Button onClick={close}>取消</Button><Button type="submit" variant="primary" disabled={saving || loading}>{saving ? '正在保存…' : '保存并应用'}</Button></div></form></Dialog>;
}

export function AppPickerDialog({ close }: { close(): void }) {
  const desktop = useDesktop();
  const [apps, setApps] = useState<AppInstance[]>([]);
  const [selected, setSelected] = useState<AppInstance | null>(null);
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [binding, setBinding] = useState(false);
  const [error, setError] = useState('');
  const serial = useRef(0);
  const bindingGuard = useRef(false);
  async function reload() {
    const request = ++serial.current;
    setLoading(true); setError('');
    try {
      const items = await desktopCommand<AppInstance[]>('apps');
      if (request !== serial.current) return;
      setApps(items);
      setSelected(current => current ? items.find(app => app.processId === current.processId && app.executablePath === current.executablePath && app.startTimeTicks === current.startTimeTicks) ?? null : null);
    } catch (failure) { if (request === serial.current) setError(errorMessage(failure)); }
    finally { if (request === serial.current) setLoading(false); }
  }
  useEffect(() => { void reload(); return () => { serial.current++; }; }, []);
  async function bind() {
    if (!selected || loading || bindingGuard.current) return;
    bindingGuard.current = true; setBinding(true); setError('');
    try {
      await desktop.perform('bind_app', { processId: selected.processId, executablePath: selected.executablePath, displayName: selected.displayName, startTimeTicks: selected.startTimeTicks });
      close();
    } catch (failure) { setError(errorMessage(failure)); }
    finally { bindingGuard.current = false; setBinding(false); }
  }
  const visible = apps.filter(app => `${app.displayName} ${app.executablePath} ${app.processId}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  const categories: { id: AppInstance['category']; label: string }[] = [{ id: 'taskbar', label: '任务栏应用' }, { id: 'tray', label: '托盘应用' }, { id: 'background', label: '后台进程' }];
  return <Dialog title="绑定运行中的应用" close={close} pending={binding}>
    <p className="dialog-description">按任务栏、托盘、后台顺序列出可访问的应用。捕获包含选定应用及子进程，不是单个网页标签。不要把通话与音乐放在同一浏览器进程树。</p>
    <label className="search-field"><Icon name="search" size={16} /><input aria-label="搜索运行中的应用" placeholder="应用名称、路径或 PID…" value={query} disabled={binding} onChange={event => setQuery(event.target.value)} /></label>
    <div className="app-picker-list" aria-busy={loading}>{categories.map(category => {
      const items = visible.filter(app => app.category === category.id);
      return items.length > 0 && <section key={category.id} aria-label={category.label}><h3>{category.label}<small>{items.length}</small></h3>{items.map(app => <button key={`${app.processId}:${app.startTimeTicks}`} type="button" disabled={loading || binding} aria-pressed={selected?.processId === app.processId && selected.startTimeTicks === app.startTimeTicks} className={selected?.processId === app.processId && selected.startTimeTicks === app.startTimeTicks ? 'selected' : ''} onClick={() => setSelected(app)}><Icon name="app" size={19} /><span><strong>{app.displayName}</strong><small>{app.executablePath}</small></span><kbd>PID {app.processId}</kbd></button>)}</section>;
    })}{!visible.length && <p className="muted" role="status">{loading ? '正在枚举可访问的进程…' : query ? '没有匹配的应用。' : '没有可绑定进程，请启动播放器后刷新。'}</p>}</div>
    {error && <p className="inline-error" role="alert">{error}</p>}<div className="dialog-actions"><Button disabled={loading || binding} onClick={() => { void reload(); }}><Icon name="refresh" size={14} />{loading ? '正在刷新…' : '刷新'}</Button><Button disabled={binding} onClick={close}>取消</Button><Button variant="primary" disabled={!selected || loading || binding} onClick={() => { void bind(); }}>{binding ? '正在绑定…' : '确认绑定此进程'}</Button></div>
  </Dialog>;
}

export function PresetsDialog({ close, create, edit }: { close(): void; create(): void; edit(config: PresetConfig): void }) {
  const desktop = useDesktop();
  const [rename, setRename] = useState<{ id: string; name: string; duplicate: boolean } | null>(null);
  const [deleting, setDeleting] = useState<{ id: string; name: string } | null>(null);
  const [error, setError] = useState('');
  return <><Dialog title="预设管理" close={close}>
    <div className="dialog-section-heading"><p className="dialog-description">恢复的是设备、增益和监听偏好，不恢复发送状态。</p><>{desktop.dirty && <Button onClick={() => { if (desktop.currentConfig) void desktop.perform('preset_save', { config: desktop.currentConfig }).catch(failure => setError(errorMessage(failure))); }}><Icon name="save" size={14} />保存当前预设</Button>}<Button variant="primary" onClick={create}><Icon name="plus" size={14} />创建预设</Button></></div>
    <div className="preset-list">{desktop.state?.presets.map(preset => <div className={`preset-list-row ${preset.corrupt ? 'invalid' : ''}`} key={preset.presetId}><Icon name="presets" size={20} /><div className="preset-row-copy"><strong>{preset.name}</strong><small>{preset.corrupt ? preset.reason : desktop.state?.activePreset?.presetId === preset.presetId ? '当前预设' : preset.presetId}</small></div><div className="inline-actions"><IconButton icon="settings" label={`编辑预设${preset.name}`} disabled={preset.corrupt} onClick={() => { void desktop.perform<PresetConfig>('preset_load', { presetId: preset.presetId }).then(edit).catch(failure => setError(errorMessage(failure))); }} /><IconButton icon="copy" label={`复制预设${preset.name}`} disabled={preset.corrupt} onClick={() => setRename({ id: preset.presetId, name: `${preset.name} 副本`, duplicate: true })} /><IconButton icon="more" label={`重命名预设${preset.name}`} disabled={preset.corrupt} onClick={() => setRename({ id: preset.presetId, name: preset.name, duplicate: false })} /><IconButton icon="refresh" label={`恢复预设备份${preset.name}`} onClick={() => { void desktop.perform('preset_restore', { presetId: preset.presetId }).then(() => desktop.notify('已恢复经验证的备份；当前发送状态不会开启。')).catch(failure => setError(errorMessage(failure))); }} /><IconButton icon="delete" label={`删除预设${preset.name}`} onClick={() => setDeleting({ id: preset.presetId, name: preset.name })} /><Button disabled={preset.corrupt} onClick={() => { void desktop.requestPreset(preset.presetId).then(close).catch(failure => setError(errorMessage(failure))); }}>应用</Button></div></div>)}</div>
    {!desktop.state?.presets.length && <p className="muted">还没有预设，先创建并选择真实设备。</p>}
    {rename && <form className="inline-editor" onSubmit={event => { event.preventDefault(); void desktop.perform(rename.duplicate ? 'preset_duplicate' : 'preset_rename', { presetId: rename.id, name: rename.name }).then(() => setRename(null)).catch(failure => setError(errorMessage(failure))); }}><label className="form-field">{rename.duplicate ? '副本名称' : '新名称'}<input autoFocus required aria-label="预设新名称" value={rename.name} onChange={event => setRename(current => current ? { ...current, name: event.target.value } : null)} /></label><Button onClick={() => setRename(null)}>取消</Button><Button type="submit" variant="primary">保存</Button></form>}
    {error && <p className="inline-error" role="alert">{error}</p>}
  </Dialog>{deleting && <ConfirmationDialog title="确认删除预设" close={() => setDeleting(null)} confirm={async () => { await desktop.perform('preset_delete', { presetId: deleting.id }); if (rename?.id === deleting.id) setRename(null); }}><p>确定删除「{deleting.name}」？如果它正在使用，会先关闭并清除音频配置。原音效素材不会删除。</p></ConfirmationDialog>}</>;
}

export function ClipDialog({ clip, close, hotkeys }: { clip: ClipItem; close(): void; hotkeys(): void }) {
  const desktop = useDesktop();
  const [name, setName] = useState(clip.displayName);
  const [gain, setGain] = useState(Math.round(clip.gain * 100));
  const [group, setGroup] = useState(clip.groupId ?? '');
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function save(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try { if (name !== clip.displayName) await desktop.perform('clip_rename', { clipId: clip.clipId, name }); if (gain !== Math.round(clip.gain * 100)) await desktop.perform('clip_gain', { clipId: clip.clipId, gain: gain / 100 }); if (group !== (clip.groupId ?? '')) await desktop.perform('clip_group', { clipId: clip.clipId, groupId: group || null }); close(); }
    catch (failure) { setError(errorMessage(failure)); } finally { setBusy(false); }
  }
  return <><Dialog title="音效详情与管理" close={close}><form onSubmit={event => { void save(event); }}>
    <div className="clip-dialog-summary"><span className="clip-art"><Icon name="audio" size={38} /></span><div><strong>{clip.displayName}</strong><p>{clip.format.toUpperCase()} · {(clip.durationMs / 1000).toFixed(2)}s · {clip.available ? '素材可用' : '素材文件缺失'}</p><small>{clip.clipId}</small></div></div>
    <label className="form-field">音效名称<input aria-label="音效名称" required value={name} onChange={event => setName(event.target.value)} /></label>
    <div className="form-two-columns"><label className="form-field">单条音效增益 (%)<input aria-label="单条音效增益" type="number" min="0" max="400" step="1" value={gain} onChange={event => setGain(Number(event.target.value))} /></label><label className="form-field">分组<Select aria-label="音效分组" value={group} onChange={setGroup} options={[{ value: '', label: '未分组' }, ...groupOptions(desktop.state?.engine?.groups ?? [])]} /></label></div>
    <div className="inline-actions"><Button onClick={hotkeys}><Icon name="keyboard" size={14} />编辑播放热键</Button><Button onClick={() => { void desktop.perform<{ managedPath: string; originalPath: string | null }>('clip_path', { clipId: clip.clipId }).then(path => revealItemInDir(path.managedPath)).catch(failure => setError(errorMessage(failure))); }}><Icon name="folder" size={14} />打开文件位置</Button></div>
    <div className="dialog-actions"><Button variant="danger" disabled={busy} onClick={() => setDeleting(true)}><Icon name="delete" size={14} />删除副本</Button><Button onClick={close}>取消</Button><Button type="submit" variant="primary" disabled={busy}>{busy ? '正在保存…' : '保存修改'}</Button></div>
  </form>{error && <p className="inline-error" role="alert">{error}</p>}</Dialog>{deleting && <ConfirmationDialog title="确认删除音效副本" close={() => setDeleting(false)} confirm={async () => { await desktop.perform('clip_delete', { clipId: clip.clipId }); close(); }} prepare={async () => { const references = await desktopCommand<{ presetId: string; name: string }[]>('clip_references', { clipId: clip.clipId }); return references.length ? `以下预设引用此条目：${references.map(reference => reference.name).join('、')}。引用会显示为缺失，请稍后处理。` : '当前可读取预设没有此条目的布局引用。'; }}><p>确定删除「{clip.displayName}」？只删除受管素材副本，不修改原文件。尚未保存的详情修改不会写入。</p></ConfirmationDialog>}</>;
}

export function GroupsDialog({ close }: { close(): void }) {
  const desktop = useDesktop();
  const [name, setName] = useState('');
  const [edit, setEdit] = useState<{ id: string; name: string } | null>(null);
  const [deleting, setDeleting] = useState<{ id: string; name: string } | null>(null);
  const [error, setError] = useState('');
  const groups = desktop.state?.engine?.groups ?? [];
  return <><Dialog title="管理音效分组" close={close}>
    <form className="inline-editor" onSubmit={event => { event.preventDefault(); void desktop.perform('group_create', { name }).then(() => setName('')).catch(failure => setError(errorMessage(failure))); }}><label className="form-field">新分组名称<input required aria-label="新分组名称" value={name} onChange={event => setName(event.target.value)} /></label><Button type="submit" variant="primary"><Icon name="plus" size={14} />创建</Button></form>
    <div className="group-list">{groupOptions(groups).map(option => {
      const group = groups.find(group => group.groupId === option.value)!;
      const ids = descendantGroupIds(groups, group.groupId);
      return <div key={group.groupId} style={{ paddingLeft: `${(groupPath(groups, group.groupId).length - 1) * 18 + 4}px` }}><Icon name="folder" size={17} /><span title={option.label}>{group.name}</span><small>{desktop.state?.engine?.clips.filter(clip => clip.groupId && ids.has(clip.groupId)).length} 条音效</small><IconButton icon="more" label={`重命名分组${option.label}`} onClick={() => setEdit({ id: group.groupId, name: group.name })} /><IconButton icon="delete" label={`删除分组${option.label}`} onClick={() => setDeleting({ id: group.groupId, name: option.label })} /></div>;
    })}</div>
    {edit && <form className="inline-editor" onSubmit={event => { event.preventDefault(); void desktop.perform('group_rename', { groupId: edit.id, name: edit.name }).then(() => setEdit(null)).catch(failure => setError(errorMessage(failure))); }}><input aria-label="分组新名称" required value={edit.name} onChange={event => setEdit(current => current ? { ...current, name: event.target.value } : null)} /><Button onClick={() => setEdit(null)}>取消</Button><Button type="submit">保存</Button></form>}{error && <p className="inline-error" role="alert">{error}</p>}
  </Dialog>{deleting && <ConfirmationDialog title="确认删除分组" close={() => setDeleting(null)} confirm={async () => { const ids = descendantGroupIds(groups, deleting.id); await desktop.perform('group_delete', { groupId: deleting.id }); if (desktop.groupId && ids.has(desktop.groupId)) desktop.setGroupId(null); if (edit && ids.has(edit.id)) setEdit(null); }}><p>删除「{deleting.name}」及其所有子分组后，成员音效移到未分组，不会删除任何素材。</p></ConfirmationDialog>}</>;
}

const actionLabels: Record<string, string> = { emergencyMute: '全部静音', stopClip: '停止音效', toggleMicSend: '真麦发送开关', toggleExtrasSend: '附加音频开关', toggleMonitor: '监听开关', playClip: '播放音效', selectPreset: '选择预设' };
interface BindingRow { section: HotkeySection; key: string; label: string }
export function HotkeysDialog({ close }: { close(): void }) {
  const desktop = useDesktop();
  const [recording, setRecording] = useState<BindingRow | null>(null);
  const [gesture, setGesture] = useState('');
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const settings = desktop.state?.settings;
  useEffect(() => { return () => { void desktopCommand('hotkeys_suspend', { value: false }).catch(failure => desktop.notify(errorMessage(failure))); }; }, []);
  useEffect(() => {
    if (!recording) return;
    function escape(event: globalThis.KeyboardEvent) {
      if (event.key !== 'Escape') return;
      event.preventDefault(); event.stopImmediatePropagation();
      if (!saving) void cancelRecording().catch(failure => setError(errorMessage(failure)));
    }
    window.addEventListener('keydown', escape, true);
    return () => window.removeEventListener('keydown', escape, true);
  }, [recording, saving]);
  if (!settings) return <Dialog title="全局热键" close={close}><p>配置尚未加载。</p></Dialog>;
  const globalKeys = [...new Set([...(desktop.state?.engine?.hotkeyActions ?? []), ...Object.keys(settings.globalHotkeys)])].filter(key => key !== 'playClip' && key !== 'selectPreset' || key in settings.globalHotkeys);
  const rows: BindingRow[] = [
    ...globalKeys.map(key => ({ section: 'globalHotkeys' as const, key, label: actionLabels[key] ?? `未知操作 ${key}` })),
    ...[...new Set([...(desktop.state?.engine?.clips.map(clip => clip.clipId) ?? []), ...Object.keys(settings.clipHotkeys)])].map(key => ({ section: 'clipHotkeys' as const, key, label: desktop.state?.engine?.clips.find(clip => clip.clipId === key)?.displayName ?? `缺失音效 ${key}` })),
    ...[...new Set([...(desktop.state?.presets.map(preset => preset.presetId) ?? []), ...Object.keys(settings.presetHotkeys)])].map(key => ({ section: 'presetHotkeys' as const, key, label: desktop.state?.presets.find(preset => preset.presetId === key)?.name ?? `缺失预设 ${key}` })),
  ];
  async function begin(row: BindingRow) { await desktop.perform('hotkeys_suspend', { value: true }); setRecording(row); setGesture(settings?.[row.section][row.key] ?? ''); setError(''); }
  async function cancelRecording() { await desktop.perform('hotkeys_suspend', { value: false }); setRecording(null); }
  function capture(event: KeyboardEvent<HTMLInputElement>) {
    event.preventDefault(); event.stopPropagation();
    if (event.key === 'Escape') { void cancelRecording().catch(failure => setError(errorMessage(failure))); return; }
    if (['Control', 'Alt', 'Shift', 'Meta', 'AltGraph', 'OS', 'Super', 'Hyper', 'Fn', 'FnLock'].includes(event.key)) return;
    const modifiers = [event.ctrlKey ? 'Ctrl' : '', event.altKey ? 'Alt' : '', event.shiftKey ? 'Shift' : '', event.metaKey ? 'Win' : ''].filter(Boolean);
    const names: Record<string, string> = { ' ': 'Space', Backspace: 'Back', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', ';': 'Semicolon', ':': 'Semicolon', '=': 'Equal', '+': 'Equal', ',': 'Comma', '<': 'Comma', '-': 'Minus', '_': 'Minus', '.': 'Period', '>': 'Period', '/': 'Slash', '?': 'Slash', '`': 'Grave', '~': 'Grave', '[': 'BracketLeft', '{': 'BracketLeft', '\\': 'Backslash', '|': 'Backslash', ']': 'BracketRight', '}': 'BracketRight', "'": 'Quote', '"': 'Quote' };
    const keypad: Record<string, string> = { NumpadMultiply: 'NumMultiply', NumpadAdd: 'NumAdd', NumpadSubtract: 'NumSubtract', NumpadDecimal: 'NumDecimal', NumpadDivide: 'NumDivide' };
    const digit = /^Numpad([0-9])$/.exec(event.code);
    const key = digit && /^[0-9]$/.test(event.key) ? `Num${digit[1]}` : keypad[event.code] ?? (event.code.startsWith('Key') ? event.code.slice(3) : event.code.startsWith('Digit') ? event.code.slice(5) : names[event.key] ?? event.key);
    setGesture([...modifiers, key].join('+')); setError('');
  }
  async function saveBinding(row: BindingRow, value: string | null) {
    if (!settings) return;
    setSaving(true);
    try {
      const next: SettingsData = { ...settings, [row.section]: { ...settings[row.section], [row.key]: value } };
      await desktop.perform('settings_save', { settings: next });
      await desktop.perform('hotkeys_suspend', { value: false });
      setRecording(null);
    } catch (failure) { setError(errorMessage(failure)); }
    finally { setSaving(false); }
  }
  return <Dialog title="全局热键与绑定状态" close={close}><p className="dialog-description">支持单个按键或 Ctrl / Alt / Shift / Win 组合键。单独按修饰键不会录入；Esc 取消录制。绑定由系统实际注册，无效或冲突配置保留但不激活；录制时只保留全部静音的分派。</p><div className="hotkey-list">{rows.map(row => {
    const issue = desktop.state?.engine?.hotkeyIssues.find(issue => issue.section === row.section && issue.key === row.key);
    const action = row.section === 'clipHotkeys' ? 'playClip' : row.section === 'presetHotkeys' ? 'selectPreset' : row.key;
    const target = row.section === 'globalHotkeys' ? null : row.key;
    const registered = desktop.state?.engine?.hotkeys.some(binding => binding.action === action && binding.targetId === target) ?? false;
    return <div className={issue ? 'invalid' : ''} key={`${row.section}:${row.key}`}><span><Icon name="keyboard" size={16} /><strong>{row.label}</strong><small>{issue?.reason ?? (settings[row.section][row.key] ? desktop.state?.engine ? (registered ? '已注册并激活' : '未激活') : '后端不可用，未激活' : '未绑定')}</small></span><kbd>{settings[row.section][row.key] || '—'}</kbd><Button disabled={!desktop.state?.engine || saving} onClick={() => { void begin(row).catch(failure => setError(errorMessage(failure))); }}>录制</Button><IconButton icon="close" label={`解绑${row.label}`} disabled={saving} onClick={() => { void saveBinding(row, null); }} /></div>;
  })}</div>{recording && <div className="hotkey-recorder"><label className="form-field">正在录制：{recording.label}<input autoFocus aria-label="录制热键" readOnly placeholder="按下单键或组合键，Esc 取消" value={gesture} onKeyDown={capture} /></label><div className="inline-actions"><Button onClick={() => { void cancelRecording().catch(failure => setError(errorMessage(failure))); }}>取消录制</Button><Button variant="primary" disabled={!gesture || saving} onClick={() => { void saveBinding(recording, gesture); }}>保存绑定</Button></div></div>}{error && <p className="inline-error" role="alert">{error}</p>}</Dialog>;
}

export function DiagnosticsDialog({ close }: { close(): void }) {
  const desktop = useDesktop();
  const engine = desktop.state?.engine;
  return <Dialog title="诊断与数据位置" close={close}><div className="diagnostic-status"><Icon name={engine ? 'activity' : 'info'} size={22} /><div><strong>{engine ? `引擎状态：${engine.status.phase}` : '音频后端不可用'}</strong><p>{desktop.state?.engineError ?? desktop.error ?? '状态仅表示本程序，不能证明远端接收。'}</p></div></div>{engine && <><h3>最近采样窗口峰值</h3><div className="metric-cards">{Object.entries(engine.levels).map(([key, value]) => <div key={key}><span>{key}</span><strong>{value > 0 ? (20 * Math.log10(value)).toFixed(1) + ' dB' : '−∞'}</strong></div>)}</div><h3>累计诊断计数（不是当前电平）</h3><pre className="diagnostic-json">{JSON.stringify(engine.metrics, null, 2)}</pre><h3>路由问题</h3>{engine.issues.length ? engine.issues.map((issue, index) => <p className="inline-error" key={index}>{issue.route}：{issue.reason}</p>) : <p className="muted">未报告路由问题</p>}{engine.libraryError && <p className="inline-error">音效库：{engine.libraryError}</p>}</>}<h3>本地数据目录</h3><code className="path-text">{desktop.state?.root ?? '尚未加载'}</code>{desktop.state?.settingsError && <p className="inline-error">配置读取失败：{errorMessage(desktop.state.settingsError)}；原文件未自动修复。</p>}<div className="dialog-actions"><Button onClick={() => { void desktop.perform('attach_output').catch(() => undefined); }} disabled={!engine?.status.devices}>明确重接输出</Button><Button onClick={() => { void desktop.perform('restart_engine').catch(() => undefined); }}>重启音频后端</Button></div><p className="form-notice">恢复仅恢复配置，所有发送与监听保持关闭。没有音频上传、遥测或自动录音。</p></Dialog>;
}

export function PendingDialogs() {
  const desktop = useDesktop();
  return <>{desktop.duplicatePath && <Dialog title="发现重复音效" close={() => { void desktop.resolveDuplicate(false).catch(failure => desktop.notify(errorMessage(failure))); }}><p>音效库已有相同内容。跳过不会修改现有条目；继续将创建独立副本。</p><code className="path-text">{desktop.duplicatePath}</code><div className="dialog-actions"><Button onClick={() => { void desktop.resolveDuplicate(false).catch(failure => desktop.notify(errorMessage(failure))); }}>跳过此文件</Button><Button variant="primary" onClick={() => { void desktop.resolveDuplicate(true).catch(failure => desktop.notify(errorMessage(failure))); }}>仍创建新副本</Button></div></Dialog>}{desktop.leaveRequest && <Dialog title="预设有未保存修改" close={() => { void desktop.resolveLeave('cancel'); }}><p>增益、设备或音效库视图尚未保存。继续会丢弃当前预设的未保存修改。</p><div className="dialog-actions"><Button onClick={() => { void desktop.resolveLeave('cancel'); }}>取消</Button><Button onClick={() => { void desktop.resolveLeave('discard').catch(failure => desktop.notify(errorMessage(failure))); }}>不保存并继续</Button><Button variant="primary" onClick={() => { void desktop.resolveLeave('save').catch(failure => desktop.notify(errorMessage(failure))); }}>保存并继续</Button></div></Dialog>}{desktop.closeConfirmation && <Dialog title="关闭窗口后继续在托盘运行" close={() => { void desktop.closeToTray(false); }}><p>关闭窗口不会停止正在发送的音频。要彻底停止，请使用托盘退出；全部静音仍可通过热键操作。</p><div className="dialog-actions"><Button onClick={() => { void desktop.closeToTray(false); }}>取消关闭</Button><Button variant="primary" onClick={() => { void desktop.closeToTray(true).catch(failure => desktop.notify(errorMessage(failure))); }}>继续在托盘运行</Button></div></Dialog>}</>;
}
