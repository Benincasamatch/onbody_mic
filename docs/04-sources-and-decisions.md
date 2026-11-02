# Onbody Mic 来源与决策登记

版本：文档基线 v1.1；补充软件 0.2.0 的局部 Rust 迁移、原生桌面和实际依赖。原始技术网页证据仍为前期规划核查，不表示本轮重新验证全部平台能力；本轮组件版本以锁文件、已安装包清单和构建结果为准。个人本地试用与公开分发的边界仍按 D17 执行。

## 1. 用户来源

### U0：最初目标

用户要求制作虚拟麦克风项目：指定应用的声音或导入音频，通过虚拟麦克风播放；暂名 `onbody_mic`；先完善需求并制定尽可能详细的计划。

### U1：功能澄清

用户明确说明：发送给 KOOK 等语音软件，Windows 首发但保留全平台可能；捕获浏览器/播放器等实时应用，直接播放音频文件；短音效收藏与热键；实时低延迟、严格防误传；与真实麦克风同时运行、可本地监听；应用级而非应用内部细分；热键/按钮控制；多个预设并记录最后使用项；主要用于开黑；必须模块化。

上述是本项目的直接功能来源；用户没有提供实际设备型号、浏览器/播放器名称、KOOK 当前发言模式、音效数量或测试结果。

### U2：采用建议并完成文档

用户后续要求：“按照建议来，创建完整文档，并写清楚注意事项”。据此将此前建议收敛为 v1.0 文档基线：一个应用 + 真麦 + 一条短音效；替换/重播策略；安全启动和预设切换；仅音效偏好与实际端点监听；统一安全控制；模块化分阶段交付。

这授权本轮完善文档，不授权直接编码、安装驱动、改系统音频、加入语音频道、公开分发或推断缺失环境事实。接口/数据/故障行为是为落实基线做的工程细化，不冒充用户逐字提出的技术规格。

### U3：P5 待实现功能登记（2026-11-01）

用户要求：抓取浏览器网页中的音频内容，或者导入音频；导入后可以裁剪，成品放入音效库。

处理：登记为 P5A（采集）与 P5B（导入/裁剪入库），写清范围与门槛，见 [03](03-roadmap-and-acceptance.md)。采集复用整应用进程树，不承诺标签页级，不做系统全量混音；每次显式开始，不采麦克风/通话，不绕过 DRM/受保护媒体，全程本地、不上传；采集草稿不进入发送、监听或试听。本条不改变 v1.0 首发行为，也不解除 P5“不自动进入”的确认规则。

### U4：局部 Rust 迁移与音效台

用户要求“无需全部迁移，优先迁移难度不高的模块，但前端界面必须重构”，保留设计功能而非现有架构；随后选择 Tauri 2 + React/TypeScript 路线并采用音效台布局，再要求继续实施。由此迁移配置/预设和原生壳，真实音频、素材库与系统热键保留 C# 并经无界面后端接入；不将 U3 的待实现功能自动纳入本版。

## 2. 官方技术来源

### S01：Windows 进程音频捕获

- 来源：[Microsoft Learn - Application loopback audio capture](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- 已核实：可捕获指定进程及子进程，或排除该进程树；目标没有音频渲染流时得到静音；样例标注最低 Windows build 20348；该机制不绑定单一物理播放端点。
- 不能据此证明：任意浏览器全部窗口恰好对应单一进程树、受保护内容一定可捕获、具体延迟、所有 Windows 10 支持。
- 规划用途：应用捕获的真实粒度与能力检查；禁止退化为系统混音却不告知。

### S02：VB-CABLE 设备方向与安装

- 来源：[VB-Audio - VB-CABLE](https://vb-audio.com/Cable/)
- 已核实：音频从 CABLE input 传到 CABLE output；页面说明输入是播放设备侧，输出是录音设备侧；Windows 安装说明要求管理员运行安装器并在安装/卸载后重启。
- 不能据此证明：独占写入、防止其他应用向端点送声、零延迟、KOOK 的全部功能兼容。
- 规划用途：Onbody Mic 写入播放侧，KOOK 选择录音侧；设备名与产品名区分。

### S03：VB-Audio 分发许可

- 来源：[VB-Audio - Licensing](https://vb-audio.com/Services/licensing.htm)
- 已核实：官方建议可把单独安装作为先决条件；基础 VB-CABLE 在捐赠模式适用及相应标识等条件下可随产品分发；专业领域存在不同付费要求；A+B/C+D 不允许按基础版方式捆绑分发。
- 限制：这是对公开条款的工程摘录，不是对未来具体商业模式的法律结论。发布时重新核查。
- 规划用途：初版不默认捆绑，也不错误宣称“任何形式都禁止捆绑”。

### S04：NAudio 已发布版本、模块与许可

- 来源：[NAudio v3.3.0 发布](https://github.com/naudio/NAudio/releases/tag/v3.3.0)
- 来源：[v3.3.0 README](https://github.com/naudio/NAudio/blob/v3.3.0/README.md)
- 核查方式：GitHub 官方 release API 返回 `v3.3.0`、`prerelease=false`、发布时间 `2026-10-19T21:25:59Z`；通过官方 contents API 读取该标签的 README，避免只依赖未发布主分支。
- 已核实：NAudio 3 最低 `net9.0`；有跨平台核心包和 Windows-only WASAPI 后端；README 声明支持 per-process loopback；MIT 许可；macOS CoreAudio 后端尚非已完成的可依赖支持。
- 不能据此证明：本项目所有接口已经可用或已编译、无性能/资源问题、macOS 设备支持已完成。
- 规划用途：Windows 适配器候选与版本验证门槛，不作为已集成声明。

### S05：Avalonia 平台与许可

- 来源：[Avalonia - Supported platforms](https://docs.avaloniaui.net/docs/overview/supported-platforms)
- 来源：[Avalonia MIT licence](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md)
- 已核实：支持多个平台的 UI 目标，具体版本支持分层；许可证为 MIT。
- 不能据此证明：系统级音频捕获、虚拟录音设备、热键权限在各平台完全一致。
- 规划用途：共享 UI 候选，避免 Windows-only 界面作为未来跨平台必须重写的部分。

### S06：.NET LTS

- 来源：[Microsoft - .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- 已核实：当前支持表列 .NET 10 为 LTS；LTS 支持期为三年，处于支持期也需要跟进适用补丁。
- 规划用途：新项目运行时建议。具体 SDK/补丁版本在实施时再次核查，不触发本机全局升级。

### S07：KOOK 输入设置与增益

- 来源：[KOOK - 麦克风没有声音或找不到设备时怎么办](https://support.kookapp.cn/6a8b/0616)
- 来源：[KOOK - 麦克风音量被自动调节了怎么办](https://support.kookapp.cn/6a8b/97a9)
- 已核实：PC 客户端可在语音设置中切换输入设备；排错文档给出系统录制设备选择 48000 Hz 的建议；声音增益可能造成麦克风音量自动调节，官方建议相应场景尝试关闭。
- 不能据此证明：所有音效能穿过降噪/语音门控、能绕过 KOOK 按键说话、存在可安全操控 KOOK 的公开本地 API。
- 规划用途：用户引导与兼容性验收。页面另有与本项目无关的排错建议，不应直接照搬为关闭安全保护的方案。

### S08：Windows 全局热键

- 来源：[Microsoft Learn - RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- 已核实：注册可能因热键已被占用失败；`MOD_NOREPEAT` 可避免按住重复触发；F12 是保留调试键，不应注册。
- 不能据此证明：所有游戏、权限级别和反作弊环境均接受相同热键方案。
- 规划用途：热键服务、冲突提示和游戏实测，不采用模拟键盘注入作为默认实现。

### S09：SysVAD 样例限制

- 来源：[Microsoft Windows-driver-samples - SysVAD README](https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/README.md)
- 已核实：示例展示虚拟音频驱动架构，但文档明确不实现混音，捕获/loopback 使用生成音调模拟。
- 规划用途：阻止把样例当成现成成品驱动；自研驱动需独立评估。

### S10：驱动签名要求

- 来源：[Microsoft Learn - Driver code signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs)
- 已核实：驱动提交与签名有证书要求；attestation/WHCP 相关提交要求 Hardware Dev Center 账号关联至少一个 EV 证书。
- 规划用途：自有驱动不是简单改名编译；正式分发需要独立流程与成本核查。

## 3. 决策状态

| ID | 事项 | 当前状态 | 改变条件 |
| --- | --- | --- | --- |
| D01 | Windows 首发，KOOK 优先 | 已确认 [U1] | 用户调整场景 |
| D02 | 模块化，预留其他平台 | 已确认 [U1] | 每个平台另做能力验证 |
| D03 | `onbody_mic`，工作名 Onbody Mic / 随声麦 | 内部工作名已采用 [U2] | 公开分发前查重，不承诺商标可用 |
| D04 | 原生前端与局部 Rust 迁移 | 0.2.0 已实现 Tauri 2 + React/TypeScript 音效台、Rust 配置/预设；C# 音频后端保留 [U4] | 平台或音频迁移独立验证，不以 UI 框架代替音频能力 |
| D05 | NAudio Windows 适配器 | 当前后端使用 2.2.1；3.3.0 是前期候选而非已集成版本 | 更换版本需重新验证 API/性能/资源释放 |
| D06 | 用户已有虚拟线缆，驱动独立安装 | 本地实现与实测使用 Steam Streaming Speakers；本轮没有安装或捆绑驱动 | 未测驱动/机器需单独验证，不外推 KOOK 接收结果 |
| D07 | 真麦与应用/音效统一混合给 KOOK | 并行能力 [U1]，混音拓扑基线 [U2] | 核心路由变更需同步规格和测试 |
| D08 | 恢复预设但不恢复发送/监听 | 已采用 [U2]；所有预设应用均关闭后重新启用 | 不因只改音量跳过安全流程 |
| D09 | 一应用 + 一真麦 + 一音效，替换/重播、不循环 | 已采用 [U2] | 多应用、叠加或长播放独立范围变更 |
| D10 | 实际端点与仅音效监听互斥，启动关闭 | 已采用 [U2]；默认偏好仅音效，路径待实测 | 不以驱动前取样替代端点验收 |
| D11 | 不默认录音/上传/修改其他应用配置 | 已采用安全基线 [U2] | 扩展需明确授权与隐私审查 |
| D12 | 三发送开关、独立停止、紧急静音锁存 | 工程细化，按 01/06 实现 | 显式解除也只回就绪静音 |
| D13 | 复制音效入受管库，原件不改，收藏库级共享 | 已采用 [U2] | 删除/迁移不得破坏原文件与其他引用 |
| D14 | 初始不绑定任何组合键，主动配置并测冲突 | 工程细化 | 不占游戏常用键，不注入游戏 |
| D15 | 基础搜索/单级分组/音量/预设 CRUD/托盘 | 收敛首发范围 [U2] | 裁减要记录，不把高级编辑默默加入 |
| D16 | 显式保存预设、版本化配置、原子写入和备份 | 工程细化 | 保存失败不假装成功，不恢复活动会话 |
| D17 | 本地个人试用为当前交付边界 | 范围限制，不是用户长期用途断言 | 公开/商业发布需另行确认许可证与分发方案 |
| D18 | 所有热键全局保存，以 clipId/presetId 关联，不随预设改绑 | 防误播的工程细化；收敛早期草案的预设内绑定设想 | 切换布局不解除绑定，界面需明示；若改作用域需变更规格和测试 |
| D19 | P5A 采集选定应用输出、P5B 导入/草稿裁剪入库 | 用户 2026-11-01 登记为待实现 [U3]；采集≠发送，整应用范围、每次显式开始、不采麦/通话、不绕 DRM、本地不上传 | 要求标签页级、采集通话/他人声音、后台/自动采集或云端处理时重新审查 |

## 4. 证据与不确定性

- 官方 API/产品文档是一手资料，但不能替代目标机器上的原型和实际 KOOK 接收端测试。
- 组件版本、平台支持和许可存在变化可能；开发开始与发布前分别复核。
- 构建与真机测试证据见各阶段开发日志与 [P4 测试报告](08-p4-test-report.md)；其结论限定在 ENV-001 环境，不外推到未测组合。
- 延迟数字属于实测记录而非对外保证；目标门槛的修订依据见 [03] §5。
- 已采用默认行为按 [U2] 登记；未知环境与待实测能力继续标记未知，不能把“同意建议”升级为证实运行效果。
- 不存在真正针对所有系统/应用/设备的防误传保证；测试承诺限定在本程序控制链路与明确记录的环境，外部旁路要显式提示。

## 5. 变更登记规则

每次更改基线应记录：日期、来源、修改理由、受影响的 R/D/T 编号、行为差异、是否需用户再次确认，以及验证状态。仅变更措辞不覆盖已存在的失败证据；技术方案失败应写明，不以替换来源或改低指标掩盖。

| 版本 | 变化 | 验证状态 |
| --- | --- | --- |
| v0.2 | 需求澄清草案、官方机制核查、34 项初版用例 | 文档检查，未运行 |
| v1.0 | 依据 [U2] 固定默认行为、补使用注意/模块契约/交接、扩展至 46 项验收 | 本轮文档检查；所有运行用例未执行 |
| v1.1 | 依 [U3] 登记 P5A 采集与 P5B 裁剪入库（03 §2-§4/§7、01 §3.2/§3.3/§5.3、06 §4.3 注记），新增 D19 与 T47-T53；v1.0 首发行为不变 | 文档检查；未开发、未验证 |
| v0.1.0（软件） | 首个可交付版本；依赖登记见 §6 | 181 项自动化测试 + 真机测量（测试报告） |

## 6. 发布依赖与许可证登记（v0.1.0）

发布物：自包含便携包（`win-x64`，无需目标机安装 .NET）。应用与驱动完全分离：不捆绑、不安装、不卸载任何音频驱动；Steam Streaming Speakers 为用户已有 Steam 安装自带组件，由用户以系统原状使用，本项目不复制或分发其任何文件。

| 组件 | 精确版本 | 许可证 | 用途 | 来源 |
| --- | --- | --- | --- | --- |
| .NET SDK / 运行时（含 WindowsDesktop） | 10.0.402 / 10.0.13 | MIT（.NET 许可） | 运行时，自包含发布 | 本地 SDK（`.tools/dotnet`） |
| NAudio | 2.2.1 | MIT | WASAPI 捕获/渲染/解码 | nuget.org（本地缓存） |
| WinForms | 随 .NET 10.0.13 | MIT（.NET part of .NET） | 桌面壳 | 同 .NET 运行时 |
| Steam Streaming Speakers 驱动 | 随用户 Steam 客户端 | Valve 用户协议（未随本项目分发） | 提供虚拟播放/录音端点对 | 用户机器既有组件 |

传递依赖：NAudio 2.2.1 的直接传递包（NAudio.Core/Wasapi/Midi/WinMM/Asio/WinForms，均 MIT）随包锁定于本地 NuGet 缓存；无其他第三方运行时依赖。项目自有代码许可证：随仓库登记（个人本地试用交付边界 [D17]；公开分发前需另行确认名称、签名、捆绑与更新策略）。

## 7. 原生桌面 0.2.0 的实际依赖

版本来源：[`desktop/package.json`](../desktop/package.json)、[`desktop/package-lock.json`](../desktop/package-lock.json)、[`Cargo.lock`](../desktop/src-tauri/Cargo.lock) 以及 Cargo 下载包的许可证声明。下表登记直接组件，不等于已完成完整传递依赖审计或公开分发许可审查。

| 组件 | 本轮锁定版本 | 许可证/边界 | 用途与一手来源 |
| --- | --- | --- | --- |
| Tauri Rust / JavaScript API / CLI | 2.12.5 / 2.12.4 / 2.12.4 | Apache-2.0 OR MIT | 原生壳、IPC、构建；[Tauri](https://github.com/tauri-apps/tauri) |
| Tauri dialog / opener / shell / single-instance | 2.8.4 / 2.7.2 / 2.6.0 / 2.5.5 | Apache-2.0 OR MIT | 文件选择、文件位置、随包后端、单实例；[官方插件](https://github.com/tauri-apps/plugins-workspace) |
| React / React DOM | 19.3.1 / 19.3.1 | MIT | 正式界面；[React](https://github.com/facebook/react) |
| Lucide React | 1.66.0 | ISC | 界面图标；[Lucide](https://github.com/lucide-icons/lucide) |
| TypeScript / Vite / React 插件 | 7.0.2 / 8.3.5 / 6.1.2 | Apache-2.0 / MIT / MIT | 仅构建；[TypeScript](https://github.com/microsoft/TypeScript)、[Vite](https://github.com/vitejs/vite) |
| serde / serde_json | 1.0.229 / 1.0.154 | MIT OR Apache-2.0 | 配置与 IPC；[Serde](https://github.com/serde-rs/serde)、[JSON](https://github.com/serde-rs/json) |
| Tokio | 1.54.1 | MIT | 异步后端请求；[Tokio](https://github.com/tokio-rs/tokio) |
| windows-sys（直接依赖） | 0.61.2 | MIT OR Apache-2.0 | Windows 原子文件操作；[windows-rs](https://github.com/microsoft/windows-rs) |
| .NET / NAudio | 10.0.13 / 2.2.1 | 延续 §6；自包含 .NET 后端 | 真实音频与素材，目标机无需安装 .NET |
| Microsoft Edge WebView2 Runtime | 使用目标机已安装版本，不固定或随便携包复制 | Microsoft 运行时许可；未安装时需用户安装，NSIS 可使用下载引导程序 | [Microsoft WebView2 分发说明](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) |

`design-preview/` 的示例数据与模拟状态不包含在正式前端构建中。旧 WinForms/CLI 只作为 0.1.x 历史维护入口，新桌面不通过它们转发命令。本地包未签名、未发布到 GitHub；公开分发前仍需复核完整依赖许可证、第三方通知、WebView2 分发方式、产品名称与签名。
