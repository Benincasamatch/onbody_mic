# 2026-10-28 全项目检查与体验修复

本轮范围：当前工作区的构建、测试、桌面自检、源码使用流程检查、NuGet 依赖审计、修复及本地交付。使用现有 docs/ 与 devlog/；未授权公开发布。

## 初始证据

- 基线提交 b75ad53；源码版本 0.1.2。原有未跟踪文件：build/shots/ 与 docs/随声麦 · 界面改版示意.html。
- Release 构建 0 警告 / 0 错误；现有 181 测试通过（Domain 45、Audio 28、Application 60、Persistence 29、Media 19）。TRX：`.tools/audit-2026-10-28/tests/`。
- 桌面 `--selftest` 在独立 root 下通过；其内置截图输出位于 build/shots/。
- 当前端点枚举：Steam Render 与内部 AUX Capture 均 Active；不能据此断言 KOOK 实收正常。
- NuGet 官方源查询（含传递依赖）：13 个工程均未报告已知漏洞。这不是对未知漏洞的保证。

## 工作步骤

1. 补充“监听关时试听、迟到完成/设备回调、切预设旧绑定、验证异常、命令队列异常”复现测试。
2. 修复已复现缺陷；改善真实电平、状态说明、应用绑定保存和可复现打包。
3. 全量回归、桌面自检与本地包验证；在 docs/10-project-audit.md 中记录结果和未覆盖边界。

## 实施与复现

- 先建立失败回归，再修复试听缺输出、迟到完成/故障回调、切预设残留 PID、验证异常和队列异常行为。
- 补查发现试听→发送交叉淡化泄漏、停止后尾音复活、大块停止读取恢复满音量，增加回归并引入 Discard 与模式/epoch 锁内检查。
- 慢解码阻塞静音/停止的用例先失败，改为后台串行解码，失效请求丢弃；增加同一模拟热键泵紧急静音、连续请求顺序与切预设取消测试。
- 增加独立最近峰值计量、加载状态及停止按钮、可用端点选择和明确的本地发送状态。具体变更见检查报告。
- 改写 README、当前使用说明和快速开始，历史发布记录保留。
- 打包脚本取消对忽略文件 dev.cmd 的依赖，取消强停旧进程及删除旧包；唯一暂存目录、自检后打包，保留证据。

## 最终检查

- Release 全工程构建：0 警告、0 错误。
- 最终测试：202/202，Domain 45、Audio 34、Application 75、Persistence 29、Media 19；无跳过。证据 `.tools/audit-2026-10-28/verified-tests/`。
- 桌面自检：4 DPI × 3 主窗尺寸，首次向导/设备/热键对话框通过。
- 视觉复核发现原有 PrintWindow 离屏截图有空白卡片/残影，不能作为证据；改用 DrawToBitmap，新的顶屏与滚动截图已逐张检查。新输出在 `desktop-rendered/shots/`，不再覆盖 build/shots/。
- NuGet 最终直连一度 TLS EOF；使用本机现有 127.0.0.1:7892 代理后官方查询成功，13 工程未报告已知漏洞。保存 `nuget-final.json`。
- 真实 KOOK、物理热键/游戏全屏、当前版本两小时音频和第二台机器仍未复测。

## 工作区保留说明

未提交或推送代码。原有未跟踪 HTML 保留。本轮最初基线自检沿用旧截图逻辑，覆盖了原有 build/shots/top.png 和 scrolled.png；没有旧副本可确认恢复，已如实记录。后续所有自检均改为独立根目录。

## 本地交付确认

- Windows PowerShell 5 实际执行 `build/build-portable.ps1` 成功；脚本有 UTF-8 BOM，中文路径可用。首次发布因系统 Temp 目录访问被拒失败，使用项目内 TMP/TEMP/APPDATA 后重试通过；失败暂存目录保留。
- 产物：`dist/OnbodyMic-0.1.3-portable-x64.zip`，约 48.2 MiB，301 个文件；展开程序目录 `dist/OnbodyMic-0.1.3-portable-x64/win-x64/`。
- ZIP SHA256：`849C1C40BCE034D073CD594DBAF2625D8BAECCB729B0AC38B5A461406CED1AAF`。
- 发布产物自身退出码 0、自检 `selftest: OK`。证据在发布文件夹下的 selftest 日志及截图。
- 另解压到 `.tools/audit-2026-10-28/unpacked-package/`，逐个文件比较 SHA256，301/301 与原发布文件一致；解压 EXE 再次自检通过，包内 CLI `devices --all` 枚举成功。Steam Render 与 AUX Capture 仍为 Active。
- 标准 Git diff whitespace 检查通过（Windows CRLF 使用 `core.whitespace=cr-at-eol`）；先前禁用 autocrlf 的尝试将所有 CRLF 误识别为行末空白，未据此重写源文件。
- 发布包内检查报告在发布前生成，描述源码/测试/自检范围；本节记录发布后 ZIP 哈希与解压验证，作为交付结果补充。
