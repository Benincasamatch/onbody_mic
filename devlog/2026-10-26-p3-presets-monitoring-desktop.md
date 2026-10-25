# 2026-10-26 P3 预设、监听与日常界面 开发日志

## 本轮范围

用户指令"从P3开始继续完成项目"（P2 已于同日关闭，前置满足）。本轮完成 docs/03 §2 P3 全部六项任务的可开发部分：预设全生命周期（新建/复制/重命名/删除/保存/选择/最后选择恢复/schema 版本控制）、事务化预设切换与失败回滚、应用/设备/热键失效提示、互斥监听偏好持久化、WinForms 主界面+托盘+首次引导+退出清理、配置损坏安全关闭与经验证的备份恢复、SelectPreset 热键。产物为 v0.1 候选（`desktop` 命令）。用户开黑试用（任务 6）登记遗留。

## 技术决策（工程事实，含偏离记录）

1. **桌面壳用 WinForms，不用文档候选 Avalonia**（G07 偏离记录）：本仓库模式为本地 SDK（.tools/dotnet）+ 已缓存包离线工作；本地 SDK 自带 Microsoft.WindowsDesktop.App 10.0.13 运行时（编译+运行零新增依赖），而 Avalonia 需网络拉取上百个包且其版本锁定（G07）无离线取证条件。docs/02 §2 中 Avalonia 为"待原型验证的路线"而非已确认依赖；职责边界不变：新增 `OnbodyMic.Desktop` 巿承担 docs/02 §5 的 Desktop 角色（UI/托盘/组合根），全部安全逻辑仍在 Application/Domain，Windows 代码未泄漏进共享程序集。若后续取得网络与版本锁定证据可换壳。
2. **预设文件制存储**：`presets/<presetId>.json` 一文件一预设（docs/02 §10），schemaVersion=1，字段名对齐 docs/06 §4.2；**新增字段 `virtualOutput.captureDeviceStableId`**（线缆 capture 侧端点 ID，docs 示意缺此字段无法配置 FullEndpoint 监听源，属"具体字段可在 P0 调整"范围内的补充）。presetId 字符集限 ASCII `[A-Za-z0-9_-]`（成为文件名，结构性拒绝路径逃逸）；文件内 presetId 与文件名不一致按损坏拒绝。
3. **事务化切换实现**（docs/06 §5.3.5 逐字落地）：`AudioSession.ApplyPreset` 单条串行命令内完成：会话侧停播/停采集/停监听（TeardownAudioPaths）→ 引擎 `AbortSession`（全开关 false、清 clip、撤销全部来源授权、sessionEpoch++、DevicesValidated=false）→ 设备校验回调（缺失=非致命 RouteIssue，保留配置标记失效，B05 不换默认设备）→ 原子提交（设备配置/增益/禁用态监听偏好/presetId）→ 输出重接（失败=可见 issue，不回滚配置）。
4. **失败语义三分**（B06）：预设不存在=非法命令直接拒绝（零状态变化）；预设损坏/增益非法=先安全关闭再失败（旧选择保留、静音）；设备缺失=非致命 issue、配置照常提交。仅成功应用写 `settings.lastPresetId`。
5. **删除活动预设**（T38）：先 `TeardownForAbort` 安全关闭 + 清 lastPresetId，不自动启用其他预设；引擎侧 LastPresetId 保留（拆下的旧选择），启动恢复以 settings 为准。
6. **SelectPreset 热键**（docs/01 §5.8 P3 补位）：`HotkeyAction.SelectPreset=6`（存储名 selectPreset）；dispatcher 绑定时校验目标存在（悬空拒绝）、触发时经 PresetManager.Apply 走与 UI 完全相同的切换事务；settings `presetHotkeys` 节全量验证（与 clip/global 热键共同查重，重复手势后到者保留标红不激活）。
7. **NAudio 枚举器 RCW 缓存**（P1 约束复发并根治）：Desktop 首次 selftest 失败取证——泵经自研 interop 激活 MMDeviceEnumerator coclass 后，任何**新建** NAudio 枚举器对其 coclass 类 cast 失败（同一 COM 单例的第二包装类型）。修复：`WindowsAudioDevices` 持有进程级缓存枚举器实例（在 AppShell 构造时、任何泵之前首次创建），List/OpenById 不再每次新建/释放。面板/冒烟组合自 P0 起即"NAudio 先于泵"，此约束固化为代码。
8. **面板启动设备解析顺序**：显式参数 → 最后预设的设备（T24：配置恢复不得强制交互重选）→ 交互提示。三种入口（面板/desktop selftest/desktop GUI）同一恢复路径。

## 工程成果

```text
src/OnbodyMic.Domain/Preset.cs            预设模型（AppSelection/PresetDeviceConfig/MonitorPreferences/PresetConfig）
                                          + PresetConfigRules（ID 字符集、增益 0..4 有限值、悬空布局/坏字段 issue）
         Hotkeys.cs                       HotkeyAction.SelectPreset + 名称/描述
         SessionEngine.cs                 AbortSession（B06 失败路径）/SwitchPreset 重构/ConfigureMonitorPreference（锁存期合法，只关不开）
src/OnbodyMic.Persistence/PresetStore.cs  JsonPresetStore：文件制 CRUD/结构校验拒绝/原子写+备份/经验证的备份恢复
                                          （bak 先解析校验，通过才原子写回主文件，坏 bak 两文件都不动）
         GlobalSettings.cs                presetHotkeys 全量验证；Ui.CloseToTrayAcknowledged
src/OnbodyMic.Application/Presets.cs      IPresetStore/ILastPresetSink 契约 + PresetLoadResult/RouteIssue/PresetApplyResult
         PresetManager.cs                 新建/保存/复制/重命名/删除/应用/启动恢复/备份恢复/引用查询（全部经 AudioSession 串行命令）
         AudioSession.cs                  ApplyPreset 事务/TeardownForAbort/TeardownAudioPaths/StartOutput；
                                          工厂签名改为 (endpointId, ...) 参数化；Status 增 Devices 字段
         AudioSessionOptions.cs           InitialDevices/DeviceValidator；四个工厂按端点 ID 取适配器
         HotkeyDispatcher.cs              SelectPreset 绑定校验/触发分派/悬空拒绝
src/OnbodyMic.Platform.Windows/WindowsAudioDevices.cs   进程级枚举器缓存（RCW 约束根治）
         WindowsAppCatalog.cs             按可执行文件路径解析运行实例（唯一→可绑；0=未运行；多=歧义拒绝，B02）
src/OnbodyMic.Cli/PanelPresets.cs         面板 preset* 命令面 + bind app auto + 启动恢复引导
         PanelSoundboard.cs               Attach 解耦为 preset 钩子参数；preset 热键 bind/unbind/持久化/清理；
                                          DeleteClip（热键清理+缓存移除）；删除音效的预设引用提示（--force）
src/OnbodyMic.Desktop/（新工程，WinForms） AppShell（组合根+设备校验）/MainForm（状态/开关/音效/预设/热键列表，500ms 轮询）
                                          /TrayContext（托盘菜单镜像开关、关窗首次气球说明、退出全停+未保存提示）
                                          /FirstRunWizard（四端点选择+监听偏好+建首预设）/Program（--selftest 自检面）
```

## 测试（全部通过，0 警告；全量 176 项 = P2 的 127 + 新增 49）

- Domain 45（新增 14：PresetConfigRules ID 字符集/增益有限值边界/悬空布局与坏分组 issue；selectPreset 名称与描述往返）
- Persistence 29（新增 17：JsonPresetStore 全字段往返、损坏拒绝文件不动、未知 schema、文件名与内 ID 不一致、越界与 NaN 增益双向拒绝、非法 ID/缺设备 ID、列表损坏可见、重命名只改名、删除清 bak、备份恢复成功/坏 bak 拒绝两文件不动/无 bak 拒绝、NewId 唯一且合法；settings preset 热键往返+与 clip 热键手势冲突只激活先到者）
- Application 55（新增 18：PresetManager 事务——应用停全部路径并提交新配置/紧急锁存保持/损坏应用安全关闭保留旧选择/未知预设零状态变化/设备 issue 不阻塞提交（B05 不替换）/preview 暂停不跨预设存活/删活动预设拆解+清 last/删非活动不动会话/重启模拟恢复全关/无 last 拒绝/保存快照/复制独立/引用查询；SelectPreset 热键——未知目标拒绝、触发走同一事务、删除后悬空拒绝、无 manager 上报）
- 既有 127 项全部保持通过（工厂签名迁移后零行为回归）

## 真机冒烟（ENV-001，Steam 线缆 + Odo 头戴麦 + Senary 耳机，root=.tools/tmp/p3smoke）

| 场景 | 结果 | 对应 |
| --- | --- | --- |
| preset new → mic/extras on（Running）→ preset apply 同一预设 | 全部转 False、ReadyMuted、无旧会话残留 | T23（含"只改音量也不例外"路径，增益保存后同样全关） |
| 手工损坏预设 JSON → 重启面板 | 启动即报损坏、安全关闭、不自动换预设；mic on 后再 apply 损坏预设 → mic=False、旧选择保留 | T25/B06 |
| preset restore → apply | 备份校验通过、恢复成功、应用成功（全关） | T25 |
| 无设备参数重启面板 | 自动恢复最后预设"开黑默认"（列表 * 标记），全部发送/监听关闭 | T24/T01 |
| 增益 2.0 → preset save → 重启 | 恢复后配置含保存的增益（状态可见） | T24/R13 |
| desktop --selftest | restored=True、四开关全 False、devices=configured、窗口/托盘/向导可构造、OK | T24 于桌面壳；结构自检 |

## P3 门槛对照（docs/03 §2 P3）

- 重启恢复最后预设但全部发送/监听关闭：单测（Restore_startup + RebuildSession）+ 面板真机 + desktop selftest 三证 ✅
- 应用任何预设后不继续旧会话、不混入旧数据：Apply_stops_every_running_path 单测（mic/extras/app/clip/monitor 全停、epoch 失效）+ 真机 Running→apply 全关 ✅
- 界面/热键/托盘显示同一状态：三入口共享同一 SessionStatus 源与同一命令面（dispatcher/主窗口/托盘定时器同源轮询）；SelectPreset 热键应用与 UI 应用走同一 ApplyPreset 事务（单测）✅；"关窗口仍运行 vs 退出停止"的区分：关窗=隐藏+首次气球说明（Ui.CloseToTrayAcknowledged 持久化），托盘退出=未保存提示→全停→进程退出 ✅（结构自检过，视觉/交互确认待用户试用）

## 遗留与限制（不冒充已验证）

1. **用户开黑试用未做**（P3 任务 6）：desktop GUI 的实际视觉/交互（托盘菜单点击、气球显示、向导点击流、游戏前台组合 T17 补记）与操作名称/开关语义是否混淆，待用户试用会话。入口：`onbody desktop`（或 dotnet run --project src/OnbodyMic.Desktop）；CLI 面板 `panel` 同步支持全部预设命令。
2. Desktop 热键编辑未做 UI（P2 面板命令仍为编辑入口，桌面窗口展示激活绑定列表并注明）；P4 或按用户反馈决定是否补键捕获控件。
3. 预设切换中的"取消异步工作"依赖既有命令队列串行化与 epoch 丢弃，无独立的解码取消令牌（全量预解码单条 ≤60s，实际取消窗口极小）；如 P5 引入长音频流式解码需补。
4. `bind app auto` 的多实例歧义拒绝已实现，但同一可执行路径多实例的进一步手动选择 UI（从列表挑 pid）桌面壳未做（面板 `bind app <pid>` 可用）。
5. 音效库上限仍为 P2 工程默认（32MB/60s/缓存 60s），未按用户素材量校准（P2 遗留延续）。
6. WinForms 壳 DPI/主题未调（默认样式）；单实例互斥已有，崩溃转储/日志文件化留 P4（当前 GUI 日志仅控制台+内存）。

## Git

- 提交见 `git log`（feat(P3): ...）。
- 下一步：P4 稳定性/兼容性/交付（前置：用户确认第一版交互可用——即遗留 1 的试用结论）。

## 2026-10-26（补）：P3.1 用户试用反馈处理

用户试用结论：预设、托盘正常 ✅；两项反馈如下，其一为真实缺口已修复，其二提供排查路径待用户复测。

1. **"首次引导中的设置应该能在之后也可以修改"**（真实缺口，docs/01 §6 设置页要求）：新增 `DeviceSettingsForm`——主窗口"设备与监听设置..."按钮与托盘菜单均可打开；四个端点 + 监听偏好随时可改，保存走 `PresetManager.UpdateDeviceSettings`（写回活动预设 → 同一 ApplyPreset 事务重新应用，全部先关闭）。向导与设置的监听输出下拉现在标注"（当前系统默认）"且默认选中系统默认设备（此前默认选第一项，耳机选错即"反听无声"的头号嫌疑）。新增单测 2 项（保存重应用全关/缺失预设拒绝），全量 178 项通过。
2. **"无法反听到任何声音"**：代码侧核查结论——监听输出实现带 AUTOCONVERTPCM/SRC（P1 场景 E 验证过 FullEndpoint 链路），引擎侧无已知缺陷；大概率配置类原因，按序自查：
   a. 监听输出设备是否=实际佩戴的耳机（用新设置对话框确认/修改——最常见）；
   b. "仅音效"监听只在外发音效播放期间有声（开附加音频+播放才有）；想听"队友听到的"需切"实际虚拟麦"模式；
   c. "实际虚拟麦"需要预设里配置了线缆 Capture 端点（设置对话框"虚拟麦 Capture"一项不能是"不配置"）；若该下拉看不到 Steam AUX 项，说明端点被 Windows 禁用，用 `OnbodyMic.Cli.exe devices --all --ids` 查状态、`enable --id <端点ID>` 启用后重开程序。
   若以上均正确仍无声，回报具体操作序列，我来加取证工具。

## 2026-10-26（补三）：P3.3 监听"炸"根因修复 + UI 缩放适配

用户复测反馈：音效监听 ✅；**真麦+应用并发监听"非常炸"且与增益无关（0.01 仍炸）；KOOK 侧实际正常**——增益无关失真把嫌疑从电平收敛到数据流结构。

取证过程（monprobe 新工具，`OnbodyMic.Cli.exe monprobe --capture-id <id> --rate-only`）：
- AUX 端点 mixFormat 实测 **44100 Hz / 1ch / PCM16**——采样率错配假设否定；
- 10ms 会话交付速率 **0.995× 实时**——P0B 驱动降速惩罚假设否定；
- 发送链路 KOOK 实收正常（用户证）——引擎→线缆→采集全好。

**根因**（读码+症状反推，高置信）：`MonitorRouter` 的 FullEndpoint 环**无预滚动、无速率适配**。SetMonitor 时采集泵（AUX 端点时钟）与监听输出泵（耳机端点时钟，独立设备独立时钟）几乎同时启动，环水位贴零抖动 → 持续饥饿/拼接 = 断续爆裂，与增益无关。P1 场景 E 当时记录 "q=441"（恰为 10ms=贴底）被误读为正常流动；音质从未人耳验证（P1 遗留#2）。P1.1 修过完全同构的 mic 通道双设备漂移（自适应速率 ±1%），监听环当时未装同款。

**修复**（`MonitorRouter` 重写为绝对流坐标）：
1. **预滚动**：FullEndpoint 激活后积满 prim（水位/4，约 37ms）才开始消费，从最新 target 窗口读起（听"现在"，不回放积压）；
2. **自适应速率**：按水位误差 ±1% 线性插值拉伸/压缩，吸收 ppm 级时钟漂移（P1.1 同款机制）；
3. **病态积压一次性排水**：环溢出时直接 splice 到 target 水位，不再逐帧滴漏；
4. Stats 增 `FullEndpointStarvedFrames` 供指标观察。
单测：预滚动静音→出声、稳态无 dump/无饥饿、溢出排水、预览/回送既有语义保持。**179 项全过**。

**UI 文字显示不全**：三窗体 `AutoScaleMode.Dpi`（用户显示缩放 >100% 时布局未缩放）；向导/设置对话框 AutoSize+GrowAndShrink；主窗 MinimumSize 860×640；"发送与监听"组内层 FlowLayoutPanel 改 Top-dock+AutoSize（原 Fill 与父 AutoSize 冲突裁字）；状态栏指标 label 改 AutoSize。

**构建注意**：用户正在运行的 OnbodyMic.Desktop（高权限实例）锁 bin 输出——修复版本先构建在 `.tools/tmp/build-p3/`（0 错误+selftest OK）；用户关闭旧实例后正常 `dotnet build` 即恢复 bin。

## 2026-10-26（补四）：P3.4 "炸"实锤取证与显式格式修复（P3.3 修复无效后）

用户复测：P3.3（预滚动+±1% 自适应）**未解决**。该结果本身是关键证据——±1% 自适应只能救 ppm 级时钟漂移，救不了**内容损坏**，嫌疑从缓冲管理收敛到数据流本身。取证（monprobe 增强：包统计+波形统计+`--explicit` 对比）：

| 会话 | 协商格式 | 峰值 | 符号翻转率（音源 440Hz） | 结论 |
| --- | --- | --- | --- | --- |
| mixFormat（原产品路径） | 44100/1ch/**PCM16** | 0.996 | **7595 Hz（18 倍）** | **实锤：mixFormat 谎报格式**——按 2 字节/样本解析实际 4 字节 float 交付 = 满幅噪声，与增益无关，P2.1 "翻转变"的定性证据 |
| 显式 44.1k mono PCM16（mic 通道同款） | 同左 | **0.400（精确=音源幅度）** | 1678 Hz（重采样纹波，主体正确） | 内容正确；交付速率 0.986× 实时（正常） |

**修复**：`WindowsMonitorCapture` 切显式 44.1k mono PCM16 会话（+AUTOCONVERTPCM，引擎负责端点格式转换）——与麦克风通道完全同款、P1/P2 全程无故障的路径；`MonitorRouter` 自适应范围 ±1%→±2%（实测显式会话 -1.4% 交付漂移，2% 音高差不可闻）。包统计（PacketsSeen/MaxPacketFrames/SilentPackets）落入 WasapiEventCapture 供长期取证。**179 项测试全过 + selftest OK**。教训登记：AUX 镜像端点的 GetMixFormat 结果不可信（会话状态相关），该端点上任何会话一律显式格式——与 P0B/P2.1 对该端点的三连取证一致。

## 2026-10-26（补二）：P3.2 反听关闭、真麦并发"炸"取证支持

用户复测：**音效监听正常**（P3 任务 3 的监听链路用户确认 ✅）。新反馈两项：

1. **"真麦发送把麦克风与应用声音传出，且非常炸"**：并发传出本身是设计行为（真麦走 mic 闸、应用走 extras+app 双闸）；"炸"的第一嫌疑为真实素材配平——音乐通常接近满幅，叠人声后总线超载、软拐点限幅（tanh, knee 0.8）大幅饱和即失真。P1 场景 A 当时用低电平测试音（单路 0.1-0.5），未暴露真实响度配平。已做：主窗口状态栏右侧新增实时指标（峰值/削波/硬跳变/欠载，超载变红）——区分"炸"发生在本程序混音还是 KOOK 侧处理（KOOK 增益/自动控制叠加不在本程序控制范围，docs Q05/T27）。待用户复测取证：本程序侧峰值≥0.99 或削波计数增长→降真麦/应用增益（默认 1.0，各自独立可调 0-4）即可配平；若本程序侧峰值正常而队友仍炸→调 KOOK 输入设置。
2. **隔离确认待复测**：若用户只勾"真麦发送"（不勾附加音频）时音乐仍传出，则为严重隔离缺陷——引擎侧 extras/app 双闸有 P1 T04/T05 单测与真机隔离取证，当前无已知缺陷路径；已请用户按此步骤复测确认。
