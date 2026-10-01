# LYNOOK 本地房间制作器

在 `projects/GaussianExample` 中打开 **Tools → LYNOOK → World Studio**。

后台 scene 队列、凭据配置与批量录制对接见 [Scene 生成队列与批量自动录制对接](scene-queue-integration.md)。

## 操作流程

窗口按编号分节：`1 · 本地文件`、`2 · 对齐和地面`、`3 · 相机取景`、`4 · 出生地`、`5 · 活动点`、`6 · 保存和录制`，以及 `7 · 发布到部署工程`、`8 · 批量录制` 两个折叠区。

1. 在「1 · 本地文件」选择本地 `.ply` / `.spz` 和配套 `.glb`，填写房间名称、选择房间类型，点击「导入并创建制作场景」。切换前 Unity 会询问如何处理当前场景的未保存修改。
2. 显示 GLB，检查它与高斯的对应关系。可调整高斯相对位置、旋转、缩放，或选中整个房间坐标节点等比缩放。只有实际检查后才勾选「已确认对齐」。
3. 在 Scene 视图点击平坦地面，设置可走范围。青色填充表示可走范围；地面高度和尺寸使用房间局部坐标。
4. 调整 Scene 透视视角，点击「使用当前 Scene 视角」，然后刷新双屏预览。相机架保留共享视点及现有双屏投影配置；主屏取景与普通透视视图可能不同，以输出预览为准。
5. 点击地面放置出生点。绿色线框仅表示角色占用空间，不是实际角色模型，也不会录入背景。可用 Unity 的移动和旋转工具调整出生方向。
6. 生成站立活动点，或点击地面添加点。每个点可以选择类型、移动和删除，生成和编辑支持撤销。生成失败会保留原有点位。
7. 保存草稿，检查全部点位，录制双屏并导出。尚未设置出生地时，可以先录制 2 秒取景预览；取景预览没有交付配置，状态为 `preview_only`。
8. 需要迁移到另一个 Unity 工程时，在「7 · 发布到部署工程」填写目标目录并一键发布；批量处理多个场景用「8 · 批量录制」。

## 本地文件

每次导入创建 `Assets/LYNOOK/Worlds/world_<时间>_<随机编号>/`：

- `Source/`：源文件副本（`room.spz` / `room.ply`、`collision.glb`，可能还有 `cameras.json`）；源文件 SHA-256 写入制作草稿。
- `Gaussian/`：转换的 GaussianSplatAsset 和对应 `.bytes` 数据。
- `Room.unity`：可以继续编辑的制作场景。
- `Main.renderTexture`、`Side.renderTexture`：该房间独立的双屏输出资源。
- `world_draft.json`：版本化（`schemaVersion`）的制作参数快照，包括资源相对路径、相机架、地面、出生点、活动点和部署目标路径。
- `camera_calibration.json`：双屏相机标定快照。

重新打开 `Room.unity` 可恢复编辑。当前版本以 Unity 场景作为编辑存档；JSON 是任务数据快照，还没有实现从 JSON 单独重建场景。

录制结果位于项目下 `Recordings/LYNOOK/WorldStudio/<房间编号>_<录制时间>/`：

- 交付录制：录制器先产出中间 MP4，再由 ffmpeg 做 H.264 流拷贝 remux 为 `main.mov` / `right.mov`（30 fps，主屏 1280×800、侧屏 720×1280），随后删除中间 MP4。`world_config.json` 中两路视频均引用 `.mov`。
- `world_config.json`：当前房间的出生点、活动点和实际相机投影；取景预览不输出该文件。
- `mesh/collision.glb`、首帧预览 `preview.png`（正面）与 `preview_right.png`（侧面），制作与相机快照。
- 本地交付另生成双屏拼接 `preview.mov`；**批量录制不生成拼接 MOV**。
- `status.json`：完整交付文件存在时标记 `needs_review`；只录 2 秒取景预览（产物为 `main.mp4 / right.mp4`，不 remux、不删 mp4）时为 `preview_only`；中断或缺文件时为 `failed`。各状态均带 `source`（batch 时带 `batchSceneId`）。

`needs_review` 只说明必要文件已生成且非空，不代表视频内容、完整解码、硬件标定或设备播放已经通过。录制输出独立保存，重录不覆盖上一份结果。

`Assets/LYNOOK/Worlds/` 与 `Recordings/` 都是生成产物，已在 `.gitignore` 中忽略，不入库。

## 代码结构与程序集

World Studio 的 C# 代码在 `Assets/Scripts/LYNOOK/`，拆为两个显式程序集：

- **LYNOOK.Runtime**（[LYNOOK.Runtime.asmdef](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/LYNOOK.Runtime.asmdef)，引用 `GaussianSplatting`、`Unity.Recorder.Editor`）：会被序列化进场景/预制件的组件、录制会话与共享常量。
  - 组件：`LYNOOKWorldAuthoring`（房间制作数据与 Gizmos）、`LYNOOKActivityPoint`、`LYNOOKDualCameraRig`、`LYNOOKWorldExportSettings`、`CameraRigTransformCopy`，以及各录制测试场景的 Motion 脚本。
  - 录制会话：`LYNOOKDualRecordingSession` / `LYNOOKCornerBoxRecordingSession`（挂在场景相机 Rig 上，PlayMode 下驱动 Unity Recorder）与 `LYNOOKRecordingWorldExporter`。它们对 `UnityEditor.Recorder` 的引用全部在 `#if UNITY_EDITOR` 内——**这些类不能放进 Editor-only 程序集**：Unity 禁止把 editor asmdef 里的 MonoBehaviour `AddComponent`/序列化到场景，否则 AddComponent 静默返回 null 并报 `Can't add script behaviour ... because it is an editor script`。
  - 常量：`LynookRoomTypes`、`LynookActivityTypes`、`LynookConvertStatuses`，提供 `All / IsValid / Normalize`，是 UI 下拉枚举与默认值的唯一来源。
- **LYNOOK.Editor**（[LYNOOK.Editor.asmdef](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOK.Editor.asmdef)，引用 Runtime、`GaussianSplattingEditor`、`Unity.Recorder.Editor`、`Unity.Timeline`）：编辑器窗口、导入/录制、以及按职责划分的静态服务。
  - [LYNOOKWorldStudioService.cs](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKWorldStudioService.cs)：薄外观，仅保留 `WorkspaceRoot` / `BatchTempRoot` 常量、`Current` 属性和全部数据传输对象（DTO）。
  - `LYNOOKWorldSceneBuilder`：资产导入与场景搭建。
  - `LYNOOKFloorDetection`：地面识别、多边形提取与简化算法。
  - `LYNOOKWorldInteraction`：射线、可站立判定、出生点与活动点生成。
  - `LYNOOKWorldPersistence`：参数校验、草稿保存、发布到部署工程。
  - `LYNOOKNodeProcessRunner`：定位并同步/异步运行 Node 脚本。
  - `LYNOOKSceneService`：场景队列拉取、资产下载/上传、转换状态回写、临时目录管理。

Node 侧工具在仓库 `Tools/`：`scene-queue-json.mjs`、`scene-download.mjs`、`scene-upload.mjs`、`scene-update.mjs`，以及统一校验入口 [contracts.mjs](file:///Users/jammie/UnityGaussianSplatting-main/Tools/contracts.mjs) 和 `schemas/*.schema.json`。

## 发布到部署工程

在「7 · 发布到部署工程」填写**另一个 Unity 工程内已存在目录的绝对路径**（如 `…/DeployProject/Assets/Worlds`），点击发布：

- 校验路径为绝对路径、目录存在，且不能位于当前工作区内部（防止自拷贝）。
- 复制整个房间工作区到 `<目标路径>/<房间编号>/`，并附带最近一次录制产物（双屏视频、`world_config.json` 等）。
- 目标已存在同名房间时会先要求确认覆盖。目标路径会保存在 `world_draft.json` 的 `deployTargetPath`。

## 当前范围和限制

- 使用现有 Unity Editor + Recorder，仍需图形环境。本地流程之外，已实现批量录制：拉取队列、下载、导入、自动出生点/活动点、上传 Azure、回写数据库；其中**地面/对齐与相机取景为人工暂停确认**，尚未全自动。
- 支持配套 GLB 的点击与碰撞检查，不自动重建碰撞网格、不自动估计高斯和 GLB 的配准关系。
- 第一版针对单层、近似水平的地面。生成器检查边界、局部地面高度、法线与角色胶囊净空；活动点也只校验地面与净空。
- 不生成椅子/床等动作点。**不要求出生点与各活动点之间直线路径无遮挡**：点可位于墙后/需绕行处，正式导出前重新检查手动修改点的地面与净空；点间通行统一交给设备端运行时寻路算法兜底，编辑器侧不做可达性判定。
- 不输出虚构的 `grid_map.json`；本地流程导出的 `assets.gridMap` 为空。
- 高斯可独立校正方向，GLB 沿用 UniGLTF 导入坐标。导出的相机与点位相对于房间坐标根节点；GLB 原文件随包复制。
- 角色线框尺寸用于几何检查，实际角色大小、动作、遮挡、视频投影到 GLB 的效果以及双屏接缝仍需人工检查和设备验证。
- 中文界面使用操作系统字体（macOS Arial Unicode MS / Windows Microsoft YaHei），不打包系统字体文件。
- 首次打开会在 `Assets/LYNOOK/WorldStudio/Editor/` 缓存当前系统的字体资产和图集，供窗口跨 Play Mode 重建使用。

## 验证入口

**Tools → LYNOOK → World Studio Checks → Validate Placement and Export** 在临时空场景中检查地面、边界、障碍阻挡、点位生成、无效参数、失败保留点位，以及带平移/旋转/缩放坐标根的导出。检查结束恢复原活动场景。

该检查用占位视频文件验证配置发布逻辑，不验证编码；实际编码使用「录制 2 秒取景预览」检查。

**Inspect Imported Room** 检查当前导入房间的高斯资源与渲染准备状态，并在制作目录生成 `main_preview.png`。

另有 **Tools → LYNOOK → Validation → Validate Recording World Export**，批量回归检查四个房间场景的 `world_config.json` 导出（夹具只写在系统临时目录）。

## 验证记录

### 2026-10-01：架构重构后编译验证

- Unity 6000.3.16f1 以 batchmode 编译，0 个编译错误。
- 范围：程序集拆分（Runtime / Editor）、God class 拆为六个职责服务并保留薄外观、共享常量收敛、C#⇄Node 契约（JSON Schema + 输出前校验）、批量状态机收敛为单一 `Transition` 写入入口。
- 本轮为静态编译验证，未在编辑器内执行实际导入或批量录制冒烟；视频编码、设备播放与物理双屏效果仍按既有约定需人工/设备验证。

### 2026-09-13：早期功能验证（历史记录）

- Unity 6000.3.16f1 编辑器编译与当时的几何/导出检查通过。
- 实际导入 Cozy Office SPZ（1,920,000 splats）及配套 GLB；源文件副本的 SHA-256 与草稿记录一致。
- 两秒双屏预览输出为 H.264；主屏 1280×800、侧屏 720×1280，均为 30 fps、60 帧，完整解码检查通过。
- 当时未把样例调成正式交付房间，也未验证真实角色动作、设备播放或物理双屏效果。

## 已知问题与修复记录

### 2026-09-19：导入后 GaussianSplatRenderer 不显示，需手动重开场景或点 Render Mode 才能加载

#### 现象

导入一键完成后，Inspector 中 `GaussianSplatRenderer` 显示橙色警告 `Gaussian Splat asset is not assigned or is empty`，Scene 视图不渲染。但磁盘上 `room.asset` 的 YAML 引用、5 个 `.bytes` 子资产的 `.meta` GUID 全部对得上，`m_SplatCount: 1920000` 等字段也正确序列化。

绕过手段（用户最初发现）：
- 手动重新打开 `Room.unity` 场景 → 警告消失、正常渲染。
- 不重开场景，在 Inspector 里随便切换一下 Render Mode 下拉框 → 也能恢复。

#### 根因

`GaussianSplatAssetCreator.ImportFile` 在 `CreateAsset` 内部 Refresh 之后调 `LoadAssetAtPath<GaussianSplatAsset>` 返回的资产实例存在 Unity 经典的 **fake-null** 状态：

- C# 对象本身非 null，所有序列化字段（`m_SplatCount`、`m_PosData` 等）都已正确加载；
- 但底层 Unity native asset 还在异步导入队列中没真正就绪，所以 `UnityEngine.Object` 重载的 `==` 运算符把 `asset != null` 判定为 `false`。

`GaussianSplatRenderer.HasValidAsset` 第一项就是 `m_Asset != null`（Unity 重载 `==`），fake-null 直接让整个校验失败；`OnEnable → CreateResourcesForAsset` 也因 `!HasValidAsset` 提前 return，GPU 缓冲不建。用户手动重开场景或点 Inspector 任意字段时，Unity 内部对引用做了一次重新解析，native side 才被加载，渲染恢复。

#### 修复（2026-10-01 加固：资产 ready 前不激活渲染器）

早期方案是「先激活、SaveScene 后再补救赋值」，但批量录制中仍偶现渲染器带病继续、后续步骤空引用。当前 [LYNOOKWorldSceneBuilder.cs](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKWorldSceneBuilder.cs) 的 `Import` 严格按加载顺序执行：

1. 创建 `Gaussian Visual` 后立刻 `SetActive(false)`，在 inactive 状态下 `AddComponent<GaussianSplatRenderer>` 并赋好 `m_Asset` 与全部 shader/compute shader 引用——此时不触发 `OnEnable/Update`。
2. 场景其余部分（GLB 碰撞、相机 Rig、地面识别、取景）照常搭建，第一次 `SaveScene`（物体仍 inactive，`m_Asset` 的 GUID/fileID 引用已写入场景）。
3. 有界同步重试（最多 4 轮）：每轮 `Refresh(ForceSynchronousImport)` → 重新导入 5 个 `.bytes` 子资产 → 重新导入主 `.asset` → `LoadAssetAtPath` 重新加载并赋给 renderer，以 **`gaussian.HasValidAsset`**（非 fake-null + `splatCount>0` + 版本匹配 + pos/other/color/sh 全在）作为唯一就绪判据。
4. 就绪后才 `splatObject.SetActive(true)`：`OnEnable` 同步执行，`EnsureMaterials / 注册渲染系统 / CreateResourcesForAsset` 一次成功；紧接着校验 `HasValidRenderSetup`（GPU 缓冲已建立），失败直接抛错中止导入，批量任务会被记为失败而不是带病继续。
5. 第二次 `SaveScene` 把 active 状态持久化进 `Room.unity`（第一次保存时物体还是 inactive，否则重开场景高斯不显示）。
6. 二次保存后若 `m_Asset` 再次退回 fake-null，重新 `Refresh + LoadAssetAtPath` 赋值；渲染器已激活，下一帧 `Update` 走 `m_PrevAsset != m_Asset` 热切换路径自动重建 GPU 缓冲。

重试循环放在 `SaveScene` 之后是关键：`SaveScene` 触发的 `Room.unity` 导入也会进 AssetDatabase 队列，必须等它一起完成才能保证 native side 一致。

定位线索：`ReferenceEquals(m_Asset, null) = false` 但 Unity 重载 `m_Asset != null = false`，说明 C# 引用在、Unity native side 不在——这是 Unity 经典 fake-null。

#### 兜底

4 轮同步刷新后仍不就绪时直接抛 `InvalidOperationException` 中止本次导入（批量录制中任务记为 Failed 并完整记录堆栈），不再让渲染器带病进入后续地面/录制步骤。若在二次保存之后才出现 fake-null，会输出 `Debug.LogWarning` 并尝试热切换赋值；用户在 Inspector 中切换 Render Mode 下拉框也可触发 Unity 重新解析引用，作为手动兜底。

### 2026-10-01：重构后批量导入空引用——录制会话类被误移入 Editor-only 程序集

#### 现象

批量录制 `[2/8] 导入房间` 抛 `NullReferenceException`，定位在 `session.SetReferences(rig, null)`。表面与高斯 fake-null 同类，但异常点在相机 Rig 搭建阶段（早于 SaveScene 与高斯就绪循环），与资产加载无关。

#### 根因

架构重构把 `LYNOOKDualRecordingSession.cs` / `LYNOOKCornerBoxRecordingSession.cs` / `LYNOOKRecordingWorldExporter.cs` git mv 进了 `Editor/`（LYNOOK.Editor，`includePlatforms: [Editor]`）。Unity **禁止把 Editor-only 程序集中的 MonoBehaviour 挂到场景物体**：`AddComponent<T>()` 不抛异常、静默返回 null，Console 另有一条 `Can't add script behaviour 'LYNOOKDualRecordingSession' because it is an editor script. To attach a script it needs to be outside the 'Editor' folder.`，下一行对返回值调方法即空引用。

#### 修复

- 三个文件 git mv 回 [Scripts/LYNOOK/](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK)（`.meta` 一并移动，GUID 不变，旧场景/预制件序列化引用不断）。
- [LYNOOK.Runtime.asmdef](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/LYNOOK.Runtime.asmdef) 增加 `Unity.Recorder.Editor` 引用；三个类对 Recorder 的使用全部在 `#if UNITY_EDITOR` 块内，Player 构建时这些代码被剔除，editor 程序集引用同步移除。
- `Import` 对 `rig` 与 `session` 加显式空值校验，再次发生时给出可定位原因的错误信息而非裸空引用。

规则：**凡要序列化进场景/预制件、或运行时被 AddComponent 的 MonoBehaviour，必须位于非 Editor-only 程序集**；编辑器专属逻辑通过 `#if UNITY_EDITOR` 隔离。

### 2026-10-01：批量运行期间收敛录制入口，并在 JSON 标记来源

#### 背景

批量录制停在 `[6/8] 等待相机` 时，若不点「继续批量录制」、而去点「6 · 保存和录制」里的独立录制按钮，录制虽成功，但批量任务仍停在 WaitingForCamera；批量控制器回编辑模式的上传回调要求 `task.state == Recording` 才接管（[LYNOOKBatchController.cs](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKBatchController.cs)），导致产物不上传、数据库不更新。

#### 修复

1. **入口收敛**：`Rebuild` 以统一上下文开关 `batchActive = LYNOOKBatchController.IsRunning` 为准，批量进行期间用 `DisableForBatch` 置灰「录制 2 秒取景预览」和「录制双屏并导出本地房间包」两个按钮，tooltip 指明改走「8 · 批量录制」的「继续批量录制」。`SetEnabled(false)` 在 UIElements 层即拦截点击，不靠各回调自行判断。
2. **JSON 体现来源**：录制来源经 `LYNOOKWorldStudioRecording.StartBatch / Start / StartPreview` → SessionState（Pending→Active）→ `LYNOOKDualRecordingSession.BeginRecording` → `LYNOOKRecordingWorldExporter.Capture` 单链路透传，无旁路：
   - `world_config.json` 的 `recording` 段新增 `"source": "batch" | "manual"`，batch 时再带 `"batchSceneId": <Postgres scenes.id>`；
   - `status.json` 在 recording / needs_review / failed 各状态同样带 `source`（batch 时带 `batchSceneId`）。
3. `Capture` 校验前置于 `recorderController.StartRecording()`，world 导出不合法时不会先开始录制。

#### 同期相关修复

- 导入给 `Gaussian Visual` 加 `localScale = (1, 1, -1)`：SPZ/PLY 源数据朝 -Z，Unity 默认朝 +Z，沿 Z 轴 scale -1 翻回正确朝向。
- `FindCamerasJson` 改为向上递归查找 `cameras.json`，行为与 `GaussianSplatAssetCreator.LoadJsonCamerasFile` 一致，支持 INRIA paper 数据集 `point_cloud/iteration_*/cameras.json` 在父目录的布局。
- `manifest.json` 把 UniVRM 三件套（`com.vrmc.gltf` / `com.vrmc.univrm` / `com.vrm.vrm`）配置为 v0.130.x `/Assets/...` 三包结构，与 lumomobile 项目对齐。
