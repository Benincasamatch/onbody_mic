import { Activity, AppWindow, AudioLines, Check, ChevronDown, Clock3, Download, Folder, Grid2X2, Headphones, Info, Keyboard, Layers3, Mic, Minus, MoreHorizontal, Music2, PanelLeftClose, PanelLeftOpen, Play, Plus, Power, RefreshCw, Search, Send, Settings2, ShieldCheck, SlidersHorizontal, Square, Star, Upload, Volume2, VolumeX, X, ArrowUpRight, Copy, Trash2, Maximize2, Save } from 'lucide-react';
const icons = { audio: AudioLines, mic: Mic, headphones: Headphones, music: Music2, app: AppWindow, search: Search, star: Star, play: Play, send: Send, settings: Settings2, keyboard: Keyboard, presets: Layers3, shield: ShieldCheck, volume: Volume2, muted: VolumeX, chevron: ChevronDown, more: MoreHorizontal, plus: Plus, collapse: PanelLeftClose, expand: PanelLeftOpen, close: X, stop: Square, folder: Folder, sliders: SlidersHorizontal, link: ArrowUpRight, info: Info, refresh: RefreshCw, clock: Clock3, grid: Grid2X2, activity: Activity, upload: Upload, check: Check, power: Power, minimize: Minus, maximize: Maximize2, copy: Copy, delete: Trash2, save: Save, download: Download };
export type IconName = keyof typeof icons;
export function Icon({ name, size = 18, className = '' }: { name: IconName; size?: number; className?: string }) {
  const Component = icons[name];
  return <Component size={size} strokeWidth={1.65} className={className} aria-hidden="true" />;
}
