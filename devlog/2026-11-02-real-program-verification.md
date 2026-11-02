# 2026-11-02 实际程序启动与核心功能复测

## 范围与环境

- 对象：本机已构建的 `desktop/src-tauri/target/release/onbodymic-desktop.exe` 与 `OnbodyMic.EngineHost.exe`。SHA-256 与 `dist/OnbodyMic-0.2.0-desktop-portable-x64/win-x64/` 内同名文件完全一致，因此本轮结论适用于该便携包内的二进制。
- 隔离数据根：`.tools/verify-20261102-1/`，从既有测试根复制 settings、presets、library；未写入默认 `%LOCALAPPDATA%\OnbodyMic`。
- 实际端点（由程序 `devices` 命令枚举，均 Active）：
  - 真麦：头戴式麦克风 (Odo By VECLAN.COM)，系统默认录音端点
  - 虚拟输出：扬声器 (Steam Streaming Speakers)
  - 虚拟 Capture：内部 AUX 插座 (Steam Streaming Speakers)
  - 监听：扬声器 (Odo By VECLAN.COM)，系统默认播放端点
- WebView2 Runtime 156.0.4314.58；通过 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9223` 连接真实 WebView2 页面执行 UI 操作与取证；未使用设计预览或模拟状态。

## 结果

| 检查 | 实测结果 |
| --- | --- |
| 启动与后端握手 | 桌面 PID 26852，自动启动 `OnbodyMic.EngineHost` PID 30244；`ReadyMuted`、`devicesValidated=true`，全部发送/监听开关关闭；两个预设均加载，无配置错误 |
| 设备枚举 | 19 个端点；预设引用的 4 个端点全部 Active，配置未回退 |
| 本地试听 | 通过真实卡片“试听”播放 `local-proof`；clip 峰值 0.002195、mix 峰值 0、completed +1；未隐式开启发送 |
| 音效外发 | 开启“附加发送”后卡片“发送”；clip/mix 峰值均 0.002195，完成播放；虚拟线缆独立录音峰值 0.002197（float32 44.1 kHz 单声道），静音基线为 0 |
| 真麦发送 | 开启“真麦发送”后 `Running`；pushed 63914 / pulled 61968 / dropped 0 / denied 851 / authorized true；mic 峰值 0.001129；关闭后回到 `ReadyMuted` |
| 应用捕获 | 真实应用选择器绑定自有 `OnbodyMic.Cli` 纯音进程（PID 25436），app 增益 100%，开启附加与应用发送；pushed 225351 / dropped 909，app/mix 峰值 0.019989（源音 0.02）；虚拟线缆录音峰值 0.019989、约 1 kHz 正弦（RMS 0.01407） |
| 紧急静音/解除 | 点击“全部静音”→ `EmergencyMuted`、latched=true、四个开关全关；点击“解除锁存”→ `ReadyMuted`、仍全关 |
| 预设切换 | 切到“迁移验证”与切回均 `ReadyMuted`、全关；应用 PID 绑定被清除，无未保存提示 |
| 诊断面板 | 可打开/关闭，显示 `ReadyMuted`、四路 −∞ 与累计计数，未伪造当前电平 |
| 关闭到托盘 | 点击“关闭到托盘”后桌面截图不再有主窗口；桌面进程与 EngineHost 均存活，配置与后端保持 |
| 显式退出 | 调用退出后桌面与 EngineHost 均结束，9223 无监听；未强杀进程 |
| 前端健康 | 各阶段 CDP 记录 `consoleErrors=[]`、`logErrors=[]`；UI 无横向溢出，状态栏显示“本程序发送已关闭” |
| 持久化 | settings、两个 preset 文件 SHA-256 与复制源一致；本轮仅改运行态增益/绑定，未保存 |
| 日志 | `logs/onbody.log` 仅 `[ClipCompleted] clip finished`，无异常 |

## 证据

- `.tools/verify-20261102-1/ui.png`：真实 Tauri 窗口截图（后端状态、四个路由卡片、音效卡片均来自真实数据）
- `.tools/verify-20261102-1/desktop-after-tray.png`：关闭到托盘后的桌面（主窗口消失）
- `.tools/verify-20261102-1/cable-baseline.wav`、`cable-signal.wav`、`cable-app.wav`：虚拟 Capture 端独立录音
- `.tools/verify-20261102-1/hashes-after.txt`：配置/预设/素材索引哈希
- `.tools/verify-20261102-1/cdp.mjs`、`core.js`、`app-capture.js` 等：本轮 CDP/UI 操作脚本

## 未验证与限制

- 真实 KOOK 或任意远端是否收到、音质与延迟；本轮只证明本机虚拟线缆 Capture 收到预期信号。
- 物理按键触发全局热键（仅确认 `Ctrl+Alt+F11` 已注册、无冲突）；全屏/权限差异未测。
- 真麦只观察到设备采集帧与房间本底峰值，没有用语音做主观录音验收。
- 两小时稳定性、第二台机器、其他虚拟驱动/蓝牙、Windows 10 未复测。
- 默认根 `%LOCALAPPDATA%\OnbodyMic` 启动未能在本会话直接验证：同一二进制在 workspace 根正常，但以 `C:\Users\...\AppData\Local`、`Documents`、用户目录、`D:\` 和 `E:\` 非 workspace 目录为根启动时，均在 setup 阶段失败（WebView2 `拒绝访问 (os error 5)`，随后一次为 `HRESULT(0x8000FFFF)`）。在 C: 的一个探测目录显式设置 Low 完整性标签后，同一二进制立即正常启动并通过 CDP 连接；workspace 目录本身即带 Low 标签。由此确认是本次 agent 会话的完整性级别/沙箱限制，而非产品缺陷。默认根启动仍需在普通桌面会话复核，本轮不记为通过。所有探测根已删除，真实根仅有的空 `webview`/`runtime-cache` 也已移除。
- 应用捕获用自有纯音进程验证进程树包含关系，未覆盖浏览器标签页隔离或游戏反作弊场景。
- 本轮没有修改源码，因此未重跑 .NET/Rust 测试；历史测试结论不变。

## 后续修复：音效、应用选择与裁剪

以上段落记录修改前的 0.2.0 复测；后续用户追加六项修复与音效编辑、删除、波形、长文件裁剪需求，已在 0.3.0 工作树实现。具体实机结果及边界见 [验证记录 §11](../docs/07-validation-and-handoff.md#11-音效应用捕获与波形裁剪修复实测)。

- 根因：Tauri canonicalize 产生的 Windows verbatim 路径被 NAudio 的 MP3 文件名解码入口当成无效 URL；字节流入口保留长路径和素材身份。已有 WAV 路径不换解码约定。
- UI：共用主题 Select 与原生 HTML 模态 top layer；确认作为独立 Dialog，原编辑状态不卸载。拖入监听通过当前 context 引用调用，避免保存启动时的空状态闭包。
- 应用：真实根链/启动时间身份、保留进程句柄、复用采集缓冲、退出和 HRESULT 故障如实传播。只捕获选定进程树，不增加系统混音回退。
- 编辑：流式来源分析和分段 WAV 导出、版本指纹、≤60 秒成品边界、本地临时试听以及取消令牌。来源/父 ID/相对区间写入 schema 2，父条目和派生素材各自独立。
- 删除：文件独占与暂存先于索引提交，失败恢复原文件/元数据/顺序；删除整树后 Rust 批量保存热键清理。
- 本轮实际构建通过，259 项 .NET、13 项 Rust 测试通过；2.5 秒成品与 110250 帧源选区零差异，长源哈希不变。真实进程音源经线缆独立录音检测到 1 kHz。
- `.tools/fix-smoke/` 保存结构化证据及隔离测试数据；临时脚本/探针项目已在交付清理。默认根与旧下载包不覆盖。

