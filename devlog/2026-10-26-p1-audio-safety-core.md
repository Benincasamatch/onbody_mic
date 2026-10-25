# 2026-10-26 P1 核心音频与安全路由 开发日志

## 本轮范围

用户指令"从 P1 开始继续完成项目 mvp"。P0B 已关闭（门槛修订后达标，见上一日志）。本轮完成 docs/03 §2 P1 全部六项任务：契约、有界缓冲/格式转换/增益/防削波、统一安全闸/授权/会话代数/原子路由、四路语义、故障处理、最小控制面板与脱敏指标。

## 工程成果（新增两个程序集 + 平台适配器 + 面板）

```text
src/OnbodyMic.Audio/            平台无关音频核心（零 NAudio 依赖）
  EngineFormat.cs               引擎内部格式 = 44100 Hz / mono / float32（与线缆 capture 对齐）
  SourceChannel.cs              每源有界环形缓冲（drop-oldest + 计数）+ ticket + ISendFeed
  SendMixer.cs                  闸控混音：每周期逐 feed 判定，拒绝即 drain（不补播），硬限幅 ±1 + 削波计数
  ClipPlayer.cs                 单槽播放器（替换/重播）+ ClipFeed（preview 无 ticket → 结构性不可外发）
  MonitorRouter.cs              ClipOnly（仅音效）/ FullEndpoint（真实端点环，150ms 高水位修剪）/ preview 试听
  IClipDecoder.cs               解码契约（全量预解码，非有限样本拒绝）
  RouteGains.cs                 不可变路由增益/监听状态快照
src/OnbodyMic.Application/      会话协调
  Adapters.cs                   平台契约（IAudioSourceAdapter/IVirtualAudioOutput/IMonitorOutput/IMonitorCaptureAdapter）
  SessionCommandQueue.cs        串行命令处理器：紧急/普通双通道，安全命令插队
  AudioSession.cs               组装源→闸→混音→输出；命令线程唯一写者 + volatile 不可变运行态快照（原子路由更新）
  SessionEvents.cs              SessionStatus/SessionMetrics/脱敏通知
src/OnbodyMic.Platform.Windows/ 新增适配器
  WasapiEventCapture.cs         共享事件捕获泵（MMCSS Highest），显式 PCM16+AUTOCONVERTPCM 或 mixFormat
  WasapiExclusiveRenderPump.cs  独占 44.1k/16/2 事件渲染泵，20ms 整缓冲契约（P0B 取证的驱动硬约束）
  WindowsMicSource.cs           真麦：显式端点 + 引擎端 44.1k mono 转换
  WindowsAppSource.cs           应用：ProcessLoopbackCapture(44.1k/1) 包装 + 根进程 watcher（T08 正常退出≠故障）
  WindowsMonitorCapture.cs      FullEndpoint：读线缆 capture 端点（KOOK 同读）
  WindowsMonitorOutput.cs       监听输出：自研共享渲染泵（MMCSS）
  NAudioClipDecoder.cs          WDL 重采样 + 声道平均 + 60s 上限 + 非有限样本拒绝
```

Domain 扩展：`SessionEngine.OutputFailed()`（关闭全部外发、递增 session epoch、保留监听与紧急锁存、相位 `OutputLost`，`ValidateDevices` 清除）。

CLI：`panel` 命令——交互式最小控制面板（真实会话端到端），命令覆盖三开关/绑定/play/preview/stop/两种监听/增益/紧急静音/指标，`auto on` 每秒打印脱敏指标。

## 关键实现决策（工程事实）

1. **格式转换全部在平台层**：引擎内部只碰 44.1k mono float。麦克风经 WASAPI `AUTOCONVERTPCM|SRC_DEFAULT_QUALITY` 由音频引擎转换（避免自维护重采样线程）；clip 用 WDL 重采样（P0B 已证 MFT pull 失配 bug，WDL 稳定）。核心程序集零 NAudio 依赖（跨平台边界检查通过：Audio/Application 不引用任何 Windows 库）。
2. **帧模型取舍**：未按 docs/06 §3.1 的"帧对象+所有权转移"理想模型，改为每源环形缓冲 + epoch 双侧校验（push 侧拒收未授权，pull 侧 gate 拒绝即 drain）。安全语义等价：关闭期间/关闭前的帧在下个周期（≤20ms）被 drain，不可补播；迟到回调因 epoch 失配被拒。零实时路径分配。此取舍如 P2/P3 需要帧级时间戳再升级。
3. **ticket 接线**：`AuthorizeSource` 后由 AudioSession 把 `CreateTicket` 写入 channel——曾遗漏此步（channel.Ticket=null → 混音器全拒绝），Application 单测当场抓住（8 项失败→修复后全过）。
4. **NAudio WasapiOut 不可用于本进程**：先用自写 interop 激活设备枚举器（独占泵所需）后，NAudio `MMDeviceEnumerator` 的 coclass cast 在缓存 RCW 上失败（冒烟 E 发现）。监听输出改为自研共享渲染泵（共享模式 padding-aware，欠填无惩罚，与独占契约不同）。
5. **app 子开关关闭也停止采集**：docs 只规定 extras 总闸关闭停采集；P1 将 `appSendEnabled=false` 同样停采集（MVP 无电平预览需求）。记录于此，P3 若做电平预览再调整。
6. **增益范围 0..4**：G06 待冻结前的保守上限；NaN/Inf 拒绝（`ClampGain` 非有限值归零）。
7. **监听与发送无环路**：FullEndpoint 环只被 MonitorRouter 消费，结构上不存在回送边（单测覆盖）。
8. **git-bash `$!` 返回 MSYS pid 而非 Windows pid**：进程绑定冒烟时 app 绑到无效 pid 立即 `SourceEnded`。工具修复：`tone` 命令打印 `Environment.ProcessId`。

## 工具修复（P0 遗留）

- `record`：等待逻辑 bug（请求 10s 实录 25s = 2×seconds+5），改为直接 sleep→stop→flush。
- `ProcessLoopbackCapture`：捕获线程原会抛异常崩进程；改为 try/catch + `Faulted` 事件；格式参数化（44.1k/1ch 用于引擎路径）。
- Steam capture 端点（内部 AUX 插座）启用状态**跨会话保持**（P0 遗留疑问，本轮重启后观察确认，单次证据）。

## 测试（全部通过，0 警告）

- `OnbodyMic.Domain.Tests` 15 项（含新增 OutputFailed 语义）
- `OnbodyMic.Audio.Tests` 20 项：环形缓冲 FIFO/溢出/未授权丢弃/ticket 撤销清空/drain；混音器 gate 拒绝不复活/独立增益/stale epoch/硬限幅计数/欠载/preview 不推进播放头；播放器替换/重播/自然完成/停止；监听路由静音/ClipOnly 纯净/preview 本地可听/FullEndpoint 增益/有界修剪/饥饿静音
- `OnbodyMic.Application.Tests` 17 项（假源/假输出，覆盖 P1 门槛语义）：启动全关静音；extras 关时 app 启用拒绝；extras 关停 app 采集+clip 发送而 mic 继续；preview 本地可听且线上为零；紧急静音锁存/停止一切/拒绝新播放/解锁仍全关；静音期间缓冲不补播；app 退出只关 app 路（mic 继续）；mic 设备丢失只关 mic 路（app/clip 继续）；输出故障全关+显式重接；增益按路独立；ClipOnly 不含 mic/app；FullEndpoint 读端点且不回送；preview 正常完成才恢复监听；StopClip 取消恢复；app 需显式绑定且发送中拒绝改绑；解码失败不动安全状态

## 真机冒烟（ENV-001，5 场景全 Pass，取证 WAV 于 `.tools/tmp/` 不入库）

| 场景 | 操作 | 线缆 capture 侧取证 | 结论 |
| --- | --- | --- | --- |
| A 发送全链路 | mic on + extras on + play(700Hz 门控 beep) | tone-detected，ratio=812,839，rms=0.051 | T03 语义：三路经安全闸送达 |
| B preview 不外发 | extras on + preview | rms=0.000000，no-tone；mixer denied 持续计数 | T11 核心：试听结构性不外发 |
| C 紧急静音 | play → 0.8s 后 mute → unmute | 分段能量：t=3-5s 有音，t≥5s 起 20s 全静 | T10/T09：截断+无补播+解锁仍全关 |
| D 单应用隔离 | 500Hz 目标进程 + 800Hz 干扰进程并发（真实 Windows pid） | 500Hz tone-detected(1,166,010)；800Hz no-tone(0.22) | T04 于 P1 全链路复刻 |
| E FullEndpoint 监听 | monitor full | mon ring 流动 q=441，无故障，相位 Running | T26 语义（数字路径，音质待用户主观） |

panel 输出确认：启动即全关（"输出已接入（发送/监听全部关闭）"）、ClipCompleted/SourceEnded 通知正常、指标（队列水位/丢弃/拒绝/峰值/削波/欠载）流动。

## P1 门槛对照（docs/03 §2 P1）

- 静音不可绕过：场景 B/C + 17 项会话单测 ✅
- 旧缓冲不补播：T09 单测（mute 期间 push 的帧被 drain，重启后只出新帧）+ 场景 C 20s 静默 ✅
- 预览不外发：场景 B（rms=0）+ ClipFeed 无 ticket 结构保证 ✅
- 单来源失效不串源：mic 丢失/app 退出两个方向的隔离单测 ✅
- 整体故障安全关闭：OutputFaulted 单测（全关 + 拒绝启用直至显式重接）✅

## 遗留与限制（不冒充已验证）

1. **人声与音质主观验证未做**：场景 A mic 开启但环境无人说话；KOOK 实收是 P0B 证据（mixloop 原型），P1 引擎链路替换后建议用户开 KOOK 复听一次。

   **2026-10-26 补记（KOOK 实收测试）**：用户开 KOOK 复测。取证：测试前端点无 KOOK 会话（输入设备曾被重置），用户切回虚拟麦并进入语音环境后，**测试后端点出现 `pid=38096 process=KOOK state=Active` 会话**——KOOK 真实在读取虚拟麦。面板侧：mic 队列水位 30ms、drop=0、deny=0、**mix peak=0.906**（真实人声过闸，无削波 clip=0）、三次哔声 ClipCompleted 正常。客观链路证据闭环，P1 引擎链路的 KOOK 实收成立。
2. **休眠唤醒/设备热插拔物理场景未注入**：代码路径（DEVICE_INVALIDATED → Faulted → 安全状态）有单测，物理事件未实测（无热插拔硬件条件）。
3. **T08 真机重绑流程未演练**：bind 语义（发送中拒绝改绑）有单测；应用退出→重启→重新 bind 的真机操作待用户场景。
4. **FullEndpoint 监听时钟漂移**：150ms 高水位修剪已实现，长时间运行漂移率待 P4 稳定性矩阵。
5. **增益上限 0..4 为工程保守值**，G06 待正式冻结。
6. 紧急静音后监听设备关闭（释放设备），解锁后需重新 `monitor` 开启——语义符合"恢复不自动"，UI 提示待 P3。
7. Steam 端点跨重启保持为单次观察；程序启动时自动检测+重启用（IPolicyConfig 工具已在库中）待 P3 集成为自动流程。

## Git

- 提交见 `git log`（feat(P1): ...）。
- 下一步：P2 音效库与全局热键（前置：P1 安全路由稳定 ✅）。
