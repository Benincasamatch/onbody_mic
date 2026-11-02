import {
  Activity, AppWindow, AudioLines, Bell, Check, ChevronDown, Clock3, Command,
  Folder, Grid2X2, Headphones, Info, Keyboard, Layers3, LayoutDashboard, List,
  Mic, Monitor, MoreHorizontal, Music2, PanelLeftClose, PanelLeftOpen,
  PanelRightOpen, Play, Plus, Power, RefreshCw, Search, Send, Settings2, ShieldCheck,
  SlidersHorizontal, Square, Star, Upload, Volume2, VolumeX, X, ArrowUpRight,
} from 'lucide-react';

const icons = {
  audio: AudioLines, mic: Mic, headphones: Headphones, music: Music2, app: AppWindow,
  search: Search, star: Star, play: Play, send: Send, settings: Settings2,
  keyboard: Keyboard, presets: Layers3, shield: ShieldCheck, volume: Volume2,
  muted: VolumeX, chevron: ChevronDown, more: MoreHorizontal, plus: Plus,
  collapse: PanelLeftClose, expand: PanelLeftOpen, inspector: PanelRightOpen,
  close: X, stop: Square, folder: Folder, dashboard: LayoutDashboard,
  sliders: SlidersHorizontal, command: Command, link: ArrowUpRight, info: Info,
  refresh: RefreshCw, clock: Clock3, grid: Grid2X2, list: List, activity: Activity,
  bell: Bell, upload: Upload, check: Check, power: Power, monitor: Monitor,
};
export type IconName = keyof typeof icons;
export function Icon({ name, size = 18, className = '' }: { name: IconName; size?: number; className?: string }) {
  const Component = icons[name];
  return <Component size={size} strokeWidth={1.65} className={className} aria-hidden="true" />;
}
