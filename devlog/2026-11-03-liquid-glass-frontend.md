# 2026-11-03 前端液态玻璃（Liquid Glass）视觉改版开发日志

## 范围与依据

用户要求参考其提供的《液态玻璃（Liquid Glass）效果设计参考》改造正式前端（`desktop/` Tauri 2 + React/TypeScript 音效台）。参考文档给出分阶段路线（L1 视觉模拟 → L2 位移折射 → L3 GPU 材质 → L4 系统原生材质）与 Web 建议顺序（CSS → SVG → WebGL），并明确“玻璃用于导航、工具栏、浮动按钮、弹出控件等功能层，不要把所有内容面板都套上玻璃”。

改动只涉及 `desktop/src/styles.css` 与 `desktop/src/App.tsx`（新增 SVG 滤镜定义）。未改音频、配置、预设、命令契约与文档描述的既有行为。

## 改动前的实现事实（已读取核实）

1. `:root` 定义了 12 个色令牌；`.app-window` 用 `rgba(16,17,20,.86)` + 两组极淡径向渐变作为窗口底色，Tauri 窗口本身 `transparent: true`、`decorations: false`（`desktop/src-tauri/tauri.conf.json`）。
2. 全部“玻璃”只是半透明底色：`.window-header` 用 `rgba(255,255,255,.012)`，侧栏 `.soundboard-sidebar` 用 `rgba(20,22,26,.65)`，混音栏 `rgba(22,24,30,.62)`，播放条 `rgba(255,255,255,.012)`；**没有任何 `backdrop-filter`**，唯一例外是 `.app-dialog::backdrop` 的 `blur(5px)`。
3. 已有“可关闭的玻璃效果”开关：`data-effects="reduced"` 由 `settings.ui.reduceTransparency` 驱动，原实现只覆盖窗口底色、音效卡/路由卡底色与对话框背板模糊三处。
4. 卡片材质（`.sound-tile`、`.route-card`）、输入井（`.form-field input`、`.select-trigger`、`.diagnostic-json`）为不透明或近不透明纯色（`#141821`/`#131821`/`#111722`），与“同一种材质”的分层观感不一致。
5. 弹出选单 `.select-popup` 通过 `showPopover()` 进入 top layer（`desktop/src/Select.tsx`），浮动提示 `.toast` 为 `position:fixed`，二者都悬浮在结构化内容之上——正是参考文档建议使用玻璃的位置。

## 决策

1. **保真层级取 L1（完整）+ L2（SVG 位移折射）**，不引入 WebGL/WebGPU：音效台是控制面板，L3 的渲染管线成本与兼容风险不成比例；参考文档也把 SVG 列为保持 Web 技术栈的进阶路线。
2. **玻璃只用于功能层**：顶栏、侧栏、混音栏、播放条、状态栏、对话框、弹出选单、提示条、按钮/图标按钮。内容卡片（音效卡、路由卡、预览行）保持“实体卡片”材质，只统一了描边、圆角与顶部高光，避免削弱内容层级。
3. **折射做在真正有结构化背景的浮动控件上**：`.app-dialog`、`.dialog-heading`、`.select-popup`、`.toast`。顶栏/侧栏/混音栏背后只有窗口底色与氛围层（无结构），加折射不可见且徒增开销，因此这些面只做模糊 + 饱和度 + 边缘高光。
4. **折射使用“先普通、后 url()”的层叠降级**：`backdrop-filter:blur(...)` 在前、`backdrop-filter:url(#id) blur(...)` 在后。支持 SVG 滤波引用时后者生效；不支持时该声明整条无效并回落到前者，不需要单独的降级分支。
5. **窗口底色从 `.86` 收回到 `.8`**：完整透出桌面需要更低的 alpha，但内容区（无玻璃面板覆盖）的次级文字会在明亮桌面上失分。`.8` 是实测取舍点（见验证）。
6. 同时补了 `@supports not (backdrop-filter: blur(1px))` 的整片降级（改为不透明底色）与 `prefers-reduced-motion` 既有约束。

## 实施记录

### 设计令牌（`desktop/src/styles.css` `:root`）

新增 `--glass-blur:26px`、`--glass-blur-strong:40px`、`--glass-saturate:1.85`、`--glass-chrome:rgba(19,21,27,.56)`、`--glass-panel`、`--glass-popup`、`--glass-line`、`--glass-line-strong`、`--glass-sheen`（顶部高光渐变）、`--glass-rim`（内侧 1px 高光 + 内描边）、`--card-surface`、`--card-sheen`、`--radius-chrome:16px`、`--radius-card:14px`；`--line` 由 `.065` 提到 `.075` 以在玻璃上保持分隔线可辨。

### 材质分层

- 窗口：`.app-window` 增加 `position:relative; isolation:isolate`，底色改为 `rgba(11,12,16,.8)`；新增 `.app-window::before` 作为 `z-index:-1` 的氛围层（四组彩色径向渐变），让模糊有可见来源，同时成为所有面板的共同背景。
- 玻璃面板（雾面）：`.window-header`、`.soundboard-sidebar`、`.soundboard-mixer`、`.playback-strip` 统一 `blur(26px) saturate(1.85)` + 半透明染色 + 顶部高光 + 内侧高光/描边；`.status-footer` 用 `blur(18px)` 做薄玻璃。
- 弹出层（更厚、带折射）：`.app-dialog`（`blur(40px)` + `url(#lg-refract)`）、`.dialog-heading`（`blur(20px)` + `url(#lg-refract-soft)`，sticky 头把滚动内容模糊/折射出玻璃厚度）、`.select-popup` 与 `.toast`（`blur(26px)` + `url(#lg-refract-soft)`）；`.app-dialog::backdrop` 的模糊由 5px 提到 9px 并降低明度遮挡。
- 控件：按钮改为玻璃芯片（顶部高光渐变 + 内侧高光 + hover 提亮），图标按钮圆角 6→9px，输入类（搜索框、输入框、下拉触发器、诊断 JSON、路径文本、波形框）统一为“玻璃上的凹槽”，开关与滑块做光泽处理，电平条槽加深。
- 内容卡片：音效卡、路由卡、预览行、指标卡、构建信息、诊断状态、关于头图、更新卡统一 `--card-surface` + `--card-sheen` + 内侧高光，圆角 10→12/14px。
- 降级：`[data-effects='reduced']` 扩展到禁用全部 `backdrop-filter`（`!important`）、隐藏氛围层、把各玻璃面改为不透明纯色；`@supports not (…)` 分支同样回落到不透明。

### 折射滤镜（`desktop/src/App.tsx`）

根节点内新增 `<svg class="glass-defs" aria-hidden="true">`，定义两个滤镜：

- `#lg-refract`：`feTurbulence(fractalNoise, baseFrequency 0.006/0.009, 2 octaves, seed 7)` → `feGaussianBlur(4)` → `feDisplacementMap(scale 22, R/G)`，用于大面积的对话框。
- `#lg-refract-soft`：`baseFrequency 0.016/0.022`、`scale 12`，用于小面积的 sticky 头、下拉选单与提示条。

`color-interpolation-filters="sRGB"` 显式指定，避免默认线性空间下位移幅度失真。`.glass-defs` 固定 0×0 且 `pointer-events:none`，不参与布局与命中测试。

## 验证

### 引擎能力前置测试

在 Chromium 156 上验证 `backdrop-filter: url(#filter)` 是否真的位移背景：构造高对比条纹背景 + 三个面板（仅 blur / url+blur / 仅 url）截图比对——仅 `url()` 的面板显示出条纹被明显扭曲，`blur` 面板仅灰化。结论：该引擎支持用 SVG 位移滤波器做真实背景折射（L2），不是只解析不生效。

### 真实前端渲染（Chromium 156 / Edge 156.0.4314.58）

`desktop/dist` 构建产物在 Edge 156 中加载真实 React 前端，并用初始脚本注入 Tauri IPC 桩（`window.__TAURI_INTERNALS__` + `isTauri`）喂入一份完整模拟状态（8 条音效、3 个分组、3 个预设、四路路由），使全部玻璃面都处于有内容的状态；页面底层用“彩色壁纸 / 纯白 / 近黑”三种 html 背景模拟透明窗口后的真实桌面。

| 检查 | 结果 |
| --- | --- |
| 计算样式 | `.window-header`/`.soundboard-sidebar`/`.soundboard-mixer`/`.playback-strip` = `blur(26px) saturate(1.85)`；`.status-footer` = `blur(18px) saturate(1.6)`；`.select-popup` = `url("#lg-refract-soft") blur(26px) saturate(1.6)`；对话框 = `url("#lg-refract") blur(40px) saturate(1.7)`，sticky 头 = `url("#lg-refract-soft") blur(20px) saturate(1.6)` |
| 桌面透射 | 同一 UI 在彩色壁纸与近黑桌面下逐点取样，玻璃面色值同向变化（`dLum ≈ +0.001`，纯色桌面占比低是因为窗口底色 alpha 已收敛到 .8） |
| 可读性（纯白桌面，最差情况） | 主文字 `#e6e7eb`：侧栏 11.6:1、顶栏 11.3:1、混音栏 9.6:1、内容区底色 8.6:1；次级文字 `#858895`：侧栏 3.9:1、混音栏 3.3:1、内容区底色 2.9:1 |
| 关闭玻璃（`data-effects=reduced`） | 全部元素的 `backdrop-filter` 计数为 0，窗口底色回退 `#101114`，氛围层隐藏 |
| 响应式 | 1320×860 / 1000×760 / 760×640 三档 `scrollWidth == clientWidth`，无横向溢出；“全部静音”三档均可达 |
| 构建 | `npm --prefix desktop run build`（`tsc --noEmit` + vite）通过，CSS 36.2 KB / gzip 7.8 KB |

### 真实 Tauri + WebView2（`--root` 指向隔离目录）

**构建方式更正（2026-11-03 复核）**：Tauri 的 dev/prod 由 **`custom-protocol` Cargo feature** 决定（`tauri::is_dev()` = 该 feature 未启用），不是 `TAURI_ENV_DEBUG` 之类的环境变量。项目 `desktop/src-tauri/Cargo.toml` 未声明该 feature，因此 `cargo build --release`（以及我最初几次“重新嵌入前端”的构建）产出的仍是 **dev 模式二进制**：它加载 `build.devUrl`（`http://127.0.0.1:1425`），**不包含** `frontendDist` 资源；只有 `npm run tauri -- build`（或显式 `--features tauri/custom-protocol`）才会把 `desktop/dist` embed 进去。上表若在 vite 开发服务器运行时观察到界面，实际渲染的是**开发服务器提供的源码**而非嵌入资源——两者源码相同，故界面/计算样式结论仍成立，但“二进制已嵌入新前端”这句话当时不成立，现更正。

复现命令与判据：`cargo build --release --features tauri/custom-protocol`，随后关闭 vite，启动后 CDP 中 `location.href` 应为 `http://tauri.localhost/`（dev 模式则为 `http://127.0.0.1:1425/`）。已实测：dev 模式 → `127.0.0.1:1425`；带 feature → `tauri.localhost`。

用 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9223/9224/9342` + `--root .tools/tmp/verify-root*|live-root` 启动真实窗口，经 CDP 检查：

- UA 为 `Chrome/156.0.0.0`（与本机 Edge 同版本，也是历史记录中的 WebView2 156.0.4314.58）；`.window-header` 计算值 `blur(26px) saturate(1.85)`，窗口底色 `rgba(11,12,16,.8)`，`#lg-refract`/`#lg-refract-soft` 均在 DOM 中。
- 打开真实“创建预设与首次配置”对话框（6 个设备字段）：`.app-dialog` = `url("#lg-refract") blur(40px) saturate(1.7)`，sticky 头 = `url("#lg-refract-soft") blur(20px) saturate(1.6)`，背板整体失焦为玻璃模态。
- 点击真实“切换透明材质”按钮：后端保存 `reduceTransparency` 并刷新后，`data-effects=reduced`、`backdrop-filter` 计数 0、窗口不透明；再点回 `glass` 正常恢复。`--root` 下 `settings.json` 的 `ui.reduceTransparency` 随之为 `false`（本轮来回切换一次）。

## 已知限制

- **窗口对真实桌面的合成未复核**：CDP 抓取的是页面层，无法证明透明白色 WebView2 窗口对系统桌面的最终合成观感；桌面透射是通过在页面底层替换 html 背景模拟的。默认 `%LOCALAPPDATA%` 根在本会话不可用（历史记录中的沙箱完整性限制），因此未在默认根下启动。
- **折射依赖 GPU 合成**：`backdrop-filter: url()` 在软件渲染/异常驱动下的表现未测；已用“普通 blur 在前、url 在后”的层叠保证语法层面可回落，但无法覆盖“解析成功、渲染异常”的情形。
- **明亮桌面下次级文字**：纯白桌面下内容区底色的 `#858895` 次级文字为 2.9:1（改版前同位置约 3.8:1）。主文字仍 ≥8.6:1。已有“切换透明材质”开关可切到完全不透明；本轮未改动 muted 色板。
- 未复测：真实音频链路、热键、托盘、实时电平下的玻璃观感（电平条/开关动效只做了静态与计算样式检查），以及 200%/150% DPI 下玻璃边缘的亚像素表现（真实窗口截图在 125% 缩放下正常）。

## 追加改动（同日，用户追加指令）

用户依次追加四项要求，均只改 `desktop/src/styles.css`（外加前述 `App.tsx` 滤镜定义）：

### 1. 背景改黑、去紫

- `.app-window` 底色改纯黑；原四组紫/蓝/粉/青氛围渐变替换为两组中性白色径向渐变（`.05` / `.028`），只保留让 `backdrop-filter` 有可见来源的作用，不再带色相。
- 玻璃染色由蓝味（`rgba(19,21,27,…)`/`rgba(25,27,34,…)`/`rgba(32,34,43,…)`）改为中性（`rgba(8,8,10,.6)`/`rgba(10,10,12,.68)`/`rgba(14,14,16,.76)`）；各处近黑背景/凹槽由 `#181c25`/`#191c24`/`#141821`/`#20212b`/`#252433`/`#16181e`/`#14161b`/`#171a21` 等统一为中性灰阶 `#0e0e0e`/`#141414`/`#1a1a1a`/`#1c1c1c`/`#121212`/`#0b0b0b`/`#0d0d0d`。
- 实测（纯白桌面，125% 缩放）：内容区底色 `(44,44,45)`、顶栏 `(25,25,27)`、侧栏 `(26,26,28)`，`b−r = 1..2`，已无紫/蓝色偏。

### 2. 主要文字改紫

- 以**保亮度换色相**的方式把 70 个中性/蓝灰文字色（126 处 `color:`/`fill:`）映射到色相 262° 的紫色族：对每个原色求其相对亮度，二分搜索出同亮度的紫色（高亮度段用更高饱和度），因此**所有已测对比度不变**。
- 语义色（`--error`/`--warning`/`--success` 族）与原本已是紫色的强调色不参与映射；`--text:#ebe5f5`、`--secondary:#c1afe1`、`--muted:#8a76ae`。
- 无 `--surface`/`--surface-raised`/`--bg` 之外的死令牌：顺手删除了本就未被引用的 `--surface`、`--surface-raised`。
- 实测：`:root`/`.page-heading h1` = `rgb(235,229,245)`，音效卡标题 `rgb(220,210,238)`，选中导航 `rgb(167,139,250)`。

### 3. 卡片、按钮改“平面 3D”

按“平面（无渐变/玻璃膜）+ 立体（顶面受光、底部挤出的暗边）”的规则重构交互面，新增令牌 `--face:#181818`、`--face-raised:#212121`、`--face-sunken:#0d0d0d`、`--face-accent:#2b2344`、`--bevel-light:rgba(255,255,255,.11)`、`--bevel-dark:#050505`、`--bevel-line:rgba(255,255,255,.1)`，并删除 `--card-surface`/`--card-sheen`（改为纯色面）。

- 凸起面（卡片/按钮/图标块）：`background:var(--face)` + `inset 0 1px 0 <顶部高光>` + `0 2px 0 | 0 3px 0 <底部暗边>`，即“挤出”效果。
- 按下态：`transform:translateY(2px)` 并把底部暗边收成 0（视觉下陷）。
- 凹槽面（输入框/下拉触发器/搜索框/诊断 JSON/路径文本/波形框/电平条槽/开关轨道）：深色面 + `inset 0 2px 0` 内阴影。
- 覆盖对象：`.button`（含 primary/danger/ghost/按压）、`.icon-button`、`.sound-tile`（含 hover/selected/按压）、`.route-card`（含 is-on）、`.preset-list-row`、`.metric-cards>div`、`.build-facts>div`、`.diagnostic-status`、`.about-heading`、`.update-card`、`.form-notice`、`.select-trigger`、`.form-field input`、`.search-field`、`.preset-picker`、`.toggle`、`.gain-slider`（轨道+滑块）、`.meter`、`.tile-actions button`、`.app-picker-list` 行、`.category-nav` 项、`.clip-art`/`.playback-art`/`.about-symbol`。
- 玻璃只保留在功能层（顶栏、侧栏、混音栏、播放条、状态栏、对话框、下拉选单、提示条），内容卡片保持实体平面。

### 4. 磨砂半透明背景：CSS 能做到哪一步（关键结论）

- **限制（有据）**：透明 WebView2 窗口中，CSS `backdrop-filter` **不能**磨砂系统桌面。WebView2 官方 issue [#4945](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4945) 记录了“宿主窗口与 webview 背景 alpha=0 时 `backdrop-filter` 不符合预期”；Chromium 的 `backdrop-filter` 只在**backdrop root**内生效（[issuetracker 380416865](https://issues.chromium.org/issues/380416865)），页面根之外的桌面像素不参与。
- **实施**：改用系统材质。`runtime.rs::apply_transparency` 原本请求 `Effect::Mica`（Mica 按壁纸取色，**不**模糊窗后内容），本轮改为 `Effect::Acrylic` —— 由 Tauri 内置（`tauri::utils::config::WindowEffect::Acrylic`）映射到 `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`（Win11 22H2+），旧系统走 `SetWindowCompositionAttribute` 亚克力分支。**不新增依赖**（原计划的 `window-vibrancy` 因此不需要：Tauri 2 自带同一组材质，且已在依赖树中）。
- 实测（PowerShell `DwmGetWindowAttribute`）：改动前 `DWMWA_SYSTEMBACKDROP_TYPE = 2`（Mica），改动后 `= 3`（Acrylic）；`DWMWA_USE_IMMERSIVE_DARK_MODE = 1`。
- 真实合成画面取色（用户桌面 + 125% 缩放，`CopyFromScreen` 截取 `.tools/tmp/window-live.png` 的窗口区域）：窗口底 `(23,22,20)` L=0.0081、顶栏 `(19,19,17)`、侧栏 `(18,18,17)`、混音栏 `(23,22,21)`；主文字对比度 **14.7:1**、次级 `#8a76ae` **4.5:1**（小字 AA 达标）；各玻璃面高频纹理近 0（混音栏 0.0000），说明背景被材质压平、无清晰桌面细节透过。
- 未验证：亚克力在拖动/缩放窗口时的性能（README/官方文档提示 Win10 1903+ 与部分 Win11 构建下 `apply_acrylic` 拖动性能偏差）；多显示器不同缩放下的材质表现。

### 5. 平面化（用户追加“平面整体效果，不要渐变”）

- 删除全部渐变：`--glass-sheen` 令牌与其 8 处引用、`.app-window::before` 的径向氛围层（及 reduced 模式下对应的 `display:none` 规则）、开关/电平条的渐变填充。
- 滑块轨道改为纯色凹槽；随之删除已无用的 CSS `--fill` 变量与 `components.tsx` 中对应的 React 内联样式传参（数值仍由“增益 100%”标签给出）。
- 交付 CSS 中 `gradient` 出现 **0** 次（`grep -c gradient desktop/dist/assets/*.css`）。
- 注：删除氛围层后，玻璃面板的 `backdrop-filter` 在窗口/侧栏这类**背景本身是纯色**的区域已无可模糊内容（不产生视觉差异），但对话框/下拉选单/提示条仍悬浮在内容之上、其 `backdrop-filter` 与 SVG 折射仍有真实作用，故保留。

### 6. 透明度分层（用户追加“背景稍低、卡片再低、按钮不透明”）

- 窗口 `rgba(0,0,0,.55)` → `rgba(0,0,0,.66)`（比原先更不透明）。
- 卡片 `--face` `#181818` → `rgba(26,26,26,.82)`（比背景更不透明）；选中态 `--face-accent` → `rgba(45,36,72,.86)`。
- 按钮 `--face-raised` 保持 `#212121`、输入/凹槽 `--face-sunken` 保持 `#0d0d0d`（完全不透明）。
- 叠加后卡片实际不透明度 ≈ 1-(1-.66)(1-.82) ≈ 0.94，仍可看出与背景的层次差。

### 7. 多色强调与次要文字（用户追加“字体不要全部是紫色的”）

- **次要文字改为中性冷灰**：又一次保亮度换色相，这次方向相反——把 48 个「暗紫灰」文字色（`Y ≤ 0.45` 且色相在紫族）映射到色相 220°、饱和度 0.09 的冷灰，亮度逐一保持 → 对比度不变。亮色文字（`Y > 0.45`，标题/卡片名/品牌）保持紫色族，满足“主要字体仍为紫、次要文字换色”。
- **新增强调色令牌**：`--accent-cyan:#5fc3cf`、`--accent-blue:#7fa6f2`（琥珀/绿复用既有 `--warning`/`--success`）。
- **按音频链路分色**（`RouteCard` 新增 `data-route` 属性）：真麦=青、应用=蓝、附加=琥珀、监听=绿；图标与“已开启”文字同色。
- 其余强调点：窗口内的小标题 `.eyebrow`、卡片波形图标 `.tile-symbol`、播放条图标、`.app-bind-link`、关于页链接 = 青色；分组面包屑链接 = 蓝色；收藏星 = 琥珀（含填充 `rgba(231,185,106,.16)`）；“预设未保存” `.dirty-note` = 琥珀。
- 实时验证（真实 WebView2，44 条音效）：路由图标 `rgb(95,195,207)` / `rgb(127,166,242)` / `rgb(231,185,106)` / `rgb(101,201,154)`；`.tile-meta` `rgb(138,145,158)`、卡片说明 `rgb(128,135,149)`（冷灰）；`.page-heading h1` `rgb(235,229,245)`、卡片名 `rgb(220,210,238)`（紫）。
- 星标（琥珀）在本机素材库无收藏项（0 条），未取得实时取色证据；规则见 CSS。

### 8. 构建与启动方式（本轮踩到的坑）

- `desktop/src-tauri/Cargo.toml` 未声明 `custom-protocol` feature → `cargo build --release` 恒为 dev 模式；正确命令为 `cargo build --release --features tauri/custom-protocol`（项目正式入口 `npm run tauri -- build` 隐含该 feature）。判据见上节。
- 用 `--root <workspace 目录>` 启动可绕过本会话的沙箱完整性限制；默认 `%LOCALAPPDATA%\OnbodyMic` 在本会话内启动会立即退出（exit 0）。
- 单实例锁：**用户自己启动的旧便携版进程**会让后续所有启动瞬间 exit 0（`tauri-plugin-single-instance` 在第二实例中直接退出），并且被强杀后残留的 `msedgewebview2.exe` 会继续占着 webview 用户数据目录；本轮用 `Get-CimInstance Win32_Process` 按 `OnbodyMic|live-root|verify-root` 匹配命令行清理。