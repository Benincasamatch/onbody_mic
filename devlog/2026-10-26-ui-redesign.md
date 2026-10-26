# 2026-10-26 UI 重设计（随声麦_UI重设计方案.md v1.0） 开发日志

## 本轮范围

用户指令"根据 docs/随声麦_UI重设计方案.md 重新制作 UI"。方案 §18.1 明确首选路线：留在现有框架（WinForms）、统一主题变量、布局容器替代绝对定位、拆分逻辑区域、状态模型驱动控件、沿用原有业务接口。本轮按此实现 P0+P1：状态语义统一、参数按路径分组、紧急静音语义明确（改名"全部静音"、状态由轮询快照驱动）、视觉规范统一、主窗口重排、音效库列表优化。§18.5 约束全部遵守：未换框架、未推倒重写音频层、未改配置格式、未引入自绘替代系统控件、未加动画。

## 技术决策与事实

1. **框架确认**：WinForms（P3 决策 G07 延续，本地 SDK 零新增依赖）。方案 §18.3 的 WPF 建议不适用——换框架属 §18.5 明确禁止的"框架未确认时直接迁移"，且本项目无网络拉取 WPF 依赖的条件。
2. **Theme.cs 设计令牌**（方案 §5/§16 ThemeTokens）：色板（浅色主题 12 个语义色：#F7F8FA 主背景/#FFFFFF 卡片/#2563EB 主色/#B91C1C 错误等）、间距常量（4/8 节奏、卡片内边距 16、分区 24）、Primary/Secondary/Danger 按钮工厂、StatusChip 胶囊、SectionTitle、CardBorder（1px 边框，Paint 事件绘制）。所有样式常量集中一处，无颜色散落在控件代码里。
3. **布局根因修复**：首版用 Dock=Top + AutoScroll 面板，WinForms 的 Dock=Top 是"后添加者先停靠"（视觉顺序与添加顺序相反），导致区段全部倒序堆叠；且 Panel+Dock 子控件在 AutoSize 下不报告高度（卡片高度塌缩为 0）。改用单列 TableLayoutPanel（6 行 AutoSize）作为内容栈，卡片本体也改 TableLayoutPanel——从 AutoSize 行内的 Dock=Fill 子控件正确推导高度。截图取证确认修复前后差异。
4. **状态芯片（方案 §7）**：总体状态/麦克风/附加音频/应用音频/监听/音效 6 枚胶囊，文字+语义色双通道（§2.5 不以颜色为唯一信息）。数据源单一：500ms 轮询的 SessionStatus 快照，UI 永不伪造开启态（§7.4——用户点击开关后标签仍由下一次轮询刷新，拒绝请求走 MessageBox）。
5. **路由卡片（方案 §8）**：真实麦克风（输入设备友好名+发送开关+输入增益）、附加音频（应用绑定描述+附加开关+应用捕获开关+应用增益；应用开关在附加关闭时禁用——引擎语义 extras-disabled）、本地监听（模式三选+音量滑块+数值）、音效输出（播放状态+停止+音效增益）。每条路径的参数就地分组（§2.2 功能就近）。设备名经 WindowsAudioDevices.List 映射 FriendlyName，缺失显示"设备不可用（未检测到）"（§8.2 设备缺失保留卡片显示异常）。
6. **音效库（方案 §9）**：搜索框（TextChanged 即时过滤，Search(query, favoritesOnly) 覆盖名称子串）+收藏筛选复选框+导入按钮；列表从 ListBox 改 ListView 四列（名称 320/时长 90/分组 110/收藏 50，DoubleClick=播放）；空态文案区分"尚无音效，导入音频文件以开始使用"vs"没有匹配的音效"（§9.3）。搜索不改底层库数据（只读过滤）。
7. **热键摘要（方案 §10）**：主窗口只显示绑定摘要（前 6 条中文动作名，超出折叠 +N 项，ToolTip 展示全量），"设置热键"按钮启动 CLI 面板进程——桌面热键编辑 UI 是 P3 已登记的已知缺口（docs/05 §8），本轮不扩scope（方案 §16 组件清单中 HotkeyEditor 为独立组件，按用户反馈决定是否补键捕获控件）。
8. **紧急静音（方案 §6.3）**：文案从"紧急全静音"改"全部静音"（方案点名该文案含义不清），按钮红色 Danger 样式与配置操作区分；锁存态显示"解除静音（保持全关）"——明确解除后不自动恢复发送（既有安全语义，方案 §6.3"若不会恢复则不要暗示可切换"的正确表述）。
9. **提示与工具提示（方案 §5.4/§13）**：ToolTip 覆盖紧急静音/搜索/收藏筛选/导入按钮与热键摘要全量列表；删除预设/删除音效保留既有确认对话框（含预设引用提示）。

## 工程成果

```text
src/OnbodyMic.Desktop/Theme.cs（新）  设计令牌：色板/间距/字体/Card/CardBorder/
                                      Primary·Secondary·Danger 按钮/StatusChip/
                                      SectionTitle/Note
src/OnbodyMic.Desktop/MainForm.cs     重写 BuildLayout 体系：单列 TableLayoutPanel
                                      内容栈（顶栏/状态芯片/路由卡片/音效库/热键卡/
                                      状态栏）；MicDeviceName 设备友好名映射；音效库
                                      搜索+收藏筛选+ListView 四列+空态区分；热键中文
                                      摘要+CLI 面板启动；默认 960×760/最小 760×640
                                      （方案 §4.1）；其余事件处理/命令调用与旧版逐字
                                      相同（业务行为零改动）
CHANGELOG.md                          [未发布] 节：UI 重设计条目
```

## 验证（全部实测）

| 项 | 结果 |
| --- | --- |
| 全解决方案构建 | 0 警告 0 错误 |
| 全量自动化测试 | 181/181 通过（Domain 45/Audio 28/Persistence 29/Media 19/Application 60），UI 改动未触音频层零回归 |
| desktop --selftest | restored=False→OK（空根）；带预设根：phase/switches/devices 正确 |
| GUI 真机冒烟（ENV-001） | PrintWindow 截图取证四轮：①首次引导向导正常渲染；②倒序布局缺陷确认；③修复后区段顺序正确；④最终版 768×608（DPI 缩放下）四卡片+芯片行+设备友好名"头戴式麦克风 (Odo By VECLAN.COM)"全部就位 |
| 预设恢复链（T24） | settings.json lastPresetId → 启动恢复"开黑默认"→ 标题栏"随声麦 — 开黑默认"（日志：已应用预设，发送/监听全部关闭） |

## 遗留与限制（不冒充已验证）

1. **用户视觉/交互试用未做**：方案 §20.3 的多 DPI/多窗口尺寸清单、键盘导航全路径、真实开黑操作流，需用户实际试用反馈（与 P3 遗留 1 同性质）。
2. 深色模式（方案 §5.1 预留语义变量已就位，但未实现第二主题）、诊断面板 UI（日志仅文件）、音效行内"更多"菜单（重命名/分组编辑仍走 CLI 面板）——均按方案 P2/P3 优先级暂缓。
3. 桌面热键编辑 UI 缺口延续（P3 遗留 2）：摘要卡提供 CLI 面板启动入口过渡。
4. 方案 §8.6 的"滑块拖动结束才应用"节流未做：现有监听音量滑块拖动时实时提交 SetGains（命令队列串行化，500ms 轮询刷新回显），P4 soak 已证实时增益调整无音频中断；如用户实测发现拖动卡顿再引入。
5. 截图取证用 PrintWindow 后台捕获（首版 SetForegroundWindow 抢焦点失败抓错窗口）。

## Git

提交见 `git log`（feat(ui): …）。下一步：用户试用反馈（尤其方案 §20.3 视觉清单与操作效率验收）。

---

## 第二轮：P0 清单落地（用户 UI 修改清单）

### 实测取证（修改前）

用 `PrintWindow` 抓取修复前的真实窗口并枚举子控件坐标，四项缺陷全部复现：

| 缺陷 | 取证 |
| --- | --- |
| 顶栏拥挤/紧急按钮被裁 | 默认 960 宽时「全部静音」右缘 1076 > 客户区 942；最小尺寸 760 时「设备与监听设置…」与紧急按钮整体在窗口外 |
| 内容横向溢出 | 内容栈宽 730 > 视口 667，卡片右侧被裁（「音效增益」标签被切） |
| DPI 缩放错配 | 字体按点（随 DPI 放大），内边距/宽度按像素（不放大），125% 下按钮高 44px 而文本更宽 |
| 卡片行高不齐 | 附加音频卡片比麦克风卡片高 35px，行内控件未垂直居中 |

### 根因与修复

1. **DPI 统一**：`Theme` 改为 DIP 令牌，字体用 `GraphicsUnit.Pixel` 由同一 `D(x)` 因子换算（13 DIP 正文 / 12 辅助 / 15 卡片标题 / 17 分区 / 20 应用名）。窗体 `AutoScaleMode.None` —— 运行时控件不参与 WinForms 自动缩放，缩放只由 Theme 负责，避免两套缩放叠加。
2. **横向不溢出**：内容栈 `Dock=Top` + `AutoSize` 放在 `AutoScroll` 面板内，宽度恒等于视口。审计断言 `_stack.Width <= viewport`。
3. **顶栏三段式**：`TableLayoutPanel(3 列)` = 标题 | 配置簇 | 紧急按钮。配置簇 `WrapContents=false`；宽度不足时按 `未保存标记 → 设备设置 → 保存 → 应用预设 → 配置管理` 顺序隐藏（被隐藏项全部在「配置管理」菜单中可达），最后才压缩预设下拉框宽度。**没有采用换行**：AutoSize 表格测量换行 FlowLayoutPanel 时使用近零宽度，会把整行撑成 7 行高（实测 294px 空白顶栏），这是本轮最耗时的坑。
4. **卡片统一**：`RouteCard` 固定骨架 —— 标题 + 状态胶囊 + 单行省略说明（完整文本进 ToolTip）+ `标签 | 控件` 参数行。末尾保留 100% 行吸收「与更高邻卡对齐」产生的余量，否则余量会被 AutoSize 行分摊成行间空隙（实测 106px 行高）。参数行 `WrapContents=false`，避免换行流报告两行高。
5. **响应式**：客户区宽度 − 2×边距 ≥ 720 DIP 用两列，否则单列（`PlaceRouteCards` 重建列/行样式）。
6. **状态真实性**：`Run()` 只报告拒绝；`Toggle`/`ToggleEmergency`/`Gain`/`Play` 额外回读 `Session.Status()`，不一致时弹「未生效」并给出可能原因。`Rejections.Describe/NextStep` 把 20+ 拒绝码映射为中文原因 + 处理入口，窗口与托盘共用同一份文案。
7. **异步**：`DeviceCatalog` 后台扫描 + 缓存 + 失败回退（WASAPI 枚举约 40ms/次，原来在 500ms 轮询里同步调用）；导入/播放解码走 `Task.Run`；导入期间禁用按钮并显示「导入中…」。
8. **热键编辑 UI**（P3 遗留缺口补上）：`HotkeyEditorForm` 复用 `Soundboard.BindHotkey/UnbindHotkey`（新增公共方法，内部走同一 `HotkeyDispatcher` 与 `settings.json` 持久化），录制时 `SuspendHotkeys()` 防止误触发；修饰键从对话框自身的 KeyDown/KeyUp 跟踪（不依赖全局键盘状态，`SetForegroundWindow` 在无人值守环境下会被系统拒绝）。

### 验证（全部实测，非推断）

| 项 | 结果 |
| --- | --- |
| 全解决方案构建 | 0 警告 0 错误 |
| 全量自动化测试 | 181/181 通过（Domain 45 / Audio 28 / Persistence 29 / Media 19 / Application 60） |
| 布局审计 `--selftest` | 96/120/144/192 DPI × 1000×780/860×640/760×560 共 12 组全部通过（无横向裁切、无父容器越界、按钮文本不截断、紧急按钮可见、卡片列数符合规则）；审计自检注入缺陷能被检出 |
| 紧急静音真机 | 点击后锁存：芯片「紧急静音已锁存」、四卡状态变「锁存」、所有开关禁用、状态栏同步；解除后保持全关 |
| 热键端到端 | 录制 Ctrl+F7 → 绑定 → `settings.json` 写入 `toggleMicSend: "Ctrl+F7"` → 列表显示 → 重启后日志 `[hotkey] 已恢复 toggle-mic-send = Ctrl+F7` |
| 音效导入 | 真实 wav 导入 → `library/index.json` 新增条目（时长 2860ms）→ 列表显示「2.9 秒」 |
| 音效播放 | 试听：芯片「音效 试听中」、卡片「试听中 / 本地试听：loop_r0_c0」、行内「▶ 试听中」 |
| 失败提示 | 附加音频关闭时点「向队友播放」→ 弹「向队友播放失败：需要先开启「附加音频」发送…处理：…」，未显示成功 |
| 设备设置 | 异步扫描到 8 个端点，标签列自适应不再裁字；「刷新设备」可用；保存后预设内容逐字段比对无变化（未改动时幂等） |

### 遗留

1. 深色模式、诊断面板 UI、音效分组编辑仍未做（方案 P2/P3 优先级）。
2. 音效播放进度条：引擎只暴露帧位置但不推送，未加进度条（避免虚假进度）。
3. 真实音频路径（虚拟线缆 + KOOK）本轮未重跑，仅验证 UI 与既有命令面接线；音频层零改动。

---

## 第三轮：便携包构建 + 仓库上传

### 打包

- **此前打包是手工命令**（P4 只记录了产物大小，没有脚本），不可复现。本轮新增 `build/build-portable.ps1`：读 `Directory.Build.props` 的 `Version` → `dotnet publish -c Release -r win-x64 --self-contained -p:DebugType=none` → 复制包内文档 → **对发布产物运行 `--selftest`** → 打 zip。任一步失败即 `throw`，不会产出未验证的包。
- **用 PowerShell 而非批处理**：包内是中文文件名，`cmd.exe` 按当前代码页解码，实测把 `使用说明.md` 写成乱码、`开始使用.txt` 静默丢失。改为 PowerShell 后文件名正确。
- **PowerShell 5.1 的 BOM 陷阱**：无 BOM 的 `.ps1` 按 ANSI 解码，中文字面量（`Join-Path $out '开始使用.txt'`）被破坏，`Copy-Item` 静默不复制。写入 UTF-8 BOM 后修复——这是本轮第二个"不报错但结果错"的坑。
- **产物**：`dist/win-x64/` 120MB / 291 条目 / zip 48.1MB（旧包 48.2MB 且含 PDB、无文档）。

### 实测验证

| 项 | 结果 |
| --- | --- |
| 脚本端到端 | `build-portable.ps1` 一次跑通，输出 "Package selftest OK" |
| 发布产物自检 | `--selftest` → `layout OK` / `selftest: OK` |
| zip 内容 | 291 条目全在 `win-x64/` 下；4 份文档；0 个 PDB |
| 解压后独立运行 | 从 zip 解压到独立目录 → `--selftest` 通过 |
| 残留进程 | 0（自检正常退出，不再占用文件句柄） |

### 上传

`github.com:443` 在本机间歇性不可达（`api.github.com` 与 `ssh.github.com:443` 正常），重试后恢复。远程 `main` 在 `f448d3c`（P1），本地领先 16 个提交。

### 发布（GitHub Release v0.1.0）

- `git push` 初始全部失败（`Failed to connect to github.com:443`），而 `Test-NetConnection` 报端口可达——两者路径不同：系统代理 `127.0.0.1:7892` 已启用（`ProxyEnable=1`），`git` 未配代理所以直连被阻断，`Invoke-WebRequest` 默认读系统代理所以能通。**根因不是网络抖动，是代理配置缺口**。执行 `git config http.proxy https.proxy` 后推送成功（`f448d3c..a41846b`）。
- 发布流程同样踩到编码问题：`.ps1` 内嵌中文 release body 在无 BOM 时被按 ANSI 解码，`ConvertTo-Json` 前的哈希字面量直接语法错误。改为**中文内容走独立 UTF-8 JSON 文件**、脚本只读字节发送，彻底绕开脚本编码问题。
- Release `v0.1.0` + 资产 `OnbodyMic-0.1.0-portable-x64.zip`（50,426,401 B）已上传。

| 验证项 | 结果 |
| --- | --- |
| 本地 ↔ 远程 SHA256 | `842a1316…d776b3` 完全一致 |
| zip 完整性（`testzip`） | OK，291 条目 |
| 下载后解压运行 | `--selftest` → `layout OK` / `selftest: OK` |
| 发布状态 | `draft=false`，已 publish |

---

## 第四轮：干净机器「没有虚拟麦克风输出」（用户实测报告）

### 症状与根因

用户报告：干净机器上程序能启动，但队友端收不到声音。

**根因不是音频引擎，是端点可见性。** 干净机器上 Steam Streaming Speakers 的虚拟麦 Capture 端点默认状态是 `Disabled`。而所有 UI 路径都在用 `WindowsAudioDevices.List(activeOnly: true)`：

| 位置 | 后果 |
| --- | --- |
| `DeviceCatalog`（主窗/设备设置共享） | 禁用端点不进列表 |
| `FirstRunWizard.Fill` | 首跑向导里根本选不到虚拟麦 |
| `DeviceSettingsForm.LoadDevicesAsync` | 设置页也看不到，无法启用 |
| `AppShell.ValidateDevices` | 把它判为 `endpoint-missing`，提示「未检测到」——误导用户去找驱动 |

即：用户有一个"存在但被禁用"的虚拟麦克风，而界面既不显示、也不提示、也无法启用。文档里写的 `enable --id` 是唯一出路，但那是命令行操作。

### 修复

1. `DeviceCatalog` 保留全部端点（`activeOnly: false`），新增 `IsActive`/`IsAvailable`/`Find`/`TryEnable`；调用方按 `State` 自行过滤。
2. `DeviceSettingsForm` 新增**「启用选中端点」**：走 `WindowsEndpointControl.SetEnabled`（IPolicyConfig，与声音设置同一调用），启用后重扫并刷新共享 catalog。端点在下拉框标注「已禁用 — 选中后点「启用选中端点」」。
3. `FirstRunWizard` 列出禁用端点但**绝不预选**（否则会建出静默失效的预设），标签标注「已禁用」。
4. 主窗口顶部新增明确诊断，直接点名症状与处理入口：
   `虚拟麦 Capture 已禁用（内部 AUX 插座）：队友端收不到声音。处理：打开「设备设置…」，选中该端点后点「启用选中端点」。`
5. `ValidateDevices` 区分 `endpoint-disabled`（可启用）与 `endpoint-missing`（驱动缺失）。
6. 修复 CLI `devices` 参数语义反转：`--all` 曾等价于「仅可用端点」，与文档相反。

### 实测（本机真实复现）

用 `OnbodyMic.Cli.exe disable --id <AUX>` 把本机 AUX Capture 置为 `Disabled`，真实复现干净机器状态，然后逐项验证：

| 项 | 结果 |
| --- | --- |
| 主窗口诊断 | 截图确认顶部显示「虚拟麦 Capture 已禁用（内部 AUX 插座）…队友端收不到声音」+ 处理路径 |
| 预设校验日志 | `失效: VirtualCapture:endpoint-disabled`（修复前是误导性的 `endpoint-missing`） |
| IPolicyConfig 启用 | `enable --id` → 状态 `Disabled → Active` 实测成功 |
| 端点恢复 | 测试后已把本机 AUX 恢复为 Active |
| 全量测试 | 181/181 通过 |
| 布局审计 | 扩展到**对话框**（首次引导/设备设置/热键设置）× 4 档 DPI，全部通过 |

### 顺带修掉的三个真实缺陷（审计发现，肉眼截图漏掉）

1. **首跑向导裁切**：固定 560px 宽 + 固定 320px 下拉框 → 端点全名被截、底部按钮被裁。改为可缩放 680×480、标签列 AutoSize、按钮按令牌给最小宽度。
2. **首跑向导单元格错位**：`layout.Controls.Add(...)` 不指定行列，表格按添加顺序分配单元格，导致「预设名称」标签与输入框分处两行。改为显式 `(column, row)` 定位 + 末尾 100% 弹性行吸收多余高度。
3. **热键设置底部溢出**：外层表格给编辑卡片固定 190px 行高，加了内边距后操作行与状态文字溢出父容器。改为 AutoSize 行 + 窗口 680×560。

### 教训

「能启动」不等于「能用」。这轮的缺陷全部属于**沉默失败**：端点被过滤后，UI 不报错、不提示、按钮可点但无效果。审计只覆盖主窗口时完全看不到；把对话框纳入审计后，一次就抓出三处裁切。
