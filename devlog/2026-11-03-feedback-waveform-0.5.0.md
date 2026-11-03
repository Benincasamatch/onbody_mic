# 2026-11-03 · 界面反馈批注实施（二）：真实波形与布局精简（0.5.0）

## 范围与依据

用户通过 EditHere 提交 12 条批注（0.4.1 主界面），核心追加要求：**音效卡显示真实波形，播放时带播放效果**。其余为批注驱动的视觉/布局调整。改动涉及 `desktop/src/`（React/CSS）、`src/OnbodyMic.Media/`（波形持久化）与 `src/OnbodyMic.EngineHost/Contracts.cs`（契约）。

## 批注对照表

| # | 意见 | 实施 |
|---|---|---|
| 1 | 这类卡片设为黑色，不透明度 90% | `--face` 改为 `rgba(0,0,0,.9)`（音效卡/路由卡等全部 `--face` 卡片） |
| 2 | 这个"更多"按钮去除 | 删除 `tile-actions` 第三列编辑按钮，`grid-template-columns` 改 `1fr 1fr`；编辑入口=单击卡片 |
| 3 | "MP3 100%增益"去除，有改动再显示 | 增益==100% 只显示格式；否则 `格式 · N% 增益`；缺失仍显示"素材文件缺失" |
| 4 | 实时显示波形（追加：真实波形 + 播放效果） | 导入时计算并持久化 peaks；卡片 SVG 画真实波形；播放中卡片 playing 态 + 扫光动画 |
| 5 | 这个栏目去掉，下方已有设置按钮 | 删侧栏 `.manage-groups`/`.sidebar-divider`/`.sidebar-section-label`/`.utility-nav`，保留底部"打开设置" |
| 6 | 顶栏与背景一样半透明 | `--glass-chrome` → `rgba(0,0,0,.66)`（同窗口背景） |
| 7 | 侧栏与背景一样半透明 + 黑色 90% 圆角卡片 | 侧栏背景同 `--glass-chrome`；分类项改为 `--face` 黑色 90% 圆角卡片 |
| 8 | 去掉播放条第二行小字 | 删 `playback-copy > span`（JSX + 死 CSS） |
| 9 | mixer 与背景一样半透明 | 同 #6 |
| 10 | mixer 卡片黑色 90% | 同 #1（`--face`） |
| 11 | 三个按钮间距重排 | `.native-window-controls` gap `2px→6px`、分隔加宽 |
| 12 | 预设选择器加"新建预设 +"按钮 | `preset-picker` 内加 `IconButton plus`，`onCreatePreset` 回调打开创建设备对话框 |

## 波形链路（核心）

### 后端

- 新增 `src/OnbodyMic.Media/WaveformPeaks.cs`：对已解码引擎格式单声道样本做 min/max 分箱（上限 2048 bin，与 `AudioFileEditor.ReadPeaks` 算法一致但无 IO）。
- `ClipItem`（ClipModels.cs）增加可选 `float[] Peaks` + `WaveformPeaks` 属性（旧库兼容为空）；**自定义值相等**（波形按值比较 `SequenceEqual`，哈希含逐 bin），否则持久化往返的 `Assert.Equal(ClipItem)` 因数组引用差异失败。
- `ClipItemDto` + `Peaks`；`Open` 加载并校验（偶数长度、≤2048bin、范围 [-1,1]、有限）；`SaveIndex` 写（空波形不写）；`ImportCore` 在解码后 `WaveformPeaks.Compute(decoded.Samples)` 存入。SchemaVersion 保持 2（可选字段向后兼容）。
- `ClipItemData`（Contracts.cs）加 `Peaks`，`FromDomain` 透传。

### 前端

- `types.ts` `ClipItem.peaks?: number[] | null`。
- `components.tsx` 新增 `ClipWaveform`：peaks → 120 bin SVG 对称波形；`ClipGrid` 计算 `playingId`（`status.clip` 命中，排除 `editor-preview:` 试听），播放中卡片加 `playing` class + `key=playing-${playbackEpoch}` 重挂载重启动画。
- CSS：`.tile-waveform`（SVG 波形）、`.tile-waveform-progress`（`waveform-sweep 3.2s` 扫光）、`.sound-tile.playing`（紫描边+辉光+标题变紫）。

## 验证

- `dotnet test`：Media 70 / Application 81 / Domain 46 / Persistence 30 / Audio 34 全通过；新增 3 个波形测试（导入计算+持久化往返、分箱与钳制、空/短样本）。
- `tsc --noEmit` + vite build 通过。
- 真实 Chromium（Tern PiP）加载开发前端 + Tauri IPC 桩（`window.isTauri` + `__TAURI_INTERNALS__` 全量模拟状态：3 条带 peaks 音效、1 条 Send 播放中）：
  - 3 张卡波形 SVG path 存在；播放卡 `playing` class、`waveform-sweep 3.2s` 动画运行（transform 持续推进）、紫描边/标题。
  - 卡片背景 `rgba(0,0,0,.9)`；顶栏/侧栏/混音栏 `rgba(0,0,0,.66)`；分类项未选中 `rgba(0,0,0,.9)`、选中紫。
  - 无"更多"按钮、`tile-actions` 两列；播放条无第二行；侧栏管理区消失、底部"打开设置"在；预设选择器 `+` 按钮存在。
  - 播放条文案正确（改增益的显示 `MP3 · 150% 增益`，正经音效 `WAV`，缺失素材 `素材文件缺失`）。

## 已知限制 / 说明

- 旧音效库（无 `Peaks`）条目波形为空 → 卡片回退静态图标；重新导入或裁剪新条目即带波形。
- Tern PiP 视口被限制在 390×258（触发 ≤700px 响应式），预设选择器按既有设计隐藏；`+` 按钮存在性已验证，桌面宽度下的实际点击未在真实窗口复测（后端已发布重编译，二进制嵌入最新前端）。
- 真实窗口冒烟受单实例锁阻塞：`E:\tools\OnbodyMic` 有用户正在运行的旧实例，新实例（含 `--root` 隔离目录与远程调试）被 `tauri-plugin-single-instance` 直接移交退出，无法附加。前端验证改用 Chromium + IPC 桩完成；后端链路由单测覆盖。

## 版本

- 按 docs/11：真实波形/新建预设属向后兼容新功能 → MINOR → **0.5.0**。`build/set-version.ps1 0.5.0` 已同步 6 处版本文件；CHANGELOG 补 0.4.1（此前遗漏）与 0.5.0 条目。
