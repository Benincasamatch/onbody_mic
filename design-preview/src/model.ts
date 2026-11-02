export type LayoutId = 'workbench' | 'console' | 'soundboard';
export type RouteId = 'mic' | 'app' | 'extras' | 'monitor';
export type SectionId = 'overview' | 'library' | 'presets' | 'devices' | 'hotkeys' | 'diagnostics';
export type PanelKind = 'presets' | 'devices' | 'hotkeys' | 'diagnostics' | 'import' | 'clip';
export type PlaybackMode = 'preview' | 'send';
export type GroupId = 'all' | 'favorites' | '游戏' | '人声' | '氛围';

export interface Clip {
  id: string;
  name: string;
  subtitle: string;
  group: '游戏' | '人声' | '氛围';
  duration: string;
  shortcut: string;
  favorite: boolean;
}

export const SAMPLE_CLIPS: readonly Clip[] = [
  { id: 'victory', name: '胜利时刻', subtitle: '干净利落的回合结束音', group: '游戏', duration: '03.2', shortcut: 'Ctrl 1', favorite: true },
  { id: 'nice', name: '这波很漂亮', subtitle: '给队友一个及时的肯定', group: '人声', duration: '02.4', shortcut: 'Ctrl 2', favorite: true },
  { id: 'wait', name: '等我一下', subtitle: '稍等，马上就到', group: '人声', duration: '01.8', shortcut: 'Ctrl 3', favorite: true },
  { id: 'round', name: '下一回合', subtitle: '重整旗鼓，继续出发', group: '游戏', duration: '04.1', shortcut: 'Ctrl 4', favorite: false },
  { id: 'oops', name: '不好意思', subtitle: '轻松化解一个小失误', group: '人声', duration: '02.1', shortcut: 'Alt 1', favorite: false },
  { id: 'retreat', name: '我先撤了', subtitle: '撤退也要有仪式感', group: '人声', duration: '02.6', shortcut: 'Alt 2', favorite: false },
  { id: 'levelup', name: '等级提升', subtitle: '一点小小的成就感', group: '游戏', duration: '03.6', shortcut: 'Alt 3', favorite: true },
  { id: 'lofi', name: '夜间氛围', subtitle: '柔和的开场片段', group: '氛围', duration: '08.0', shortcut: 'Alt 4', favorite: false },
  { id: 'ready', name: '准备好了', subtitle: '队伍集合，开始游戏', group: '人声', duration: '01.5', shortcut: 'Shift 1', favorite: false },
  { id: 'alert', name: '注意身后', subtitle: '一个清晰的提醒', group: '游戏', duration: '02.8', shortcut: 'Shift 2', favorite: false },
  { id: 'calm', name: '放轻松', subtitle: '下一把会更好', group: '氛围', duration: '05.4', shortcut: 'Shift 3', favorite: false },
  { id: 'end', name: '今天就到这里', subtitle: '给今晚一个温柔收尾', group: '氛围', duration: '06.2', shortcut: 'Shift 4', favorite: false },
];

export const PRESETS = [
  { id: 'gaming', name: '开黑默认', description: '真实麦克风 + 短音效', icon: 'app' as const },
  { id: 'music', name: '音乐分享', description: '指定播放器 + 本地监听', icon: 'music' as const },
  { id: 'quiet', name: '安静夜间', description: '较低增益，专注交流', icon: 'headphones' as const },
];

export const ROUTE_INFO = {
  mic: { name: '真实麦克风', source: 'USB 耳麦 · 示例设备', action: '真麦发送', icon: 'mic' as const, description: '只发送你的讲话' },
  app: { name: '应用音频', source: '音乐播放器 · 示例绑定', action: '应用发送', icon: 'app' as const, description: '仅捕获选定应用进程树' },
  extras: { name: '附加音频', source: '应用与音效的发送总开关', action: '附加发送', icon: 'music' as const, description: '关掉它，不影响真麦' },
  monitor: { name: '本地监听', source: '佩戴的耳机 · 示例设备', action: '开启监听', icon: 'headphones' as const, description: '仅音效 / 实际虚拟麦' },
};

export const LAYOUTS = [
  { id: 'workbench' as const, code: 'A', name: '工作台', description: '导航清晰 · 详情常驻', icon: 'dashboard' as const },
  { id: 'console' as const, code: 'B', name: '单屏控制台', description: '路由常显 · 少切页面', icon: 'sliders' as const },
  { id: 'soundboard' as const, code: 'C', name: '音效台', description: '音效优先 · 紧凑混音', icon: 'grid' as const },
];
