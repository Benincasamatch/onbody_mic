import { invoke, isTauri } from '@tauri-apps/api/core';
import type { CommandError, DesktopState } from './types';

export const nativeRuntime = isTauri();

export function errorMessage(error: unknown) {
  if (typeof error === 'string') return error;
  if (error && typeof error === 'object' && 'message' in error) return String((error as CommandError).message);
  return String(error);
}

export async function desktopState(): Promise<DesktopState> {
  if (!nativeRuntime) throw new Error('请通过随声麦桌面程序打开；浏览器页面不连接音频后端。');
  return invoke<DesktopState>('desktop_state');
}

export async function desktopCommand<T>(op: string, args: object = {}): Promise<T> {
  if (!nativeRuntime) throw new Error('当前页面不是 Tauri 桌面窗口，操作未执行。');
  return invoke<T>('desktop_command', { op, args });
}
