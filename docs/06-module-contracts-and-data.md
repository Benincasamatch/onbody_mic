# Onbody Mic 模块契约与数据基线

版本：文档基线 v1.0（工程设计，待实现验证）  
日期：2026-10-24  
状态：仅用于规划、评审和 P0 原型接续；当前仓库尚无项目代码。本文件中的目录、接口和 JSON 均为示意，不表示已经实现。

本文把 `docs/01-product-requirements.md` 与 `docs/02-architecture-and-safety.md` 收敛为一套可执行的模块边界。它不是 API 锁定书，也不是对 Windows、KOOK、虚拟设备或任何延迟指标的实测声明。

## 1. 工程基线与术语

### 1.1 默认产品行为

- 首发只验证 Windows；共享业务模型保留将来 macOS/Linux 桌面适配的可能。
- 一个会话最多有一个已选应用、一个真实麦克风和一条正在播放的音效。多应用和多条音效叠加不在首发默认范围。
- 应用捕获按单一应用/进程树粒度处理，不承诺浏览器标签页或网页级隔离。
- 新音效替换当前音效；再次触发同一音效从头重播。停止音效不影响真实麦克风和应用背景音。
- 启动只恢复最后使用的预设配置。`micSendEnabled`、`extrasSendEnabled`、`appSendEnabled`、监听和音效播放均为关闭；无旧队列补播、无进度恢复、无来源自动启用。
- `appSendEnabled` 是附加音频内的应用子路由，开启它不能隐式开启 `extrasSendEnabled`；当 `extrasSendEnabled=false` 时，请求开启 `appSendEnabled` 必须拒绝，不能只靠后端总闸挡住声音。
- 音效无独立发送总闸：只有 `extrasSendEnabled=true` 且收到显式 `PlayClip` 命令时，当前音效才可发出。关闭附加音频总闸立即令 `appSendEnabled=false`、停止应用采集、清应用与 clip 发送队列并停止外发 clip；不影响真麦发送，也不停止纯本地 preview。
- 发送播放与本地试听共用唯一 clip 播放器/播放槽，同一时刻只能有一个播放目标；Preview 与 Send 授权不同，不能重复叠加同一素材。`StopClip` 停止任意模式下的 clip 播放并清理相关队列。
- 预设切换先停止播放、取消旧异步工作、清空队列、关闭所有发送与监听，再校验新预设；全部校验成功才更新 `lastPresetId`，之后仍须用户手动启用。
- 紧急静音是锁存状态。用户显式解除后进入 `ReadyMuted`；若设备/配置仍有故障则保持 `ConfiguredMuted`/`FaultedMuted`，所有发送和监听继续关闭。播放热键、预设导入、设备恢复不能自动解除。
- 监听偏好为 `ClipOnly`，但启动关闭。`FullEndpoint` 读取实际虚拟麦克风 capture 端点，只能送至耳机，绝不能回到发送混音。两种监听模式互斥。PreviewClip 播放期间暂停当前监听；播放结束后只可恢复未被安全命令撤销的监听状态。任何 Stop、切预设、故障、紧急静音或关闭监听命令都会使自动恢复失效。
- 来源、设备、权限或身份不确定时关闭相关路径，不自动换默认设备、其他应用或系统混音。

`sessionArmed` 如果作为内部派生状态使用，只能由显式启用命令和已验证条件推导；它不是另一个无 UI 的必需总闸。UI 展示并操作三个明确定义的开关，避免一个隐藏的“已 armed”与用户所见状态不一致。

### 1.2 不编造原则

- 不编造设备型号、KOOK 行为、驱动排他性、支持的 Windows build 或硬件实测延迟。
- “低延迟”目前是工程目标，必须在 P0 测量后确定验收阈值；不能把库名称、采样率、缓冲设置当成已达标证据。
- NAudio 是 Windows P0 候选，原型阶段冻结候选版本；具体 API、回调行为和许可证组合须编译及测试确认。
- 文档 v1.0、产品版本和配置 `schemaVersion` 三者独立；改文档编号不触发配置迁移。

## 2. 模块与建议目录

首版采用一个桌面进程中的清晰模块，不引入微服务、后台网络服务、RPC 总线或插件市场。以下目录是建议布局，当前尚不存在。

```text
src/
  OnbodyMic.Domain/              # 平台无关的模型、状态与策略
  OnbodyMic.Application/         # 用例、串行命令、会话协调与安全闸调用
  OnbodyMic.Audio/               # PCM、混音、缓冲、峰值保护和指标
  OnbodyMic.Media/               # 音效导入、解码、管库和缓存
  OnbodyMic.Platform.Windows/    # 应用捕获、设备、热键、系统事件
  OnbodyMic.Persistence/         # JSON、迁移、原子保存和备份
  OnbodyMic.Diagnostics/         # 脱敏日志、指标、用户主动导出
  OnbodyMic.Desktop/             # Avalonia UI、ViewModel、托盘、组合根
tests/
  OnbodyMic.Domain.Tests/
  OnbodyMic.Application.Tests/
  OnbodyMic.Audio.Tests/
  OnbodyMic.Media.Tests/
  OnbodyMic.Persistence.Tests/
  OnbodyMic.Platform.Windows.Tests/
docs/
devlog/
```

依赖方向：`Desktop -> Application -> Domain/Contracts`；平台、媒体、持久化与诊断实现契约；组合根负责组装。音频回调不得调用 UI、文件 I/O、网络或配置保存。可按工程规模合并程序集，但不能让 UI 直接操作 WASAPI 或绕过安全闸。

## 3. 模块契约

以下是建议职责，不是现存类名。契约需要表达失败、取消、能力未知与资源所有权；具体签名在 P0 建工程后确定。

| 契约 | 职责 | 禁止行为 |
| --- | --- | --- |
| `IApplicationAudioCatalog` | 列出可选应用实例、身份、捕获粒度和能力状态 | 自动授权/切换来源；宣称标签页隔离 |
| `IProcessAudioSource` | 绑定已确认进程实例，产出带时间戳帧，报告退出/失效 | 名称猜身份；PID 复用后继续发；回退系统 loopback |
| `IMicrophoneSource` | 打开用户确认的真实输入端点并报告断连 | 静默选择默认麦克风；绕过 `micSendEnabled` |
| `IClipLibrary` | 将素材复制进受管库，分配稳定 ID，管理收藏/元数据 | 修改原文件；信任外部路径；直接控制播放路由 |
| `IClipPlayer` | 可取消地向 clip 总线解码/播放一条音效 | 直写系统端点；绕过附加音频总闸；停止后补播 |
| `IAudioMixer` | 按不可变路由快照混合 mic/app/clip | 在回调内修改共享集合或决定来源授权 |
| `ISendGate` | 对每帧检查会话代数、来源授权、总闸、路由、设备健康和紧急锁存 | 接受未知来源；监听回送发送 |
| `IVirtualAudioOutput` | 写入明确选择的虚拟播放端点并报告格式/缓冲/故障 | 自动迁移默认设备；承诺排他写入 |
| `IMonitorCapture` | FullEndpoint 读取实际虚拟录音端点，输出帧给监听路由 | 写回发送；假称端点实测 |
| `IMonitorRouter` | 在端点帧与 ClipOnly 抽头中择一输出至耳机；处理 Preview 暂停/恢复 | 两模式同时叠加；停止后迟到恢复；送入发送闸 |
| `IHotkeyService` | 注册动作、验证冲突、抑制按键重复 | 注入游戏按键；绕过总闸；预绑定未确认按键 |
| `IPresetStore` | 版本化读写、迁移、原子保存和备份 | 恢复运行态发送；PID 作稳定身份；保存半配置 |
| `ISessionController` | 串行执行启用、停止、切预设、静音及解锁 | UI 自行改运行态；并发提交冲突路由 |
| `IPlatformCapabilities` | 报告 `Supported/Unsupported/NeedsPermission/Unknown` | 把未知伪装成支持；静默改用系统混音 |

### 3.1 音频帧和缓冲所有权

内部帧至少包含 `sourceId`、`sessionEpoch`、`sourceEpoch`、必要时的 `playbackEpoch`/`monitorEpoch`、单调时间戳、采样率、声道数、样本格式、有效帧数和只读样本视图。实时回调产生的缓冲只在回调期间有效；进入队列前必须复制到池化/租用内存。成功入队即转移缓冲所有权，消费或丢弃后恰好归还一次，禁止悬空引用、重复释放和泄漏。

帧不可变。路由、增益和静音由消费侧按不可变快照处理。全会话停止/切预设/紧急静音/整体引擎重建才递增 `sessionEpoch`；单一来源静音、失效或重开只递增该来源的 `sourceEpoch`，不得误杀仍有效的真麦或其他已授权来源。音效替换/停止递增 `playbackEpoch`；监听停止/模式切换递增 `monitorEpoch`。消费方校验相关 epoch 后丢弃迟到帧。每个来源、发送、ClipOnly 监听和 FullEndpoint 监听有界隔离队列；监听过载不能阻塞发送。具体队列时长、样本池实现等 P0 测量后确定。

### 3.2 配置引用和素材路径

预设只引用稳定 `clipId`，不把绝对路径写进热键动作。管库索引可记录原始导入路径用于溯源/重新定位；路径隐私限制针对日志和诊断包，而不是禁止配置保存运行所需路径。导入时复制到受管库，原文件只读，不移动、覆盖或删除。路径须经存在性、大小、权限和解码校验。

## 4. 数据模型（JSON 示意）

本节 JSON 只解释关系，不是现有文件。具体字段可在 P0 调整，但不得改变安全语义。

### 4.1 全局设置示意

```json
{
  "schemaVersion": 1,
  "lastPresetId": "preset-default",
  "libraryRoot": "%LocalAppData%/OnbodyMic/library",
  "globalHotkeys": {
    "stopClip": null,
    "toggleExtrasSend": null,
    "toggleMicSend": null,
    "emergencyMute": null,
    "toggleMonitor": null
  },
  "clipHotkeys": {
    "clip-airhorn": null
  },
  "presetHotkeys": { "preset-default": null },
  "ui": { "startMinimized": false }
}
```

安全关闭是运行时不变量，不提供 `...OnStartup=true` 配置选项，也不序列化活动开关。日志默认不写完整路径、窗口标题、命令行或音频内容；设置文件可以保存必要的素材库目录。所有动作/音效/预设选择热键为全局配置，初始均不绑定。

### 4.2 预设示意

```json
{
  "schemaVersion": 1,
  "presetId": "preset-default",
  "name": "开黑默认",
  "application": {
    "selection": { "executablePath": "C:/Apps/Player/player.exe", "packageId": null },
    "captureScope": "ProcessTree"
  },
  "microphone": { "deviceStableId": "device-mic-1", "gain": 1.0 },
  "appGain": 1.0,
  "clipBus": {
    "gain": 0.8,
    "playPolicy": "ReplaceAndRestartSame"
  },
  "clipLayout": ["clip-airhorn"],
  "libraryView": { "favoritesOnly": false, "groupId": null },
  "virtualOutput": {
    "deviceStableId": "device-virtual-out-1",
    "captureDeviceStableId": "device-virtual-capture-1"
  },
  "monitor": {
    "mode": "ClipOnly",
    "deviceStableId": "device-headphones-1",
    "gain": 0.5
  }
}
```


P3 实现补充（2026-10-26，工程登记）：`virtualOutput.captureDeviceStableId` 为新增字段——线缆 capture 侧端点是 FullEndpoint 监听的读取源，与 render 侧端点无派生关系，须显式保存；`monitor.deviceStableId` 为监听输出（耳机）。实现字段名与本节一致，由 `presets/<presetId>.json` 一文件一预设承载，presetId 限 ASCII `[A-Za-z0-9_-]`（作为文件名）。
路径、ID、示例增益和热键均为示意，不是实际设备或性能结果。全局控制绑定、每条音效播放绑定与选择预设绑定均在全局设置；`emergencyMute` 尤其不随预设改变。映射可包含多条，示例一条不限制素材数量；`clipLayout` 控制布局而非授权白名单。所有绑定默认 `null`/空，未经用户设置不得注册。有效配置只保存选择和偏好，运行时 PID、句柄、活动开关不落盘。`gain` 只是候选格式，数值约束需 P0 冻结。

### 4.3 音效库索引示意

```json
{
  "schemaVersion": 1,
  "items": [
    {
      "clipId": "clip-airhorn",
      "displayName": "Airhorn",
      "managedAssetPath": "assets/clip-airhorn.wav",
      "originalImportPath": "D:/Audio/airhorn.wav",
      "durationMs": 1200,
      "format": "wav",
      "favorite": true,
      "groupId": "group-game",
      "gain": 1.0,
      "decodeStatus": "PendingValidation"
    }
  ],
  "groups": [{ "groupId": "group-game", "name": "游戏" }]
}
```

素材二进制存于受管库，预设/热键只引用 `clipId`。收藏与单级分组属于全局库；每条音效的增益乘以预设音效总线增益，仍受最终峰值保护。删除库素材前检查所有预设布局和全局热键引用；删除分组将素材移到未分组，绝不删除素材文件。索引中的时长仅为示意，实际导入必须解码校验。

## 5. 会话状态与开关真值

### 5.1 状态

```text
Unconfigured      无可用配置或设备
ConfiguredMuted   配置已载入，但设备/来源尚未全部验证
ReadyMuted        配置和设备验证通过，所有发送/监听关闭
Running           至少一个获准来源通过发送闸
SourceLost        来源失效；关闭该来源，不扩大范围
OutputLost        虚拟输出失效；所有外发关闭
FaultedMuted      引擎/路由校验失败，保持关闭
EmergencyMuted    紧急静音锁存，普通命令不能解除
```

这些是 UI/运行概念，不是必须互斥的单个枚举。发送状态、每路健康、锁存状态、本地播放/监听状态分别建模：可以在应用 `SourceLost` 时真麦仍为 `Running`，也可以没有外发而仅本地试听。`ReadyMuted` 只在未启用任何发送/本地播放时显示；局部故障不能把整机错误地显示为已全静音。

### 5.2 显式发送控制

用户可见、可操作的运行开关只有以下三个；会话内部可以派生 `sessionArmed`，不能另外要求用户理解一个隐藏开关。

| 开关/动作 | 含义 | 重要约束 |
| --- | --- | --- |
| `micSendEnabled` | 真实麦克风是否允许进发送混音 | 启动关闭；与附加音频独立 |
| `extrasSendEnabled` | 附加音频主总闸，管应用声音和音效外发 | 启动关闭；关闭立即停止外发 clip 并清 clip 发送队列；不影响真麦 |
| `appSendEnabled` | 应用音源子路由是否允许进入附加音频 | 启动关闭；开启不隐式开启 `extrasSendEnabled`；extras 关闭时启用请求直接拒绝 |
| `PlayClip(clipId)` | 显式请求发送一条音效 | 只有 `extrasSendEnabled=true` 且其它安全检查通过才外发；否则拒绝，不排队 |
| `PreviewClip(clipId)` | 显式授权本地试听 | 发送关闭时仍可本地预览；紧急静音时拒绝；与发送共享单一播放器，不能并行重复播放 |
| `StopClip` | 停止当前 Preview 或 Send 播放 | 停止播放器并清理相关队列；不关闭真麦 |

**无独立 clip-send 总闸。** `extrasSendEnabled` 关闭即拒绝应用子路由启用、停止应用采集，并关闭应用和音效附加发送、清队列；音效需要显式 `PlayClip`。`PreviewClip` 是独立的本地授权，但与发送共用播放器；预览期间暂停监听，结束后仅在没有安全命令撤销恢复资格时恢复先前监听。紧急静音拒绝试听。

### 5.3 转移约束

1. 启动加载 `lastPresetId`，所有发送及监听开关为 false，无播放队列，进入 `ConfiguredMuted` 或 `ReadyMuted`。
2. 显式启用某发送路径前，校验来源身份、设备、格式、权限和路由。内部 `sessionArmed` 由显式启用状态推导，不作为另一个 UI 开关。
3. 应用、麦克风或素材失效时不换绑其他来源。虚拟输出或引擎失败时所有外发关闭；恢复后须重新验证和手动启用。
4. 紧急静音可从任意状态进入 `EmergencyMuted`，撤销所有发送授权、停音效/试听并清理发送/监听队列。显式解除后所有开关继续 false；验证通过才到 `ReadyMuted`，未通过保持配置/故障静音。锁存期间允许编辑/应用配置但不能改变 `EmergencyMuted`；重启也仅恢复配置且全关，不恢复活动授权。
5. 切预设顺序：撤销授权 -> 递增 `sessionEpoch` -> 取消异步工作 -> 停止音效 -> 清发送及监听队列 -> 所有发送/监听关闭 -> 校验新预设 -> 原子应用 -> 更新 `lastPresetId`。失败保持旧配置但静音，或进入 `ConfiguredMuted`；不得半旧半新。
6. `PreviewClip` 开始前记录监听状态并暂停监听；正常结束后只有监听 epoch 未变、且期间没有任何停止命令（含 `StopClip`）、切预设、故障、紧急静音或关闭监听命令时，才可恢复原监听。其它任何安全命令都使恢复令牌失效。
7. 休眠进入/恢复、虚拟输出失效/重连和整体引擎故障都撤销整个会话，停止发送、试听与监听；恢复仅重新校验，不自动重启来源。单麦克风/应用/监听设备故障则按所属路径处理，不误杀其他有效来源。

## 6. 命令优先级和取消语义

所有按钮、全局热键、设备事件和启动动作先转换为命令，由 `ISessionController` 串行处理。命令包含会话代数、来源和取消令牌；UI 不直接改运行状态。

1. 紧急：`EmergencyMute`、`StopAll`、引擎故障关闭；`PreviewClip` 在紧急锁存时拒绝。
2. 安全切换：`SwitchPreset`、`StopClip`、关闭发送开关、关闭监听、撤销来源、设备丢失。
3. 运行控制：显式启用发送路径、监听模式切换、重新验证设备。
4. 播放控制：`PlayClip`、`PreviewClip`、同条重播；Preview 与 Send 共用单一播放器，新目标替换旧目标，不排队。
5. 非实时：保存、索引、日志导出、电平 UI 刷新。

高优先级命令不能排在大量播放命令之后。`StopClip` 停止任一模式下的 clip 播放、取消解码与后续输出提交、清 clip 队列，并使 preview 触发的监听自动恢复令牌失效；已进入驱动、KOOK 或网络缓冲的样本无法追回，必须测量残余尾音，不能承诺瞬时撤回。

全会话停止/切换先将相关授权置 false、递增 `sessionEpoch`、取消任务，再释放设备；单来源故障只递增对应 `sourceEpoch` 并关闭该路，其他有效来源保持运行。音效替换/停止使用 `playbackEpoch`，监听开关/模式变化使用 `monitorEpoch`。取消必须幂等。队列有界；过期实时帧丢弃并计数，不能以无限缓存伪装连续。每个缓冲在入队/消费/丢弃/取消/故障路径中恰好归还一次。preview 与发送共用一个播放器但授权和总线不同；FullEndpoint 监听分离，监听没有到 `ISendGate` 的依赖边。配置以不可变快照原子替换。

## 7. 配置版本、校验和迁移

- 每份独立持久化文件（全局设置、预设、音效库索引）可有自己的 `schemaVersion`。它只标识数据格式，不代表文档或产品版本。
- 仅序列化结构不兼容变化递增 schema；文档编辑和普通产品升级不递增。
- 先解析到受限 DTO，验证类型、范围、ID 和引用，再迁移成当前模型。未知枚举不能被当作授权值；未知字段按明确策略保留或忽略。
- 迁移纯本地、可测试；先写临时文件、flush 并复读校验，成功后原子替换，保留可恢复备份。失败不得覆盖旧文件或启用音频。
- 活动会话状态、运行开关、旧 PID 和待播队列不从旧配置迁移。
- 导入/读取配置拒绝 `NaN`、正负无穷、负时长、超界 gain、超大集合、重复 ID、悬空 `clipId` 和越界路径；不静默改值后自动运行。设备 ID 当前不可用时保留配置但标记该路失效，禁止替换为默认设备。合法 UI 滑块可限制在已验证范围内；gain 上限由 P0 音频测试确定。
- JSON 序列化必须拒绝非有限浮点；反序列化后再次验证，避免序列化器行为差异。

## 8. 测试替身与测试策略

具体测试框架等建工程时再选择。替身应支持可控时钟、确定性样本、注入错误和观察所有帧的去向，核心安全规则不依赖真实硬件才能验证。

| 替身 | 验证重点 |
| --- | --- |
| `FakeApplicationSource` | 静音/退出/实例变化；不改绑来源；旧 source epoch 帧丢弃而真麦继续 |
| `FakeMicrophoneSource` | 可重复波形、静音和断连；关闭 `micSendEnabled` 后无麦克风帧外发 |
| `FakeClipDecoder/Player` | 慢解码、失败、取消竞态；替换/重播/停止；preview/send 共用播放器、试听永不外发 |
| `FakeVirtualOutput` | 记录每帧来源、总闸、路由；断连立即关闭所有外发 |
| `FakeMonitorCapture` | FullEndpoint/ClipOnly 互斥；只到耳机；preview 自动暂停与条件恢复；安全命令撤销恢复资格 |
| `FakeHotkeyService` | 冲突在激活前报错；无默认绑定；按键重复不连播 |
| `FakePresetStore` | 成功/失败/损坏/迁移；失败不更新 last preset、不恢复发送 |
| `FakeClock` | 命令排序、取消竞态、延迟统计和队列老化 |

最低自动化场景：启动所有开关关闭；extras 关闭时请求打开 app 必须拒绝；关闭 extras 会关 app 发送、停应用采集、清 app/clip 队列并停止外发 clip，但真麦和本地 preview 可继续；紧急锁存中拒绝 preview，解除后仍全关但可通过新的显式本地动作试听；切预设清队列并只在成功后更新 ID；新 clip 替换旧 clip；相同 clip 重播；preview 不进 send 且期间暂停监听；正常结束仅在监听未被安全命令撤销时恢复；FullEndpoint 监听不回送；热键冲突阻止激活；source epoch 故障不误杀真麦；session epoch 旧帧不复活；配置非法浮点不进入运行态。

硬件/真实软件场景单独做 P0/P1 集成验收：目标应用捕获范围、KOOK 实际接收、真麦并发、实际虚拟录音端点监听、设备断连、启动恢复、热键与游戏前后台、端到端延迟和停止尾音。记录 OS/build、应用/驱动版本、设备连接方式、采样格式、测试方法和重复次数；未知信息标为未知，不填猜测值。延迟阈值在基线测量和用户体验验收后另行冻结。

## 9. 跨平台能力模式

共享模型只描述语义：应用选择标识、来源授权、帧时间、预设、音效、命令和能力状态。不得向 Domain/Persistence 泄漏 Windows PID 句柄、WASAPI COM 对象或原生设备结构。

| 能力 | 可共享部分 | 平台需独立实现/验证 |
| --- | --- | --- |
| 桌面 UI | Avalonia 页面、ViewModel、验证 | 托盘和系统窗口行为 |
| 音频处理 | PCM 语义、混音、路由规则 | 设备时钟、调度、低延迟输出 |
| 文件与预设 | 元数据、库引用、迁移逻辑 | 沙箱路径、打包和解码依赖 |
| 应用捕获 | 选择与授权模型、提示 | 各平台的应用级捕获机制及权限 |
| 虚拟麦克风 | 输出契约与状态 | 虚拟端点/驱动、签名、安装与卸载 |
| 全局热键 | 动作、冲突规则 | OS API、桌面会话限制与保留键 |

能力使用 `Supported/Unsupported/NeedsPermission/Unknown` 表示；Unknown 一律不启用，Unsupported 不得偷偷替换为系统混音。全平台目前只表示未来桌面适配可能，不承诺移动端/Web 或跨平台能力等价。

## 10. 开发接续约定与注意事项

1. 当前只有规划文档；接续时先检查仓库状态和文档差异，不假定存在解决方案、代码、测试或已安装依赖。
2. 不在 P0 前安装驱动、静默修改系统默认设备或要求管理员。先以用户明确选择的既有设备验证链路；发现能力不足时停下来报告，不用全系统捕获兜底。
3. 技术候选为 Avalonia + .NET 10；NAudio 是 Windows 音频候选，P0 需冻结具体受支持版本并核验 API、许可证、包来源和捕获范围。不要因候选出现在规划里就直接升级或拉入依赖。
4. P0 必须先验证单应用捕获边界、KOOK 输入、真麦并发、FullEndpoint 监听并发、第三方端点是否可被外部软件绕过、热键后台行为，以及延迟/停止尾音。任何关键边界无法满足都应回到需求/方案评审。
5. 一次只推进一个可验证阶段；记录假设、未验证项、结果、失败路径和用户需决策项至 `devlog/`。无硬件证据不写“已验证”“低延迟达标”或具体支持矩阵。
6. 所有发送路径必须通过单一 `ISendGate`。Preview 与 Send 共用单一播放器但不可共用授权；preview 期间暂停监听并以监听 epoch 管理恢复。切预设/紧急静音/整体故障递增 session epoch，单源故障只递增 source epoch，避免误杀其他来源。
7. 设置文件可保存实现必要的路径和用户选择；日志及诊断默认最小化路径/应用身份，不录音、不上传。用户主动导出诊断包前展示内容并允许取消。
8. 初始没有热键，用户主动配置后经冲突校验激活。热键变更事务不能留下半激活状态；切预设不改全局绑定，但应检查其引用是否仍有效。当前布局不展示的有效音效仍可由其全局键触发，这一点必须在界面说明。
9. 不把文档建议描述为用户已经确认的技术实现；不将示意 JSON 写入运行目录冒充可用配置。

### 10.1 P0 输出物

- 可复现的能力与设备组合表，未测条目标成“未知/待测”。
- 单应用捕获与外部绕写风险的观察记录。
- KOOK 接收、真麦并发、FullEndpoint 实际端点监听的端到端结果。
- 延迟、队列水位、丢帧和停止尾音的测量方法与结果，不预设虚构阈值。
- 对继续/调整路线/停止的决策及其证据；若第三方虚拟端点不满足已确认边界，不静默降级。

