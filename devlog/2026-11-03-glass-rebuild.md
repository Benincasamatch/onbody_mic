# 2026-11-03 · 磨砂玻璃重构（Acrylic 系统材质 + 透明分层）

## 背景

用户要求"重新构建 UI 界面的玻璃效果"。此前（2026-11-03 上午）的液态玻璃改版基于 CSS `backdrop-filter`，但经独立玻璃测试工程（`glass-test`，独立项目，后移出本仓库）实测证实：**透明 WebView2 窗口中 CSS `backdrop-filter` 完全失效**（WebView2 issue [#4945](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4945)，Chromium 只对 backdrop root 内像素生效，透明窗口的 backdrop root 之外是桌面像素，不参与）。真实磨砂只能来自系统 Acrylic 材质。

因此本次重构 = **系统 Acrylic（磨砂来源）+ CSS 半透明分层（透射控制）**，删除全部失效的 backdrop-filter 死代码。

## 用户指定参数（已全部实现并验证）

| 元素 | 参数 | 计算样式实测 |
|---|---|---|
| 窗口底 `.app-window` | alpha 30 | `rgba(0,0,0,.3)` |
| 侧栏 `.soundboard-sidebar` | 圆角矩形 + alpha 66 | `rgba(0,0,0,.66)` + 圆角 14px |
| 主区 `.soundboard-main` | 圆角矩形 + alpha 66 | `rgba(0,0,0,.66)` + 圆角 14px |
| 混音栏 `.soundboard-mixer` | 圆角矩形 + alpha 66 | `rgba(0,0,0,.66)` + 圆角 14px |
| 面板间隔 | 非常小 | 6px（实测 gapSM=gapMX=6） |
| 顶栏 `.window-header` | 浏览器工具栏式 + alpha 25 | `rgba(0,0,0,.25)`，高 40px |
| 顶栏右侧按钮 `.header-actions .icon-button` | 实色 #322E39 | `rgb(50,46,57)`，不参与半透明 |

## 变更文件

- `desktop/src/styles.css`
  - `.app-window` 底 `rgba(0,0,0,.66)` → `rgba(0,0,0,.30)`，删 `backdrop-filter:blur(30px)`
  - `.window-header` 54px → 40px 浏览器工具栏式，`rgba(0,0,0,.25)`，删 blur
  - `.header-actions .icon-button` 实色 `#322E39`（hover `#3b3745`）
  - `.soundboard-body` grid 三列 → flex + gap 6px + 内边距；三个面板改圆角 14px、边框、内高光、alpha 66
  - `.playback-strip` / `.status-footer` 删 blur，改 `rgba(255,255,255,.035/.03)` 极淡白
  - 全部失效 backdrop-filter 删除（app-dialog/dialog-heading/::backdrop/toast/select-popup/app-picker-list sticky 头/primary/danger 按钮），计数从 13 → 0
  - 删除死令牌 `--glass-blur`/`--glass-blur-strong`/`--glass-saturate`/`--glass-chrome`
  - 媒体查询内 8 处 grid-template-columns 列宽规则 → 面板宽度控制
  - `.sidebar-collapsed`/`.mixer-hidden` 状态改宽度/display 控制
- `desktop/src/App.tsx`
  - 删除 `<svg class="glass-defs">` 及 `#lg-refract`/`#lg-refract-soft` 两个 SVG 位移折射滤镜（依赖失效 backdrop-filter，属死代码）

## 保留项（有意不动）

- `[data-effects='reduced']`（设置"切换透明材质"开关）：Rust 侧切 `Effect::None` + CSS 不透明回退，语义保留
- `@supports not (backdrop-filter)` 降级分支：当前无实际 blur 可关，保留以防未来引入

## 验证（真实 Tauri + WebView2 156，CDP）

- 构建：`npm run build`（tsc + vite）通过；`cargo build --release --features tauri/custom-protocol` 通过
- 计算样式（CDP `getComputedStyle`）：上表全部命中；全页面 `backdrop-filter` 计数 **0**；`glass-defs`/`filter` 元素 0
- 几何：顶栏高 40px；三面板 y=48 等高 774；间隔 6px；侧栏 184px、混音栏 258px
- 说明：空库根目录（`--root .tools/tmp/glass-root`）无音效卡，`.sound-tile` 未渲染属正常

## 测试工程

`glass-test`（独立项目，已移出本仓库）用于迭代验证；其制作方法（实测结论、Rust Acrylic 代码、CSS 分层取值、构建/验证/移植步骤）在本日志"背景"与"验证"节中保留关键结论。

## 已知限制

- 透明窗口的合成截图（CopyFromScreen/PrintWindow/CDP）对透明区域不可靠，真实观感需人眼确认
- Acrylic 在窗口拖动/缩放时性能偏差（Win10 1903+ 与部分 Win11 构建）
- 多显示器不同缩放下的材质表现未测
