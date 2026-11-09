# 2026-11-09 · 设置入口固定在左侧栏

## 背景
用户反馈：“设置应该固定在左侧栏目中”。

## 问题复现
侧栏 `aside.soundboard-sidebar` 自身是滚动容器（`overflow:auto`），“打开设置”按钮 `button.sidebar-note` 放在导航末尾、靠 `margin-top:auto` 贴底。这带来三处失效：

1. 分组/音效树超过一屏时，整条侧栏一起滚动，按钮被推到内容末尾、滚出可视区（`margin-top:auto` 在溢出的 flex 列中不起作用）。
2. 收起侧栏（`.sidebar-collapsed`）时 `.sidebar-note` 直接 `display:none`，设置入口消失。
3. ≤700px 断点同样 `display:none`。

## 决策
把侧栏拆成“可滚动导航区 + 固定底部入口”：设置按钮移出滚动容器，永远停在左栏底部；收起与窄宽度时改为仅图标，仍可点击。

- 不把设置面板本身嵌进左栏：现有交互是统一 `SettingsDialog` 分项入口（0.4.1 起“侧栏管理区 4 项合并为单一设置入口”），本次只解决“入口不在固定位置/会消失”。
- 不新增第二套导航：按钮仍是同一个 `openPanel('settings')`。

## 修改文件
- `desktop/src/App.tsx`：`<div className="sidebar-scroll">` 包裹 `.sidebar-heading` 与 `.category-nav`；`button.sidebar-note` 留在 `aside` 内、滚动区外，并补 `aria-label="打开设置"`（仅图标时仍有可读名称）。
- `desktop/src/styles.css`
  - `.soundboard-sidebar`：`overflow:auto` → `overflow:hidden`（外壳不再滚动）。
  - 新增 `.sidebar-scroll { flex:1; min-height:0; overflow-y:auto; }`，滚动只发生在导航区。
  - `.sidebar-note`：`margin-top:auto` → `margin-top:10px; flex-shrink:0`，作为固定底栏。
  - 收起态：从隐藏列表移除 `.sidebar-note`，改隐藏 `.sidebar-note > span`，并在 58px 窄栏中居中图标（`.sidebar-collapsed .sidebar-note { justify-content:center; padding:0; }`）。
  - ≤700px：侧栏改为横排（`flex-direction:row`），滚动区 `flex:1`、导航横向换行，设置入口变为右侧仅图标按钮；同时把收起态的 58px 宽度在窄断点复位为 `width:auto`，避免横排里被压成 58px。

## 行为变化
- 设置入口在所有宽度与折叠状态下都可见，位置固定在左栏底部。
- 侧栏标题与树照常滚动；滚动区不再包含设置按钮。

## 验证
- `npm --prefix desktop run build`（`tsc --noEmit` + vite）通过。
- 真实 Chromium（Edge，1280×760，无 Tauri 桩）：侧栏 184px；“打开设置”位于 y 668–705（高 37px），侧栏底 722（16px 内边距 + 1px 描边），`gapBelow=17`。
- 溢出：向 `.category-nav` 注入 40 行后 `.sidebar-scroll` `scrollHeight=1875 / clientHeight=587`；把 `scrollTop` 拉到 1875，按钮矩形仍为 668–705（未移动），仍在侧栏内。
- 收起侧栏：宽度 58px，按钮 42×37 贴底、文字隐藏、图标可见；点击后打开 `dialog[aria-label="设置"]`。
- 窄宽度（368 CSS px，仅浏览器预览）：侧栏横排，“打开设置”变为 35×29 图标按钮，位于导航右侧且可见。
- 截图证据：`.tools/tmp/` 本轮未留存（临时会话截图），几何与可见性以上述 DOM 测量为准。

## 已知限制
- ≤700px 断点只用于浏览器预览（真实窗口 `minWidth=760`，见 [左侧树形目录 devlog](2026-11-08-left-tree-navigation.md)）；该断点侧栏宽度仍受 ≤950px 嵌套规则约束（132px），横排内容因此偏窄，属既有问题，本次未改。
- 本轮只改前端，未在真实 Tauri/WebView2 窗口复测；Rust 与音频后端零改动。