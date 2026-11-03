# 2026-11-03 · 界面反馈批注实施

## 背景
用户通过 EditHere 对随声麦主界面（0.4.0）提交 10 条批注反馈，要求按批注意见调整主界面布局与交互。

## 批注对照表

| # | 意见 | 实施 |
|---|---|---|
| 1 | 删侧栏"只有明确开启的声音…"提示，改为设置入口 | 侧栏底部 `.sidebar-note` 改为"打开设置"按钮 |
| 2 | "诊断与状态"收入设置栏目 | 侧栏管理区 4 项合并为单一"设置"入口，打开统一设置页 |
| 3 | 同上（侧栏管理区按钮收入设置） | 同上 |
| 4 | 右上角"设备与预设配置"收入设置，无需独立按钮 | 删除 WindowHeader 的 settings 图标按钮 |
| 5 | 右上角删按钮 | 与 #4 合并处理；静音按钮保留不动 |
| 6 | 删 footer"状态不代表 KOOK 或远端已收到"提示 | 删除 `.footer-note` |
| 7 | 关闭/开启状态用红绿色强调 | footer 状态：开启 `--success` 绿、关闭 `--error` 红、异常保持 warning |
| 8 | 音效卡片：快速添加快捷键按钮；单击打开详情，双击播放 | 卡片 shortcut-chip 增强为显式快捷入口（未绑定显示"快捷"），点击打开全局热键并聚焦该音效；单击卡片打开详情（ClipDialog），双击播放 |
| 9 | 收藏前后图标应有变化 | 收藏后星标实心填充 `fill:var(--warning)`（原 alpha 0.16 几乎不可见） |
| 10 | 无需标注 | 忽略 |

## 用户澄清（歧义确认）
- 右上角按钮从左到右：收起展开分类导航 / 收起展开发送与监听 / 设备与预设配置 / 静音。静音不动；两个折叠按钮逻辑统一（按 mixer 的 active 模式制作）；设备与预设配置收入设置页面
- footer 诊断按钮不动；侧栏管理区按钮收入设置页面
- 侧栏底部提示改为"设置"按钮

## 修改文件
- `desktop/src/App.tsx`：设置入口、设置 panel、footer 状态红绿、ClipGrid hotkeys 回调、HotkeysDialog focusClipId
- `desktop/src/components.tsx`：WindowHeader 删 settings 按钮/移除 openPanel prop、ClipGrid 快捷键按钮与单击详情
- `desktop/src/dialogs.tsx`：新增 SettingsDialog、HotkeysDialog 支持 focusClipId
- `desktop/src/styles.css`：footer 红绿、收藏实心、settings-grid、shortcut-chip、hotkey focused 高亮

## 验证
- `npx tsc --noEmit` 通过
- `npm run build` 通过（vite 构建成功）
- tauri dev 冒烟验证（进行中）

## 发布（0.4.1）
- 版本决策：本次为界面修正（PATCH），用户确认发布为 0.4.1
- `build/set-version.ps1 0.4.1` 同步 6 处版本文件
- `build/build-desktop.ps1 -Bundle` 全链构建通过（.NET 后端发布、Rust release、vite、NSIS）
- 产物：
  - `dist/OnbodyMic-0.4.1-desktop-portable-x64/`
  - `dist/OnbodyMic-0.4.1-desktop-portable-x64.zip`
  - `dist/OnbodyMic-0.4.1-desktop-installer-x64.exe`
- 冒烟：便携版启动窗口正常、侧栏渲染正常
- `.edithere/` 批注目录已加入 `.gitignore`

