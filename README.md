# 随声麦 · Onbody Mic

Windows 游戏语音音频面板：把真实麦克风、指定应用进程树和导入音效混合后送入虚拟音频线缆，供 KOOK 等语音软件选作输入；支持独立本地试听、监听、热键和预设。

当前本地交付版本 **0.2.0**：正式桌面端改为 **Tauri 2 + React/TypeScript 音效台**，Rust 负责配置、预设和原生壳；C#/.NET 10 继续负责真实音频、素材库和系统热键，通过随包的无界面后端运行。便携包见 [Releases](https://github.com/Benincasamatch/onbody_mic/releases/tag/v0.2.0)。版本历史见 [CHANGELOG](CHANGELOG.md)，本轮实测与限制见[验证记录](docs/07-validation-and-handoff.md)。

## 运行

解压 `OnbodyMic-0.2.0-desktop-portable-x64.zip`，打开 `win-x64/onbodymic-desktop.exe`。同目录的 `OnbodyMic.EngineHost.exe` 是自动启动的音频后端，不要单独运行或删除。便携包自带 .NET 运行时；Windows 需有 **Microsoft Edge WebView2 Runtime**，仅安装 Edge 浏览器不能代替运行时检查。

需要 Windows x64 和已安装的虚拟音频线缆。本项目历史实机路线为 **Steam Streaming Speakers**，程序不附带或安装驱动。Windows 11 以外系统、其他虚拟驱动和蓝牙组合未验证。

1. 在设备设置中创建预设，明确选择真实麦克风、Steam Render 虚拟输出和实际佩戴的耳机。Capture 供“实际虚拟麦”监听使用；仅试听或仅音效监听可不配置它。
2. 在 KOOK 中手动选择线缆 Capture（本机名称为“内部 AUX 插座 (Steam Streaming Speakers)”）作为输入。
3. 启动和切预设后所有发送与监听默认关闭。讲话开启“真麦发送”；音效外发开启“附加音频”；应用声音还需绑定应用并开启应用捕获。
4. 本地试听不需要开启发送或自动监听，但必须配置可用的监听耳机。
5. 关闭主窗口后继续在托盘运行；用托盘“退出”彻底停止。

端点显示“已禁用”时可在设备设置中选择后启用；“未插入”“未检测到”需检查设备连接或驱动。不能仅凭程序的发送状态判定远端已收到。

完整流程、热键、故障排查与数据位置见[使用说明](docs/05-usage-and-precautions.md)。

## 构建、测试与打包

原生桌面构建需要 Node.js/npm、Rust MSVC 工具链、Windows C++ 构建工具和 .NET 10 SDK：

```powershell
powershell -ExecutionPolicy Bypass -File build/build-desktop.ps1
```

脚本恢复前端依赖、发布自包含音频后端、构建原生桌面，并生成 `dist/OnbodyMic-<版本>-desktop-portable-x64/` 和 ZIP。已有包不会被覆盖，新产物自动加后缀。项目内 `.tools/cargo`、`.tools/rustup` 和 `.tools/dotnet` 可用时优先使用，不要求修改全局工具链。

`-Bundle` 额外构建 NSIS 安装包；其 WebView2 引导程序可能需要联网下载。便携包不自动安装 WebView2 或音频驱动。

```powershell
npm --prefix desktop run build
cargo test --manifest-path desktop/src-tauri/Cargo.toml --lib --release
dotnet test OnbodyMic.sln -c Release
```

`design-preview/` 仅保存选型时的三种布局示意，不是正式程序。旧版 WinForms 的 `build/build-portable.ps1` 和 `--selftest` 仍属于 0.1.x 历史维护入口，不能用于证明新版原生桌面或真实音频链路。

## 能力与边界

- 真麦、应用、音效独立增益；附加音频总开关和紧急静音锁存。
- WAV/MP3 受管素材库、搜索、收藏、单级分组、重命名、单条增益、卡片试听与发送；单条音效播放。
- 全局热键编辑、冲突检查和设置持久化。
- 应用按稳定路径保存配置，切预设后须重新确认当前进程绑定。
- 电平显示最近采样窗口内的各路峰值；诊断页另保留累计峰值。
- 其他程序直接写入共享线缆的声音不受本程序开关控制。应用捕获包含目标进程树；同一浏览器中的通话和音乐有回送风险。
- 设置、素材与日志默认存入 `%LOCALAPPDATA%\OnbodyMic`，可用 `--root` 指定独立目录。程序无音频上传功能。
- 启动、切换/删除活动预设和明确重启音频后端只恢复配置，不恢复发送、监听、播放或 PID 绑定。
- 网页/应用音频采集与导入后裁剪已列入 [P5A/P5B 计划](docs/03-roadmap-and-acceptance.md)，本版未实现。

## 项目资料

- [需求](docs/01-product-requirements.md)、[架构与安全边界](docs/02-architecture-and-safety.md)、[验收计划](docs/03-roadmap-and-acceptance.md)
- [依赖、许可证与决策来源](docs/04-sources-and-decisions.md)、[模块契约](docs/06-module-contracts-and-data.md)
- [验证与交接](docs/07-validation-and-handoff.md)、[历史 P4 实机记录](docs/08-p4-test-report.md)、[界面方案](docs/09-ui-modernization-plan.md)

历史记录中的性能、KOOK 和设备结论只适用于当时环境，不能代替当前版本复测。当前操作方法以使用说明为准。
