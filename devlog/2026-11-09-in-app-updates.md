# 2026-11-09 · 应用内检查并更新

## 背景
用户要求“增加应用内检查并更新的功能”。原实现只检查 GitHub Releases 并在浏览器打开下载链接，文档也明确写了“不自动下载或安装”。

## 需求确认
用户选择：

| 项 | 选择 |
|---|---|
| 信任链 | 官方 `tauri-plugin-updater`（minisign 签名校验） |
| 覆盖范围 | 安装版（NSIS）+ 便携版自替换 |
| 安装后 | 自动重启新版本 |

## 决策
1. **安装版走官方插件**：`latest.json` 作为 GitHub Release 资产，`releases/latest/download/latest.json` 作为端点；安装包由 `tauri-plugin-updater` 下载、校验、提取后以 `/P /UPDATE /R` 静默安装。`restart_after_install` 保持默认 `true`，由 NSIS 重启。
2. **便携版无法用安装包**（会把便携副本变成第二份安装）：清单里增加扩展字段 `portable.{url,sha256,size}`；`install_portable_update` 下载 ZIP、流式 SHA-256 校验，写辅助 PowerShell 脚本，脚本等待本进程退出后解压、替换 `win-x64` 下两个可执行文件并重启。插件解析清单时忽略未知字段，扩展字段不会被它拒绝。
3. **安装前先停音频后端**：新增 `desktop_command("update_prepare")`，复用 `exit_app` 的清理逻辑（向后端发 `exit`、3 秒超时、终止子进程）但不调用 `app.exit`；插件随后 `std::process::exit(0)` 不会留下孤儿 EngineHost。若 `install()` 校验失败，前端调用 `restart_engine` 恢复。
4. **不引入 `tauri-plugin-process`**：Windows 下安装器自己重启，便携版由辅助脚本启动，`process` 插件没有用武之地，已移除。
5. **开发构建不自我更新**：`install_kind()` 在 `debug_assertions` 下返回 `development`，避免把 `target/debug` 当便携目录替换。

## 修改文件
- `desktop/src-tauri/Cargo.toml`：新增 `tauri-plugin-updater`、`reqwest`（stream）、`rustls`（ring provider）、`sha2`。
- `desktop/src-tauri/tauri.conf.json`：`bundle.createUpdaterArtifacts`、`plugins.updater`（公钥、端点、`installMode: passive`）。
- `desktop/src-tauri/capabilities/main.json`：`updater:default`。
- `desktop/src-tauri/src/lib.rs`：注册 updater 插件与 `install_portable_update` 命令。
- `desktop/src-tauri/src/update.rs`（新增）：`install_kind`、URL/哈希校验、下载进度事件 `update_progress`、SHA-256、辅助脚本生成与启动。辅助脚本优先用 `tauri-plugin-shell` 的 Rust 命令启动（与本程序启动音频后端相同的 `CREATE_NO_WINDOW` + 管道方式，打包构建里普通 `std::process::Command` 曾被拒绝为 `ERROR_NOT_SUPPORTED`），失败时回退到 `std` 并带上两条错误原因。
- `desktop/src-tauri/src/update-helper.ps1`（新增）：等待 PID 退出、解压、按字节数校验替换、读取 `restart-args.txt`（Windows 命令行转义后的原启动参数）、成功或失败都重新启动程序、写 `%TEMP%\onbodymic-update.log`。
- `desktop/src-tauri/src/runtime.rs`：抽出 `stop_backend`，新增 `exit_for_update`（置退出标记后停后端）与 `update_prepare` 操作。
- `desktop/src-tauri/src/version.rs`：`AppInfo.install_kind`。
- `desktop/src/version.ts`：`AppInfo.installKind`。
- `desktop/src/update.tsx`：`check()` + `download()` + `install()` 流程、便携清单解析、进度事件订阅、`InstallStatus` 组件、横幅与关于页按钮/错误/进度。
- `desktop/src/App.tsx`、`desktop/src/styles.css`：接线与进度条样式。
- `build/update-key.ps1`（新增）、`build/build-desktop.ps1`：签名环境注入与 `latest.json` 生成。
- 文档：README、docs/02 §12、docs/05 §5、docs/06、docs/11 §3–§5、CHANGELOG。

## 行为
- 检查仍由前端按 12 小时节流，数据来自 GitHub API；只有点击“立即更新”才走插件/下载。
- 清单版本落后于 GitHub 最新 Release 时拒绝安装并提示（避免装到旧包）；清单缺失（旧 Release 未上传 `latest.json`）时提示改用手动下载。
- 更新前不保存未保存的预设改动（与 `exit_app` 一致），界面在安装阶段会被进程退出中断。

## 验证
- `cargo test --lib`：17 项通过（哈希格式/大小写、URL 白名单、Windows 重启参数转义、更新包 SHA-256、CHANGELOG 解析）。
- 辅助脚本单独实测（`.tools/tmp/test-helper.ps1`）：模拟旧进程 4 秒后退出，脚本替换两个文件、按字节数确认、以 `--root "C:\a b"` 原参数重启（`%~1`=`--root`、`%~2`=`C:\a b` 未拆分）、清理 zip 与参数文件、写入 `updated` 日志。
- 便携版端到端（隔离数据根 `--root .tools/tmp/e2e/data`，本地 `127.0.0.1:8123` 提供 0.7.1 便携包与清单，`ONBODYMIC_UPDATE_ALLOW_LOCAL=1`）：在“关于随声麦与更新”横幅出现 v0.7.1 与“立即更新”；点击后横幅显示下载百分比，下载 49.5 MB 后程序退出；辅助脚本把 `app/win-x64/onbodymic-desktop.exe` 替换为负载文件（SHA-256 前 16 位 `27d45bda45048e6f`，大小 16 869 888），`%TEMP%\onbodymic-update.log` 写入 `updated`，zip/参数文件与解压目录清理，新进程以 `--root E:\items\nether\projects\onbody_mic\.tools\tmp\e2e\data` 重启并重新拉起音频后端。
- 过程中发现并修复两处：`app.exit(0)` 被退出钩子拦住（改用置位后的 `exit_for_update`）；打包程序里普通 `std::process::Command` 启动 PowerShell 返回 `ERROR_NOT_SUPPORTED`（改用 `tauri-plugin-shell` 的同款启动路径，std 仅作回落）。
- 安装版端到端（经用户同意）：本地端点构建的 0.7.0 安装包覆盖 `E:\tools\OnbodyMic`，本地清单提供签名的 0.7.1 安装包；点击“立即更新”后插件校验签名并退出应用，NSIS 静默覆盖并在约 30 秒内自动重启；注册表 `DisplayVersion` 0.7.0→0.7.1、新进程保留 `--root` 参数、`installKind=installed`，插件 `check` 对同一清单返回“无更新”，确认运行的是 0.7.1。之后用正式 0.7.0 安装包恢复了该目录（`DisplayVersion` 回到 0.7.0）。

## 已知限制
- 便携版只替换两个可执行文件，包内 README/CHANGELOG/docs 不随之更新。
- 便携包信任级别为 HTTPS + GitHub 账号（清单里的 SHA-256 未做 minisign 签名）；安装包有 minisign 签名。
- 安装版要求用户此前通过 NSIS 安装；NSIS `/UPDATE` 模式不支持“便携版转安装版”。
- 安装版更新会覆盖现有安装目录；NSIS 打包会改写可执行文件（bundle 信息），安装后文件哈希不等于构建目录里的文件，判断版本需用注册表 `DisplayVersion` 或插件 `current_version`。便携版下载地址的 loopback 例外仅在 `ONBODYMIC_UPDATE_ALLOW_LOCAL=1` 时生效，用于本地验证，默认关闭。