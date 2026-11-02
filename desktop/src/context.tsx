import { createContext, useContext, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { listen } from '@tauri-apps/api/event';
import type { ClipItem, DesktopState, PresetApplyResult, PresetConfig, RuntimeState } from './types';
import { desktopCommand, desktopState, errorMessage, nativeRuntime } from './bridge';

export interface LeaveRequest { type: 'switch' | 'exit'; presetId?: string }
export interface DesktopContextValue {
  state: DesktopState | null;
  error: string | null;
  toast: string;
  selectedClip: ClipItem | null;
  selectClip(id: string): void;
  query: string;
  setQuery(value: string): void;
  favoritesOnly: boolean;
  setFavoritesOnly(value: boolean): void;
  groupId: string | null;
  setGroupId(value: string | null): void;
  filteredClips: ClipItem[];
  importing: boolean;
  importPaths(paths: string[]): Promise<void>;
  duplicatePath: string | null;
  resolveDuplicate(allow: boolean): Promise<void>;
  dirty: boolean;
  currentConfig: PresetConfig | null;
  leaveRequest: LeaveRequest | null;
  resolveLeave(choice: 'save' | 'discard' | 'cancel'): Promise<void>;
  requestPreset(id: string): Promise<void>;
  closeConfirmation: boolean;
  closeToTray(keep: boolean): Promise<void>;
  perform<T>(op: string, args?: object): Promise<T>;
  refresh(): Promise<void>;
  notify(message: string): void;
}

const DesktopContext = createContext<DesktopContextValue | null>(null);

export function configMatches(left: PresetConfig, right: PresetConfig) {
  return left.presetId === right.presetId && left.name === right.name &&
    left.devices.micDeviceId === right.devices.micDeviceId && left.devices.virtualRenderDeviceId === right.devices.virtualRenderDeviceId &&
    left.devices.virtualCaptureDeviceId === right.devices.virtualCaptureDeviceId && left.devices.monitorDeviceId === right.devices.monitorDeviceId &&
    left.devices.application?.executablePath === right.devices.application?.executablePath && left.devices.application?.displayName === right.devices.application?.displayName &&
    Math.abs(left.micGain - right.micGain) < 0.000001 && Math.abs(left.appGain - right.appGain) < 0.000001 && Math.abs(left.clipGain - right.clipGain) < 0.000001 &&
    left.monitor.mode === right.monitor.mode && Math.abs(left.monitor.gain - right.monitor.gain) < 0.000001 &&
    left.libraryFavoritesOnly === right.libraryFavoritesOnly && left.libraryGroupId === right.libraryGroupId &&
    left.clipLayout.length === right.clipLayout.length && left.clipLayout.every((id, index) => id === right.clipLayout[index]);
}

export function DesktopProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<DesktopState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [toast, setToast] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [favoritesOnly, setFavoritesOnly] = useState(false);
  const [groupId, setGroupId] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);
  const [duplicatePath, setDuplicatePath] = useState<string | null>(null);
  const [leaveRequest, setLeaveRequest] = useState<LeaveRequest | null>(null);
  const [closeConfirmation, setCloseConfirmation] = useState(false);
  const mounted = useRef(true);
  const importingRef = useRef(false);
  const refreshSerial = useRef(0);
  const presetView = useRef<string | null>(null);
  const importQueue = useRef<string[]>([]);
  const dirtyRef = useRef(false);
  const configRef = useRef<PresetConfig | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; refreshSerial.current++; };
  }, []);

  async function refresh() {
    const serial = ++refreshSerial.current;
    try {
      const next = await desktopState();
      if (!mounted.current || serial !== refreshSerial.current) return;
      setState(current => {
        if (current?.engine && next.engine && current.engine.revision > next.engine.revision) {
          next.engine = { ...next.engine, revision: current.engine.revision, status: current.engine.status, levels: current.engine.levels, metrics: current.engine.metrics, issues: current.engine.issues };
        }
        return next;
      });
      setError(null);
    } catch (failure) { setError(errorMessage(failure)); }
  }

  async function perform<T>(op: string, args: object = {}): Promise<T> {
    try {
      const result = await desktopCommand<T>(op, args);
      if (['preset_apply', 'preset_create', 'preset_save', 'preset_rename', 'preset_duplicate', 'preset_delete', 'preset_restore', 'settings_save', 'set_transparency', 'restart_engine', 'import_clip', 'clip_delete', 'clip_rename', 'clip_group', 'group_create', 'group_rename', 'group_delete', 'clip_favorite', 'clip_gain'].includes(op)) await refresh();
      return result;
    } catch (failure) {
      setToast(errorMessage(failure));
      throw failure;
    }
  }

  useEffect(() => {
    if (!nativeRuntime) { setError('请通过桌面程序打开。此浏览器页面不会连接、模拟或播放音频。'); return; }
    let disposed = false;
    const unlisteners: (() => void)[] = [];
    async function start() {
      for (const [event, handler] of [
        ['engine_state', (payload: RuntimeState) => setState(current => current?.engine && payload.revision >= current.engine.revision ? { ...current, engine: { ...current.engine, ...payload } } : current)],
        ['data_changed', () => { void refresh(); }],
        ['engine_notice', (payload: { kind: string; message: string }) => setToast(payload.message)],
        ['engine_stopped', (payload: { message: string }) => { setState(current => current ? { ...current, engine: null, engineError: payload.message } : current); }],
        ['request_exit', () => { if (dirtyRef.current) setLeaveRequest({ type: 'exit' }); else void desktopCommand('exit_app').catch(failure => setToast(errorMessage(failure))); }],
        ['confirm_close_to_tray', () => setCloseConfirmation(true)],
      ] as const) {
        const unsubscribe = await listen(event, message => { if (!disposed) (handler as (payload: unknown) => void)(message.payload); });
        if (disposed) unsubscribe(); else unlisteners.push(unsubscribe);
      }
      if (!disposed) await refresh();
    }
    void start().catch(failure => setError(errorMessage(failure)));
    return () => { disposed = true; for (const unsubscribe of unlisteners) unsubscribe(); };
  }, []);

  useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(''), 5500);
    return () => window.clearTimeout(timer);
  }, [toast]);

  useEffect(() => {
    const preset = state?.activePreset;
    if (preset && presetView.current !== preset.presetId) {
      presetView.current = preset.presetId;
      setFavoritesOnly(preset.libraryFavoritesOnly);
      setGroupId(preset.libraryGroupId);
    } else if (!preset) { presetView.current = null; }
  }, [state?.activePreset?.presetId]);

  const clips = state?.engine?.clips ?? [];
  const normalized = query.trim().toLocaleLowerCase();
  const filteredClips = clips.filter(clip => (!normalized || clip.displayName.toLocaleLowerCase().includes(normalized)) && (!favoritesOnly || clip.favorite) && (!groupId || clip.groupId === groupId))
    .sort((left, right) => state?.settings.ui.clipSort === 'importTime' ? right.importUnixMs - left.importUnixMs : left.displayName.localeCompare(right.displayName));
  const selectedClip = clips.find(clip => clip.clipId === selectedId) ?? clips[0] ?? null;
  const saved = state?.activePreset;
  const status = state?.engine?.status;
  const currentConfig: PresetConfig | null = saved ? {
    ...saved,
    devices: status?.devices ?? saved.devices,
    micGain: status?.gains.mic ?? saved.micGain,
    appGain: status?.gains.app ?? saved.appGain,
    clipGain: status?.gains.clip ?? saved.clipGain,
    monitor: { mode: status?.monitorMode ?? saved.monitor.mode, gain: status?.gains.monitor ?? saved.monitor.gain },
    libraryFavoritesOnly: favoritesOnly, libraryGroupId: groupId,
  } : null;
  const dirty = Boolean(saved && currentConfig && !configMatches(saved, currentConfig));
  configRef.current = currentConfig;
  dirtyRef.current = dirty;

  useEffect(() => {
    if (nativeRuntime) void desktopCommand('set_dirty', { value: dirty }).catch(failure => setToast(errorMessage(failure)));
  }, [dirty]);

  async function requestPreset(id: string) {
    if (dirty) { setLeaveRequest({ type: 'switch', presetId: id }); return; }
    const result = await perform<PresetApplyResult>('preset_apply', { presetId: id });
    if (!result.accepted) throw new Error(result.rejection ?? '预设应用被拒绝');
    if (result.outputAttachRejection) setToast(`预设已应用但输出不可用：${result.outputAttachRejection}`);
  }

  async function resolveLeave(choice: 'save' | 'discard' | 'cancel') {
    if (choice === 'cancel') { setLeaveRequest(null); return; }
    const request = leaveRequest;
    if (!request) return;
    if (choice === 'save') {
      if (!configRef.current) throw new Error('没有可保存的活动预设');
      await perform('preset_save', { config: configRef.current });
    }
    setLeaveRequest(null);
    if (request.type === 'exit') await perform('exit_app');
    else if (request.presetId) await perform('preset_apply', { presetId: request.presetId });
  }

  async function drainImports() {
    importingRef.current = true;
    setImporting(true);
    try {
      while (importQueue.current.length) {
        const path = importQueue.current.shift()!;
        const result = await perform<{ outcome: 'Imported' | 'Duplicate'; item: ClipItem }>('import_clip', { path });
        if (result.outcome === 'Duplicate') { setDuplicatePath(path); return; }
        setSelectedId(result.item.clipId);
      }
    } finally { importingRef.current = false; if (mounted.current) setImporting(false); }
  }
  async function importPaths(paths: string[]) {
    if (importingRef.current || duplicatePath) throw new Error('请先处理当前导入请求');
    importQueue.current.push(...paths);
    await drainImports();
  }
  async function resolveDuplicate(allow: boolean) {
    const path = duplicatePath;
    if (!path) return;
    if (allow) {
      setImporting(true);
      importingRef.current = true;
      try {
        const result = await perform<{ item: ClipItem }>('import_clip', { path, allowDuplicate: true });
        setSelectedId(result.item.clipId);
      } finally { importingRef.current = false; if (mounted.current) setImporting(false); }
    }
    setDuplicatePath(null);
    await drainImports();
  }
  async function closeToTray(keep: boolean) {
    if (keep) await perform('hide_window');
    setCloseConfirmation(false);
  }

  const value: DesktopContextValue = {
    state, error, toast, selectedClip, selectClip: setSelectedId, query, setQuery,
    favoritesOnly, setFavoritesOnly, groupId, setGroupId, filteredClips,
    importing, importPaths, duplicatePath, resolveDuplicate,
    dirty, currentConfig, leaveRequest, resolveLeave, requestPreset,
    closeConfirmation, closeToTray, perform, refresh, notify: setToast,
  };
  return <DesktopContext.Provider value={value}>{children}</DesktopContext.Provider>;
}

export function useDesktop() {
  const value = useContext(DesktopContext);
  if (!value) throw new Error('Desktop controls require DesktopProvider');
  return value;
}
