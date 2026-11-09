# 2026-11-07 批量导入设置与多选批量修改 开发日志

## 本轮范围

用户指令：“更改添加音效的逻辑，导入时可以进行批量更改，而不是一个一个的进行更改。” 确认范围后按两项落地：

1. **导入时批量设置**：一次导入 2 个及以上文件/文件夹时，先弹一次设置，统一应用到本批（分组、单条增益、收藏、名称规则）。
2. **库内多选批量修改**：音效卡支持多选，一次修改多条的同样四项。

改动涉及 `src/OnbodyMic.Media`、`src/OnbodyMic.EngineHost`、`desktop/src`、`desktop/src-tauri`、测试、README、使用文档。

## 现状与问题

- 导入流程为逐文件 `inspect_audio` → `import_clip`；单条成功即入库，没有任何“本批统一设置”的入口。
- 入库后只能打开单张卡片详情逐条调用 `clip_rename` / `clip_gain` / `clip_group`。
- `import_clip` 原本只接受 `displayName`、`groupId`、`allowDuplicate`，不接受初始增益/收藏。
- 每个单条修改命令各自写一次 `index.json` 并触发一次刷新，逐条套用会放大 I/O 与刷新次数。

## 决策

1. **导入即带元数据**：为 `import_clip` 与 `import_trimmed_audio` 增加可选 `gain`、`favorite`，与名称/分组一起在一次素材写入中完成；不在导入后追加 `clip_gain`/`clip_favorite`。重复副本与裁剪后导入复用同一设置，行为一致。
2. **原子批量命令**：新增 `clip_batch_update`，`ClipLibrary.UpdateMany` 先校验全部 ID、目标分组、增益、名称，再一次性替换并 `SaveIndex()`；写失败回滚所有被改条目。避免 N 次写盘和“改到一半失败”的半成品状态。
3. **选择模式而非仅 Ctrl 点击**：单击卡片原本是“打开详情”，与多选冲突。工具栏增加“多选”显式模式：模式内单击切换选择，Shift 连选，双击播放禁用；退出模式恢复原交互。
4. **名称规则统一**：前缀 / 后缀 / 查找替换三种；不填或选择“保留原名”时不发送 `displayName`，沿用后端文件名默认值。库内修改的预览使用第一条选中音效。
5. **单文件不弹窗**：保持原导入体验；只有 `files.length >= 2` 才进入批量设置。
6. **四项均可“不修改”**：库内批量修改默认不改变任何字段，未选规则不下发对应参数；`groupId: null` 与“缺少 groupId”语义区分——前者表示移到未分组。

## 工程成果

### 后端

- `src/OnbodyMic.Media/ClipLibrary.cs`
  - `Import` / `ImportSelection` / `ImportCore` 增加 `gain`、`favorite`，导入前校验增益有限且 0–4；条目直接写入这两个字段。
  - 新增 `UpdateMany(clipIds, applyGroup, groupId, gain, favorite, rename)`：ID 去重、缺失 ID/未知分组/非法增益/空名称整批拒绝；无实际变化时不写盘；成功后 `Changed` 一次。
- `src/OnbodyMic.EngineHost/EngineRuntime.cs`
  - `import_clip`、`import_trimmed_audio` 解析可选 `gain` / `favorite`。
  - 新增 `clip_batch_update`：校验 `clipIds` 数组、`groupId` 是否存在（null 有效）、`gain`、`favorite`、`rename.mode`（prefix/suffix/replace）与 `rename.value`。
- `desktop/src-tauri/src/runtime.rs`：命令白名单加入 `clip_batch_update`。

### 前端

- `desktop/src/types.ts`：`ImportFile` 增加 `displayName?`、`gain?`、`favorite?`；新增 `RenameRule`、`BatchImportOptions`。
- `desktop/src/context.tsx`
  - `importPaths`：准备队列后，单文件直接导入；多文件进入 `pendingImportBatch`。
  - `confirmImportBatch`：按设置映射名称/分组/增益/收藏入队；`cancelImportBatch` 清空本批。
  - 多选状态：`selectionMode`、`selectedClipIds`、`toggleClipSelection`（Shift 连选）、`selectAllClips`、`clearClipSelection`；音效库变化时剔除已不存在的 ID。
  - `saveAudioImport` 传递批量增益/收藏；`perform` 刷新列表加入 `clip_batch_update`。
  - 导出 `fileBaseName` / `applyRenameRule` 供两个对话框共用。
- `desktop/src/dialogs.tsx`：新增 `ImportBatchDialog`、`ClipBatchDialog` 与共用 `RenameFields`；`PendingDialogs` 渲染批量导入；重复确认文案补充增益/收藏。
- `desktop/src/components.tsx`：卡片在选择模式显示勾选块，单击切换选择；`ImportButton` 在批量弹窗存在时禁用。
- `desktop/src/App.tsx`：工具栏“多选”入口与批量操作条（已选数量、全选、清除、批量修改、退出多选）；渲染批量修改对话框。
- `desktop/src/styles.css`：`.form-check`、`.tile-check`、`.sound-tile.picked`。

## 验证

| 层面 | 结果 |
|---|---|
| Media 单测 | 73 通过（新增 3：`Import_applies_initial_gain_and_favorite_and_rejects_invalid_gain`、`Update_many_applies_group_gain_favorite_and_rename_with_one_persisted_change`、`Update_many_rejects_invalid_batch_without_partial_changes`） |
| 全量后端 | Domain 46 / Audio 34 / Persistence 30 / Application 81 全通过 |
| 构建 | `OnbodyMic.sln`、`OnbodyMic.EngineHost` 0 警告 0 错误；`cargo check`（Tauri）通过 |
| 前端 | `tsc --noEmit`、`vite build` 通过 |
| EngineHost 实机 | 3 个 WAV 导入时 50% 增益 + 收藏；`clip_batch_update` 改分组/25%/取消收藏/前缀 → 索引逐项正确；非法 `gain=5` 返回 `invalid-argument` 且索引不变；`groupId:null` 批量移出分组成功 |
| 真实浏览器（Tauri IPC 桩） | 批量导入 3 文件统一“冒烟-”前缀、150% 增益、收藏，卡片显示 `WAV · 150% 增益`；多选 3 卡批量改为“游戏”/50%/取消收藏/前缀，提交参数与卡片文本逐项一致；退出多选后勾选块消失 |

证据目录：`.tools/tmp/batch-smoke-*`（验证后已删除）；浏览器验证使用 `127.0.0.1:1425` 开发前端与全量 IPC 桩。

## 已知限制

- 批量设置只提供“统一值”，不支持本批内逐文件覆盖；需要单条差异时仍可导入后打开卡片详情修改。
- 重复内容与超过 60 秒的文件仍逐项确认/裁剪，批量设置只作用于成功导入的条目。
- 多选模式暂不提供批量删除（用户本轮只要求分组/增益/收藏/名称）；删除仍走单条详情确认。
- 名称查找替换为全局字面替换（区分大小写），不支持正则。

## 下一步

- 视用户反馈决定是否增加批量删除或“本批文件列表逐项微调”。
- 若素材量增大，可评估批量导入的进度反馈（当前逐条导入沿用既有状态栏“正在导入”）。