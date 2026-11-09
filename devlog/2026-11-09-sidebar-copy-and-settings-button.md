# 2026-11-09 · 侧栏文案精简与设置按钮

## 背景
用户通过 EditHere 对随声麦主界面提交 3 条批注：删除两处说明文字，并把“打开设置”做成按钮。

## 批注对照

| # | 意见 | 实施 |
|---|---|---|
| 1 | 去掉“用声音 随时在手边” | 删除 `.sidebar-heading` 内说明段落，保留 `YOUR COLLECTION` 与“声音收藏夹” |
| 2 | 去掉“本地试听与对外发送清楚分开” | 主区标题仅在选中分组时渲染面包屑，未选中时不再输出说明段落 |
| 3 | “将这个打开设置做成按钮形式” | 原 `.sidebar-note` 容器 + 透明文字按钮合并为单个 `<button class="sidebar-note">`，样式对齐分类导航：37px 高、`--bevel-line` 描边、`--face` 底、悬停/按下态 |

## 修改文件
- `desktop/src/App.tsx`：侧栏标题与主区回退段落删除；设置入口改为整行按钮
- `desktop/src/styles.css`：`.sidebar-note` 改为按钮外观；删除 `.sidebar-note p`、`.settings-button`、`.sidebar-heading p`、`.page-heading p` 及 950px 断点内已失效的 `.sidebar-heading p` 规则

## 行为不变
- 点击“打开设置”仍调用 `openPanel('settings')`，打开统一设置面板
- 收起侧栏（`.sidebar-collapsed`）与 700px 以下断点仍隐藏该入口，与改动前一致

## 验证
- `npm run build`（`tsc --noEmit` + vite）通过
- Chromium 1652×1077 加载 dev 页面：侧栏只有“声音收藏夹”、主区“音效台”无副标题；`button.sidebar-note` 存在且为 37px 描边按钮
- 点击该按钮：`dialog[open]` 标题为“设置”