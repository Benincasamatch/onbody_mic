# 2026-10-26 界面现代化（用户 7 项建议清单）开发日志

## 范围与依据

用户提出 7 项 UI 改进清单 + 示意稿 `docs/随声麦 · 界面改版示意.html` + ToggleSwitch 示例代码（用户注明未编译验证，落地时需补键盘/焦点/禁用态）。方案落档 `docs/09-ui-modernization-plan.md`，待确认后分阶段实施。

## 调研事实（代码级，均已读取核实）

1. 当前 UI：WinForms，设计令牌 `Theme.cs`（DIP/DPI 体系、12 语义色、按钮工厂），主窗 `MainForm.cs`（1926 行，TableLayoutPanel 内容栈 + 500ms 轮询驱动全部状态文本），卡片 `RouteCard.cs`（状态芯片 + 描述行 + 参数行骨架）。
2. 状态数据源完整可用：`SessionStatus`（EmergencyLatched/OutputLost/Switches/Clip/DevicesValidated/MonitorMode）+ `SessionMetrics.MixerPeak`（float，实时峰值）。电平条**无需改音频层**。
3. 音效热键在 `Soundboard.Settings.Data.ClipHotkeys`（Dictionary<string, string?>），桌面侧可直接读取；`Soundboard.BindHotkey/UnbindHotkey` 已是公共方法（HotkeyEditorForm 在用）。
4. 拖拽导入可复用 `TryImport(library, path, allowDuplicate)` + 重复确认对话框。
5. 布局回归有现成护栏：`LayoutAudit.cs`（--selftest，4 DPI × 3 尺寸，反射断言 `_emergencyButton/_routeGrid/_presetCombo`）——新控件加入后需扩展断言（ToggleSwitch 电平条行高等）。
6. 清单第 2 项的问题真实存在：`UpdateRouteChip` 中"不可用"用 `Theme.Error` 红字；"未绑定"在 `_extrasCard` 描述里但卡片状态有"已开启/已关闭"混用，措辞确实不统一。
7. 旧方案 §5.3"框架不支持圆角就不硬模拟"与用户清单冲突 → 以用户清单为准（计划文档已注明）。

## 实施记录（2026-10-26 当日完成）

用户已确认三个决策点：增益滑块 0–400%、电平条峰值保持+衰减、一次落地；ToggleSwitch 示例补齐后使用。

### 事实

1. **新控件**：`ToggleSwitch.cs`（用户示例为底，补 Tab/空格/焦点框/45% 禁用态/AccessibleName；`Text` 重写用 `#nullable disable/restore` 包住 setter——基类 `Control.Text` 标了 `[AllowNull]`，C# 无法在 accessor 重写上重复该注解，这是探针工程证实过的编译器限制）；`LevelMeter.cs`（MixerPeak 采样 + 0.72/tick 峰值衰减）；`IconFont.cs`（Fluent→MDL2→无 三级回退）；`ClipRow.cs`（52 DIP 行：★/名称/时长/分组/热键/▶/发送/⋯，Dock=Top 逆序添加保证库序）；`SmokeShots.cs`（selftest 内 PrintWindow 截图）。
2. **Theme 扩展**：Air 琥珀、StatusIdle/Sending/Warn/Fault 四态、Chip 底色族、IconBack、Meter 色、CardRadius=10/ButtonRadius=8；`RoundRect`/`CardChrome`/`ClipToCard`（Paint 圆角 + Region 裁剪）；`ButtonBase` 尺寸变化时重裁圆角；`StatusChip` 带语义底色；`Headline` 17 DIP、分区标题降 13 DIP muted。
3. **MainForm**：大状态条（灯+标题+副说明+四芯片，六态真值表，红=锁存/输出失效、绿=AnySendEnabled、琥珀=试听/待配置、灰=全关）；措辞统一（已关闭→关、已开启→开，红只留设备不可用/锁存）；增益全部 0–400% 滑块+百分比；电平条接线（micActive=发送开启且设备在；clipActive=播放中；空闲 −∞ dB）；底部状态栏只留「运行正常/电平异常」+「诊断 ●」，指标明细进弹层；音效库换 ClipRow 列表 + 拖放导入（复用 TryImport+重复确认，多文件循环）+ 文件对话框 Multiselect；热键列读 `Soundboard.Settings.Data.ClipHotkeys`，点击直开 HotkeyEditorForm，编辑后 RefreshClips 同步列。
4. **教训**：外部 `WM_VSCROLL`/`WM_MOUSEWHEEL` 对 WinForms AutoScroll Panel 无效（两次离屏抓图零像素差证实），进程内 `AutoScrollPosition` 一行解决——截图取证基建收进 `--selftest`（`SmokeShots`），下次改 UI 不再需要外挂脚本。

### 验证（全部实测）

| 项 | 结果 |
| --- | --- |
| 构建 | 0 警告 0 错误 |
| 全量测试 | 181/181 通过（Domain 45 / Audio 28 / Persistence 29 / Media 19 / Application 60） |
| `--selftest` | layout OK（96/120/144/192 DPI × 3 尺寸），96 DPI 顺带输出 top.png / scrolled.png |
| 冒烟截图 | 大状态条灰态+设备不可用红芯片、四圆角卡片+图标块+ToggleSwitch+滑块+电平条（−∞ dB）、音效两行（★/设置热键/▶/琥珀发送/⋯）、拖放提示、状态栏诊断按钮全部真实渲染 |
| 遗留 | 深色模式（Theme 语义色已就位未实现第二主题）；真实发声路径的绿/琥珀态需有虚拟线缆环境才能截图取证（当前环境虚拟麦 Capture 端点缺失，大状态条如实显示红字提示） |

## 状态

- [x] 调研：项目结构、示意稿、当前 UI 代码、数据源
- [x] 计划：docs/09-ui-modernization-plan.md（A 状态系统 / B 控件现代化 / C 按钮权重 / D 音效库 / E 图标层级）
- [x] 决策点 1：增益滑块范围 → 0–400%（用户确认）
- [x] 决策点 2：电平条平滑方式 → 峰值保持+衰减（用户确认）
- [x] 决策点 3：一次落地（用户确认）
- [x] 阶段 A–E 实施
- [x] 验证（构建/测试/--selftest/截图冒烟）
- [ ] 用户试用反馈（多 DPI/窄窗口/真实开黑操作流）
