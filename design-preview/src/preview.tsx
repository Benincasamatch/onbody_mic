import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { PRESETS, SAMPLE_CLIPS } from './model';
import type { Clip, GroupId, PanelKind, PlaybackMode, RouteId, SectionId } from './model';

const allOff = (): Record<RouteId, boolean> => ({ mic: false, app: false, extras: false, monitor: false });
const DEFAULT_GAINS: Record<RouteId, number> = { mic: 100, app: 70, extras: 85, monitor: 65 };
const DEFAULT_FAVORITES = SAMPLE_CLIPS.filter(clip => clip.favorite).map(clip => clip.id);

export interface PreviewPlayback {
  clipId: string;
  mode: PlaybackMode;
}

export interface PreviewState {
  clips: readonly Clip[];
  filteredClips: readonly Clip[];
  selectedClip: Clip;
  selectClip(id: string): void;
  enabled: Readonly<Record<RouteId, boolean>>;
  gains: Readonly<Record<RouteId, number>>;
  favorites: ReadonlySet<string>;
  toggleFavorite(id: string): void;
  query: string;
  setQuery(value: string): void;
  group: GroupId;
  setGroup(value: GroupId): void;
  section: SectionId;
  setSection(value: SectionId): void;
  presetId: string;
  setPreset(value: string): void;
  playback: PreviewPlayback | null;
  playClip(id: string, mode: PlaybackMode): void;
  stopClip(): void;
  emergency: boolean;
  toggleEmergency(): void;
  toggleRoute(route: RouteId): void;
  setGain(route: RouteId, gain: number): void;
  panel: PanelKind | null;
  openPanel(kind: PanelKind): void;
  closePanel(): void;
  notification: string;
  notify(message: string): void;
  reset(): void;
  reducedEffects: boolean;
  toggleEffects(): void;
  monitorMode: string;
  setMonitorMode(mode: string): void;
  clipGain: number;
  setClipGain(gain: number): void;
}

function usePreviewState(): PreviewState {
  const [enabled, setEnabled] = useState(allOff);
  const [gains, setGains] = useState(DEFAULT_GAINS);
  const [favorites, setFavorites] = useState(() => new Set(DEFAULT_FAVORITES));
  const [selectedId, setSelectedId] = useState('victory');
  const [query, setQuery] = useState('');
  const [group, setGroup] = useState<GroupId>('all');
  const [section, setSection] = useState<SectionId>('overview');
  const [presetId, setPresetId] = useState(PRESETS[0].id);
  const [playback, setPlayback] = useState<PreviewPlayback | null>(null);
  const [emergency, setEmergency] = useState(false);
  const [panel, setPanel] = useState<PanelKind | null>(null);
  const [notification, setNotification] = useState('');
  const [reducedEffects, setReducedEffects] = useState(false);
  const [monitorMode, setMonitorMode] = useState('ClipOnly');
  const [clipGain, setClipGain] = useState(100);

  useEffect(() => {
    if (!notification) return;
    const timer = window.setTimeout(() => setNotification(''), 3600);
    return () => window.clearTimeout(timer);
  }, [notification]);

  const filteredClips = useMemo(() => {
    const search = query.trim().toLocaleLowerCase();
    return SAMPLE_CLIPS.filter(clip =>
      (!search || `${clip.name} ${clip.subtitle}`.toLocaleLowerCase().includes(search)) &&
      (group === 'all' || (group === 'favorites' ? favorites.has(clip.id) : clip.group === group)));
  }, [query, group, favorites]);
  const selectedClip = SAMPLE_CLIPS.find(clip => clip.id === selectedId) ?? SAMPLE_CLIPS[0];

  function toggleRoute(route: RouteId) {
    if (emergency) {
      setNotification('示意：请先解除静音锁存；解除后所有路径仍保持关闭。');
      return;
    }
    if (route === 'app' && !enabled.app && !enabled.extras) {
      setNotification('示意：应用发送需要先开启「附加音频」。');
      return;
    }
    const turningOffExtras = route === 'extras' && enabled.extras;
    setEnabled(current => ({ ...current, [route]: !current[route], ...(turningOffExtras ? { app: false } : {}) }));
    if (turningOffExtras && playback?.mode === 'send') setPlayback(null);
  }

  function playClip(id: string, mode: PlaybackMode) {
    setSelectedId(id);
    if (emergency || (mode === 'send' && !enabled.extras)) {
      setNotification('示意：发送尚未开启，或处于静音锁存。不会排队补播。');
      return;
    }
    setPlayback({ clipId: id, mode });
    setNotification(mode === 'preview' ? '仅模拟本地试听状态，不播放任何音频。' : '仅模拟发送状态，不连接任何音频端点。');
  }

  function toggleEmergency() {
    setEmergency(current => !current);
    setEnabled(allOff());
    setPlayback(null);
    setNotification(emergency ? '示意：已解除锁存，所有发送与监听仍保持关闭。' : '示意：已锁存全部静音，清除当前播放状态。');
  }

  function setPreset(id: string) {
    setPresetId(id);
    setEnabled(allOff());
    setPlayback(null);
    setNotification('示意：已切换预设，所有发送与监听保持关闭。不会保存配置。');
  }

  function toggleFavorite(id: string) {
    setFavorites(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  function reset() {
    setEnabled(allOff());
    setGains(DEFAULT_GAINS);
    setFavorites(new Set(DEFAULT_FAVORITES));
    setSelectedId('victory');
    setQuery('');
    setGroup('all');
    setSection('overview');
    setPresetId(PRESETS[0].id);
    setPlayback(null);
    setEmergency(false);
    setPanel(null);
    setNotification('');
    setMonitorMode('ClipOnly');
    setClipGain(100);
  }

  return {
    clips: SAMPLE_CLIPS, filteredClips, selectedClip, selectClip: setSelectedId,
    enabled, gains, favorites, toggleFavorite, query, setQuery, group, setGroup,
    section, setSection, presetId, setPreset, playback, playClip,
    stopClip: () => setPlayback(null), emergency, toggleEmergency,
    toggleRoute, setGain: (route: RouteId, gain: number) => setGains(current => ({ ...current, [route]: gain })),
    panel, openPanel: setPanel, closePanel: () => setPanel(null),
    notification, notify: setNotification, reset,
    reducedEffects, toggleEffects: () => setReducedEffects(current => !current),
    monitorMode, setMonitorMode, clipGain, setClipGain,
  };
}

const PreviewContext = createContext<PreviewState | null>(null);

export function PreviewProvider({ children }: { children: ReactNode }) {
  const state = usePreviewState();
  return <PreviewContext.Provider value={state}>{children}</PreviewContext.Provider>;
}

export function usePreview() {
  const state = useContext(PreviewContext);
  if (!state) throw new Error('Preview controls require PreviewProvider');
  return state;
}
