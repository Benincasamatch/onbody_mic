export type RouteId = 'mic' | 'app' | 'extras' | 'monitor';
export type MonitorMode = 'ClipOnly' | 'FullEndpoint';
export type PanelKind = 'presets' | 'devices' | 'hotkeys' | 'diagnostics' | 'clip';
export type HotkeySection = 'globalHotkeys' | 'clipHotkeys' | 'presetHotkeys';

export interface UiPreferencesData {
  startMinimized: boolean;
  closeToTrayAcknowledged: boolean;
  clipSort: 'name' | 'importTime' | 'libraryOrder';
  reduceTransparency?: boolean;
}
export interface SettingsData {
  schemaVersion: number;
  lastPresetId?: string | null;
  libraryRoot?: string | null;
  globalHotkeys: Record<string, string | null>;
  clipHotkeys: Record<string, string | null>;
  presetHotkeys: Record<string, string | null>;
  ui: UiPreferencesData;
}
export interface AppSelection {
  executablePath: string | null;
  displayName: string | null;
}
export interface DeviceConfig {
  micDeviceId: string;
  virtualRenderDeviceId: string;
  virtualCaptureDeviceId: string | null;
  monitorDeviceId: string;
  application: AppSelection | null;
}
export interface RouteGains { mic: number; app: number; clip: number; monitor: number }
export interface PresetConfig {
  presetId: string;
  name: string;
  devices: DeviceConfig;
  micGain: number;
  appGain: number;
  clipGain: number;
  monitor: { mode: MonitorMode; gain: number };
  clipLayout: string[];
  libraryFavoritesOnly: boolean;
  libraryGroupId: string | null;
}
export interface PresetSummary { presetId: string; name: string; corrupt: boolean; reason: string | null }
export interface SettingsIssue { section: string; key: string; reason: string }
export interface ClipItem {
  clipId: string;
  displayName: string;
  managedAssetPath: string;
  originalImportPath: string | null;
  durationMs: number;
  format: 'wav' | 'mp3';
  favorite: boolean;
  groupId: string | null;
  gain: number;
  importUnixMs: number;
  available: boolean;
  origin?: ClipOrigin | null;
}
export interface ClipGroup { groupId: string; name: string; parentGroupId: string | null }
export interface AudioDevice { id: string; friendlyName: string; dataFlow: 'Render' | 'Capture'; state: string; isDefault: boolean }
export interface AppInstance { processId: number; executablePath: string; displayName: string; category: 'taskbar' | 'tray' | 'background'; startTimeTicks: string }
export interface HotkeyBindingView { action: string; targetId: string | null; gesture: string }
export interface SessionStatus {
  phase: 'Unconfigured' | 'ConfiguredMuted' | 'ReadyMuted' | 'Running' | 'EmergencyMuted' | 'OutputLost';
  switches: { micSendEnabled: boolean; extrasSendEnabled: boolean; appSendEnabled: boolean; monitorEnabled: boolean; anySendEnabled: boolean };
  emergencyLatched: boolean;
  outputLost: boolean;
  devicesValidated: boolean;
  lastPresetId: string | null;
  appBinding: { processId: number; description: string } | null;
  clip: { clipId: string; mode: 'Preview' | 'Send'; playbackEpoch: number } | null;
  monitorSuspendedForPreview: boolean;
  monitorMode: MonitorMode;
  gains: RouteGains;
  devices: DeviceConfig | null;
  clipLoading: boolean;
}
export interface SessionLevels { mic: number; app: number; clip: number; mix: number }
export interface RuntimeState {
  revision: number;
  status: SessionStatus;
  levels: SessionLevels;
  metrics: Record<string, unknown>;
  issues: { route: string; reason: string }[];
}
export interface EngineSnapshot extends RuntimeState {
  clips: ClipItem[];
  groups: ClipGroup[];
  hotkeys: HotkeyBindingView[];
  hotkeyIssues: SettingsIssue[];
  hotkeyActions: string[];
  libraryError: string | null;
  libraryRoot: string;
}
export interface DesktopState {
  root: string;
  settings: SettingsData;
  settingsError: CommandError | null;
  settingsIssues: SettingsIssue[];
  presets: PresetSummary[];
  activePreset: PresetConfig | null;
  engine: EngineSnapshot | null;
  engineError: string | null;
  version: string;
}
export interface CommandError { code: string; message: string }
export interface CommandResult { accepted: boolean; rejection: string | null }
export interface PresetApplyResult {
  accepted: boolean;
  rejection: string | null;
  issues: { route: string; reason: string }[];
  outputAttachRejection: string | null;
}
export interface ImportResult { outcome: 'Imported' | 'Duplicate'; item: ClipItem; existingDuplicate: ClipItem | null }
export interface ClipOrigin { kind: string; parentClipId: string | null; originalPath: string; startMs: number; endMs: number }
export type AudioSource = { path: string; clipId?: never } | { clipId: string; path?: never };
export interface AudioInspection { durationMs: number; sampleRate: number; channels: number; peaks: number[]; sourceVersion: string }
export interface AudioSelection { sourceVersion: string; startMs: number; endMs: number; displayName: string; groupId: string | null }
export interface PendingAudioImport { file: ImportFile; inspection: AudioInspection | null; reason: string; remaining: number }
export interface ImportFile { path: string; groupId: string | null }
export interface FolderImportResult { files: ImportFile[]; rootGroupIds: string[] }
