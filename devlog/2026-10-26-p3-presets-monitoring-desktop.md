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
