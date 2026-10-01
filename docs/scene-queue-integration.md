# Scene 生成队列与批量自动录制对接

ugcplatform 侧核对日期：2026-09-26，来源为本机 `/Users/jammie/Code/ugcplatform` 工作区源码；未验证线上部署或实际数据库/队列连接。ugasu 侧（批量录制实现）更新于 2026-10-01。本文件供 ugasu（本仓库）开发批量录制使用。

## 1. 当前可用入口与边界

- 全站队列快照存于 Postgres 的 `scenes` 表，包含供应商生成状态和 Unity 转换状态。
- 管理员页面：`/admin/scene-queue`；接口：`GET /api/admin/scene-queue`。
- 无人值守读取可由受信任的服务端进程使用 `DATABASE_URL` 查询；现有管理员接口要求 Clerk 管理员登录，**没有独立的队列查询 API key**。
- `ugcplatform` 中没有 Azure Queue 的入队/领取实现，无法从该仓库确认真实队列名、Queue endpoint、消息格式、租约或死信策略。数据库快照不等于 Azure 队列长度。
- ugasu World Studio 已在「8 · 批量录制」实现批量流程（见第 6 节）：拉取队列只读展示、勾选后串行 下载 → 导入 → 人工对齐 → 出生点/活动点 → 人工取景 → 录制 → 上传 → 回写。**尚未实现任务原子领取**，多进程仍可能重复处理同一任务。

## 2. key、凭据位置与用途

本机配置来源：`/Users/jammie/Code/ugcplatform/.env.local`。实际值只通过本机配置/部署密钥管理交接，不写进本文件、Unity Assets、Git 或日志。不要整份复制 `.env.local`；worker 仅注入它所需的凭据。

| key | 用途 | 本机检查结果 |
| --- | --- | --- |
| `DATABASE_URL` | Neon/Postgres 连接串；查询 `scenes`。worker 建议单独使用最小权限账号 | 已配置，未输出值 |
| `SCENE_CONVERT_CALLBACK_TOKEN` | HTTP convert-callback 的共享密钥（当前批量录制直连数据库，不使用此 key；仅未来切换到 callback 回写时需要） | 未配置/为空 |
| `AZURE_STORAGE_CONNECTION_STRING` | 现有 Azure Blob 服务端读写/签 SAS 的凭据 | 已配置，未输出值；不据此推断真实 Queue 配置 |
| `AZURE_SCENE_CONTAINER` | 场景 Blob 容器名，不是密钥 | 未配置，源码默认 `scene-assets` |
| `WORLD_LABS_API_KEY` | 调用供应商生成/刷新任务；不是队列查询 key，也不是回调 token | 已配置；只读现有待转换任务不需要它 |

以上只说明本机文件中是否存在非空配置，未核验有效性或线上环境。

## 3. 如何立即查询

### 3.1 现有命令：人工排查

在 UGC 项目目录运行（脚本按当前工作目录读取 `.env.local`）：

```bash
cd /Users/jammie/Code/ugcplatform
npm run show:scene-queue
npm run show:scene-queue -- --all --limit=50
npm run show:scene-queue -- --failed --limit=50
```

默认显示在途任务，默认上限 40，最大 200；`--all` 优先于 `--failed`。输出是面向人工的文本，不是 worker JSON 协议，可能包含用户身份信息。不要把日志公开或依赖解析这个表格来领取任务。

### 3.2 管理员 HTTP 接口：观察状态

在已登录且 `Clerk publicMetadata.isAdmin === true` 的网站同源浏览器控制台执行：

```js
const response = await fetch(
  '/api/admin/scene-queue?scope=active&page=1&pageSize=20',
  { credentials: 'same-origin' }
);
if (!response.ok) throw new Error(`queue HTTP ${response.status}`);
const snapshot = await response.json();
console.log(snapshot);
```

| 参数 | 规则 |
| --- | --- |
| `scope` | `active`（默认）、`failed`、`all` |
| `page` | 从 1 开始 |
| `pageSize` | 默认 20，最大 80 |
| `query` | 最长 80 字符；搜索名称、prompt、operation id、convert task id、用户邮箱/用户名；不是 scene id 精确查询 |

响应结构：`{ scope, rows, stats, pagination, stuckAfterMinutes }`。

- `rows`：`id, name, prompt, model, status, convertStatus, convertTaskId, convertQueuedAt, operationId, providerSceneId, marbleUrl, thumbnailUrl, error, convertError, createdAt, updatedAt, ownerClerkId, ownerEmail, ownerUsername, ownerName`。
- `stats`：`generating, awaitingConvert, queued, processing, convertReady, convertFailed, stuck`。统计针对全站有 `operation_id` 的行，不随当前搜索/分页过滤；计数可能重叠，不应相加当作任务总数。
- `pagination`：`page, pageSize, total, totalPages, hasPrevious, hasNext`。
- `stuckAfterMinutes = 30`：只标识等待太久的 `pending`，不代表自动失败或可以重新领取。
- `active` 按 `created_at` 升序，其余按降序。实时分页不是稳定快照，不能用页码充当认领游标。
- 未登录为 401，非管理员为 403。该接口不会主动刷新供应商生成状态，也不会返回完整录制资产字段或认领任务。

### 3.3 SQL：读取待转换任务及输入资产

以下为只读 SQL，可在使用 `DATABASE_URL` 的服务端数据库客户端中执行：

```sql
SELECT id AS scene_id, name, operation_id, provider_world_id,
       status, convert_status, convert_task_id, convert_queued_at,
       marble_url, thumbnail_url, pano_url, collider_mesh_url,
       metadata->>'splatUrl' AS splat_url,
       metadata->'spzUrls' AS spz_urls,
       metadata->>'hdrPanoUrl' AS hdr_pano_url,
       world_json_url, preview_video_url,
       error, convert_error, created_at, updated_at
FROM scenes
WHERE operation_id IS NOT NULL
  AND status = 'succeeded'
  AND convert_status = 'pending'
ORDER BY COALESCE(convert_queued_at, created_at) ASC NULLS LAST, id ASC
LIMIT 20;
```

使用参数化查询查看指定任务：`SELECT * FROM scenes WHERE id = $1`，`$1` 绑定整数 scene id。检查转换完成记录时将候选 SQL 的 `pending` 改为 `ready`；不要把这些记录再次纳入普通待处理批次。

`SELECT` 仅发现候选，不会认领任务。真实批处理必须先接入既有调度器或实现原子领取，否则多个进程会重复录制同一任务。

## 4. 主键、状态与录制输入

| 数据库 key | API/业务名称 | 含义 |
| --- | --- | --- |
| `id` | `id` / 本文 `scene_id` | 平台场景主键；回调 URL 使用此值。建议作为任务关联键 |
| `operation_id` | `operationId` | 供应商生成操作 id；非空是当前队列筛选条件 |
| `provider_world_id` | `providerSceneId` | 供应商结果 id，保留数据库旧列名 |
| `convert_task_id` | `convertTaskId` / 回调 `taskId` | 转换任务追踪 id，最长 128；不是 scene id；现有注释按 Azure 消息 id 使用 |
| `convert_queued_at` | `convertQueuedAt` | 进入待转换状态的时间，旧数据可能为空 |
| `collider_mesh_url` | `colliderMeshUrl` | 配套碰撞模型输入，导入前确认实际文件格式与高斯配准 |
| `metadata.splatUrl` | 同名 | 默认优先 500k、100k、full_res、单一 spz URL；可能已替换为 Azure 副本 |
| `metadata.spzUrls` | 同名 | 供应商各精度 URL 对象；按现有键判断，不保证每档都有 |
| `pano_url` / `metadata.hdrPanoUrl` | `panoUrl` / 同名 | 全景图 / 可选 HDR 输入；可能为空 |
| `world_json_url` / `preview_video_url` | `worldJsonUrl` / `previewVideoUrl` | 转换后输出地址 |

供应商阶段：`submitting / generating / succeeded / failed`。转换阶段：

```text
pending → queued → processing → ready
                         └──→ failed
```

这是字段表达的生命周期，不代表 API 已实现全部迁移校验。`queued` 应由外部调度器维护；当前回调只接受 `processing / ready / failed`。`convert_status IS NULL` 包含预设和旧数据，不能自动当作待录制。

现有 `GET /api/scenes/[id]` 只允许场景所有者读取；它会在生成中轮询供应商，并在成功后写入 `pending` 和 `convert_queued_at`。管理员队列查询不执行这一步。无人值守生成还需确认独立服务负责刷新生成结果，不能假定用户关掉页面后自动推进。

资产注意事项：

- `marble_url` 是查看页面，不是可直接导入的 SPZ/GLB。
- Azure 容器私有，数据库存稳定地址，匿名下载可能 403。由受信任服务使用 Blob 凭据下载，或签发短期只读 SAS 给录制机；不要把账户连接串发给浏览器/最终设备。
- 供应商直链可能过期；取任务后及时下载、校验大小/格式、计算哈希。过期时由供应商集成服务刷新，不要反复重试同一个失效 URL。
- 队列接口的 `thumbnailUrl` 没有统一签 SAS；完整资产也不在该接口响应中。现有场景签名函数主要覆盖 thumbnail、pano、metadata.splatUrl，不能假设输出 JSON/视频自动获得授权访问。
- 时间列是无时区 timestamp；既有脚本专门避免用数据库 `now()` 判断超时。开发租约需统一 UTC/时区语义，不能直接把这些旧时间列当成可靠的 lease。

## 5. 转换结果回写：直接更新数据库

当前 ugasu 批量录制工具直接使用 `DATABASE_URL` 连接 Postgres，**不走 HTTP convert-callback**。
录制产物上传 Azure 确认无误后，直接 `UPDATE scenes` 表即结束，前端刷新即可看到状态变化。

回写 SQL（成功）：

```sql
UPDATE scenes
SET convert_status = 'ready',
    convert_task_id = $1,          -- batch-{sceneId}-{attempt}
    world_json_url = $2,           -- Azure 上 world_config.json 的稳定 URL
    preview_video_url = $3,        -- Azure 上主路视频 main.mp4 的稳定 URL
    convert_error = NULL,
    updated_at = NOW()
WHERE id = $4;                      -- scene_id
```

回写 SQL（失败）：

```sql
UPDATE scenes
SET convert_status = 'failed',
    convert_error = LEFT($1, 1000),
    updated_at = NOW()
WHERE id = $2;
```

字段说明：

| 字段 | 写入值 |
| --- | --- |
| `convert_status` | `ready` 或 `failed` |
| `convert_task_id` | 本次批量尝试的追踪 id，例如 `batch-33-001` |
| `world_json_url` | `https://<account>.blob.core.windows.net/scene-assets/scenes/{id}/world_config.json` |
| `preview_video_url` | `https://<account>.blob.core.windows.net/scene-assets/scenes/{id}/main.mp4`（主路） |
| `convert_error` | 失败原因，最多 1000 字符；成功时置 NULL |

> 关于双路视频：schema 只有 `preview_video_url` 一个视频字段，侧路 `right.mp4` 与主路一同上传到 `scenes/{id}/`，双路引用关系写入 `world_config.json` 内部，不单独占数据库列。

### 关于 HTTP convert-callback 端点

`POST /api/scenes/{scene_id}/convert-callback` 路由已存在（`src/app/api/scenes/[id]/convert-callback/route.ts`），作为未来多 worker / 生产部署时的标准回写通道保留。当前本机批量录制不使用它，原因：

- `src/proxy.ts` 的 Clerk 中间件未豁免该路由，无 Cookie 调用会先遇到 401
- `SCENE_CONVERT_CALLBACK_TOKEN` 未配置，路由会返回 503
- 直接连库更简单可靠，无需额外网络依赖

未来切换到 callback 时，需先在 `proxy.ts` 豁免该精确路由并配置共享 token。

## 6. ugasu 批量录制：实现现状与待办

入口为 World Studio 窗口的「8 · 批量录制」，控制器是 [LYNOOKBatchController.cs](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKBatchController.cs)。状态写入统一经过 `Transition`，各状态文案集中派生；每个自动状态有一个 enter handler，两个等待态无 handler 保持暂停。

### 已实现（2026-10-01）

1. **拉取队列（只读）**：`LYNOOKSceneService.FetchSceneQueue` 经 `Tools/scene-queue-json.mjs` 查 `convert_status = pending`，在窗口列出并勾选；不做任何认领或状态修改。
2. **下载**：`BeginDownload` 把 SPZ + GLB 下到系统临时目录 `lynook_batch/<scene_id>/`，已下载且非空时复用缓存。
3. **导入**：`BeginImport` 复用 [World Studio](lynook-world-studio.md) 的导入链路，带上数据库的 `splatSemantics`（米制缩放 / 地面归零 / 绕 X 轴 180°）。
4. **人工对齐**：导入后进入 `WaitingForAlignment` 暂停；用户调好地面/对齐后点「继续」，置 `alignmentConfirmed`。
5. **自动出生点与活动点**：`BeginPlaceSpawn` / `BeginGeneratePoints` 自动放置出生点并生成站立活动点。
6. **人工取景**：进入 `WaitingForCamera` 暂停；用户调好相机后点「继续」。
7. **录制**：进入 PlayMode 由现有 Recorder 链路产出 `main.mp4 / right.mp4 / world_config.json / mesh/collision.glb / preview.png`，回到 EditMode 后校验必要文件非空。
8. **上传与回写**：`BeginUpload` 经 `scene-upload.mjs` 上传 Azure，`BeginUpdateDB` 经 `scene-update.mjs` 直接 `UPDATE scenes` 为 `ready`（写 `world_json_url`、`preview_video_url`）。任一阶段失败都经 `FailCurrent` 记日志、回写 `failed`，并继续下一个任务。
9. **契约校验**：四个 Node 脚本在 stdout 前都用 `Tools/contracts.mjs` 按对应 JSON Schema 校验，防止 C# 模型与脚本输出漂移。

### 尚未实现 / 待办

1. **任务原子领取（最高优先）**：当前只在窗口勾选、没有租约，多个 Unity 实例会重复处理同一任务。需要单独的受信任调度服务作为唯一领取方；若沿用 Azure Queue，先取得真实队列名/endpoint、消息 schema、visibility timeout、续租、删除/重试/死信协议。
2. 若采用数据库领取，设计事务内 `FOR UPDATE SKIP LOCKED` + 条件状态更新，补 lease、owner、attempt、超时恢复及最大重试次数；只允许持有有效 lease 的 attempt 回写。现有表没有这些租约字段。
3. 自动化质量门槛：目前地面/对齐与取景靠人工确认；若要去掉人工，需先有可校验的自动对齐与取景判据，不能仅凭两份资源 URL 就承诺可交付房间。
4. worker 重启恢复：超时、下载失败、Unity 崩溃、上传成功但数据库更新失败需分别处理，防止因回写短暂失败而重复整段录制；已删除场景的行不应无限重试。

上线验收至少覆盖：只读查询、两个 worker 竞争只处理一次（依赖任务领取落地）、过期输入 URL、Unity 中断恢复、输出可授权下载和完整解码、数据库回写后前端刷新状态正确。当前批量功能经过 batchmode 静态编译验证，尚未执行真实端到端批量运行验证。

## 7. 源码定位

以 `/Users/jammie/Code/ugcplatform/` 为根：

- `scripts/show-scene-queue.mjs`：现成只读队列快照脚本。
- `src/app/api/admin/scene-queue/route.ts`、`src/lib/admin-list.ts`：筛选、响应、分页、统计。
- `src/lib/admin-auth.ts`、`src/proxy.ts`：Clerk 管理员与全站鉴权。
- `src/db/schema.ts` 的 `scenes`：实际字段/类型。
- `src/app/api/scenes/[id]/route.ts`、`src/lib/worldlabs.ts`：生成进度、pending 标记、资产字段映射。
- `src/app/api/scenes/[id]/convert-callback/route.ts`：转换状态回写。
- `src/lib/scene-storage.ts`、`src/lib/azure-vrm.ts`：Blob 路径和签名。

以本仓库 ugasu（`UnityGaussianSplatting`）为根：

- `projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKBatchController.cs`：批量状态机（`Transition` 单写入入口 + 各 enter handler）。
- `…/LYNOOKSceneService.cs`：拉队列、下载/上传、状态回写、临时目录管理。
- `…/LYNOOKNodeProcessRunner.cs`：定位并同步/异步运行 Node 脚本。
- `…/LYNOOKWorldSceneBuilder.cs` / `LYNOOKWorldInteraction.cs` / `LYNOOKFloorDetection.cs`：导入搭建、点位生成、地面识别。
- `Tools/scene-queue-json.mjs` / `scene-download.mjs` / `scene-upload.mjs` / `scene-update.mjs`：Node 侧查询与传输。
- `Tools/contracts.mjs` + `Tools/schemas/*.schema.json`：C#⇄Node 输出契约与输出前校验。

ugasu 相关说明：[本地房间制作器](lynook-world-studio.md)、[双屏录制](../projects/GaussianExample/Assets/LYNOOK/DualScreenRecorder/README.md)。
