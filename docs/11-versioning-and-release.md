# 版本、提交与发布规范

本文规定版本号来源、提交格式、安装包与更新检查的发布流程。历史提交格式不回溯改写；从本文生效起的新提交按此执行。

## 1. 版本语义

采用语义化版本 `MAJOR.MINOR.PATCH`，发布标签固定为 `vMAJOR.MINOR.PATCH`。

- `MAJOR`：不兼容的数据/配置/命令行契约变化。
- `MINOR`：向后兼容的新功能，包括新的桌面界面、更新模块和安装程序。
- `PATCH`：向后兼容的缺陷修复、文档和打包修正。
- 预发布使用 `-beta.1`、`-rc.1` 后缀；GitHub 上标记为 prerelease，更新检查只提示正式版。

版本号只允许通过脚本修改，避免 C#、Rust、Tauri 与前端四处漂移：

```powershell
powershell -ExecutionPolicy Bypass -File build/set-version.ps1 0.3.0
```

脚本同步 `Directory.Build.props`、`desktop/package.json`、`desktop/package-lock.json`、`desktop/src-tauri/Cargo.toml`、`Cargo.lock` 和 `tauri.conf.json`。`build/tag-release.ps1` 会再次核对全部版本、CHANGELOG 段落和工作树。

## 2. 提交规范

采用 Conventional Commits：

```text
<type>(<scope>): <subject>

<body>

<footer>
```

- `type`：`feat`、`fix`、`docs`、`refactor`、`perf`、`test`、`build`、`ci`、`chore`、`revert`。
- `scope`：可省略；建议使用 `audio`、`library`、`desktop`、`engine`、`platform`、`persistence`、`build`、`docs`。
- `subject`：祈使句，不超过 72 个字符，不以句号结尾；中文或英文均可，但同一提交内保持一致。
- `body`：说明原因、行为变化和验证证据；不要只复述 diff。
- `footer`：关联 issue 用 `Refs: #12`；破坏性变更写 `BREAKING CHANGE: ...`。

示例：

```text
feat(desktop): add in-app version check and installer handoff

Compare semver against the latest GitHub release, show commit/build
stamps and release notes, and open the download URL externally.
Verified with node --test and a real Tauri window.
```

## 3. 发布流程

1. 更新 `CHANGELOG.md`：新增 `## [X.Y.Z] - YYYY-MM-DD` 段落，按“新增 / 修复 / 验证 / 已知限制”组织。更新检查展示的“当前版本说明”来自该文件。
2. 修改版本：`build/set-version.ps1 -Version X.Y.Z`。
3. 构建并验证：`build/build-desktop.ps1 -Bundle`。该命令同时生成便携目录/ZIP、NSIS 安装程序、更新签名和清单（构建前需已运行一次 `build/update-key.ps1`）：
   - `dist/OnbodyMic-X.Y.Z-desktop-portable-x64/`
   - `dist/OnbodyMic-X.Y.Z-desktop-portable-x64.zip`
   - `dist/OnbodyMic-X.Y.Z-desktop-installer-x64.exe`
   - `dist/latest.json`（含安装包签名与便携包 SHA-256）
4. 提交版本与 CHANGELOG：`chore(release): vX.Y.Z`。
5. 打标签：`build/tag-release.ps1 -Version X.Y.Z`；确认后加 `-Push` 推送标签。
6. 在 GitHub Release 上传安装程序、便携 ZIP 和 `latest.json`，三个资产必须使用构建输出的原始文件名（清单里的地址按该文件名生成，改名会让更新 404）。未上传 `latest.json` 的版本只能手动下载更新；资产名包含 `setup` 和 `.exe` 时，手动入口优先提供安装程序链接。

`build/tag-release.ps1` 不会自动提交、构建或上传；它只验证版本一致性、CHANGELOG 段落和标签唯一性，然后创建 annotated tag。

## 4. 应用内版本与更新模块

- `desktop/src-tauri/build.rs` 在构建时写入 Git 短哈希、提交时间、UTC 构建时间和工作树脏标记；缺失 Git 元数据时写 `unknown`，不伪造值。
- `app_info` IPC 命令返回构建信息、仓库/发布页地址、更新 API 和解析后的 CHANGELOG。更新说明直接来自 `CHANGELOG.md`，不复制到前端。
- 桌面端最多每 12 小时自动请求一次 `https://api.github.com/repos/Benincasamatch/onbody_mic/releases/latest`；也可以从页脚版本号打开“关于随声麦与更新”手动检查。
- 更新比较在前端按语义化版本执行；预发布和草稿不提示。自动检查失败保持静默，手动检查显示原因。
- 发现新版本时显示横幅和“关于”对话框；点“立即更新”才会下载，不自动安装、不替换正在运行的文件。安装版由 `tauri-plugin-updater` 读取 `latest.json`、校验 minisign 签名后以 `/P /UPDATE /R` 静默覆盖并自动重启；便携版由 `update.rs` 的 `install_portable_update` 下载 ZIP、校验清单 SHA-256，退出后由辅助脚本替换 `win-x64` 程序并重启。`installKind` 按可执行文件旁是否存在 `uninstall.exe` 判定，开发构建不执行应用内安装。
- 检查与下载只读取公开版本信息和发布资产，不包含设备、配置、音频或用户标识，也不向仓库写入数据。

## 5. 更新签名与清单

- 首次构建安装包前运行 `build/update-key.ps1`：生成 `.tools/updater/onbodymic.key`（私钥，`.tools/` 已被 git 忽略）和 `password.txt`，并把公钥写入 `tauri.conf.json` 的 `plugins.updater.pubkey`。私钥或密码丢失后无法再签发新版本，已安装用户将无法应用内更新；换机需迁移这两个文件。
- `build/build-desktop.ps1 -Bundle` 在构建时注入 `TAURI_SIGNING_PRIVATE_KEY` 与 `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`，Tauri 生成 `*.exe.sig`；脚本读取签名并写出 `dist/latest.json`：`version`、`notes`（CHANGELOG 当前版本段）、`pub_date`、`platforms.windows-x86_64.{signature,url}` 以及扩展字段 `portable.{url,sha256,size}`。
- 更新端点为 `https://github.com/Benincasamatch/onbody_mic/releases/latest/download/latest.json`；GitHub 的 `latest/download` 跟随最新正式 Release，预发布不会命中。
- 信任边界：安装包由 minisign 公钥校验；便携包校验清单中的 SHA-256，清单本身经 HTTPS 获取但未做 minisign 签名，因此便携版的自替换信任级别等同 GitHub 账号与 HTTPS。便携替换只覆盖两个可执行文件，不删除用户文件。
