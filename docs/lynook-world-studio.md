# LYNOOK 本地房间制作器

在 `projects/GaussianExample` 中打开 **Tools → LYNOOK → World Studio**。

后台 scene 队列、凭据配置与批量录制对接见 [Scene 生成队列与批量自动录制对接](scene-queue-integration.md)。

## 操作流程

1. 选择本地 `.ply` / `.spz` 和配套 `.glb`，填写房间名称，点击「导入并创建制作场景」。切换前 Unity 会询问如何处理当前场景的未保存修改。
2. 显示 GLB，检查它与高斯的对应关系。可调整高斯相对位置、旋转、缩放，或选中整个房间坐标节点等比缩放。只有实际检查后才勾选「已确认对齐」。
3. 在 Scene 视图点击平坦地面，设置可走范围。青色框表示候选活动范围；地面高度和尺寸使用房间局部坐标。
4. 调整 Scene 透视视角，点击「使用当前 Scene 视角」，然后刷新双屏预览。相机架保留共享视点及现有双屏投影配置；主屏取景与普通透视视图可能不同，以输出预览为准。
5. 点击地面放置出生点。绿色线框仅表示角色占用空间，不是实际角色模型，也不会录入背景。可用 Unity 的移动和旋转工具调整出生方向。
6. 生成站立活动点，或点击地面添加点。每个点可以选择、移动和删除，生成和编辑支持撤销。生成失败会保留原有点位。
7. 保存草稿，检查全部点位，录制双屏并导出。尚未设置出生地时，可以先录制 2 秒取景预览；取景预览没有交付配置，状态为 `preview_only`。

## 本地文件

每次导入创建 `Assets/LYNOOK/Worlds/world_<时间>_<随机编号>/`：

- `Source/`：源文件副本；源文件 SHA-256 写入制作草稿。
- `Gaussian/`：转换的 GaussianSplatAsset 和对应 `.bytes` 数据。
- `Room.unity`：可以继续编辑的制作场景。
- `Main.renderTexture`、`Side.renderTexture`：该房间独立的双屏输出资源。
- `world_draft.json`：版本化的制作参数快照，包括资源相对路径、相机架、地面、出生点和活动点。
- `camera_calibration.json`：双屏相机标定快照。

重新打开 `Room.unity` 可恢复编辑。当前版本以 Unity 场景作为编辑存档；JSON 是任务数据快照，还没有实现从 JSON 单独重建场景。

录制结果位于项目下 `Recordings/LYNOOK/WorldStudio/<房间编号>_<录制时间>/`：

- `main.mp4`、`right.mp4`：H.264，30 fps，默认主屏 1280×800、侧屏 720×1280。
- `world_config.json`：当前房间的出生点、活动点和实际相机投影；取景预览不输出该文件。
- `mesh/collision.glb`、`preview.png`、制作与相机快照。
- `status.json`：完整文件存在时标记 `needs_review`；只录取景时为 `preview_only`；中断或缺文件时为 `failed`。

`needs_review` 只说明必要文件已生成且非空，不代表视频内容、完整解码、硬件标定或设备播放已经通过。录制输出独立保存，重录不覆盖上一份结果。

## 当前范围和限制

- 使用现有 Unity Editor + Recorder，仍需图形环境；此版本没有接数据库、任务领取、对象存储上传或自动交付。
- 支持配套 GLB 的点击与碰撞检查，不自动重建碰撞网格、不自动估计高斯和 GLB 的配准关系。
- 第一版针对单层、近似水平的地面。生成器检查矩形边界、局部地面高度、法线、角色胶囊净空和候选点之间的直线路径。
- 不生成椅子/床等动作点，也不声称已实现导航寻路。由于现有设备端角色采用直线移动，生成器要求出生点和各活动点之间可直接通行；正式导出前重新检查手动修改的点。
- 不输出虚构的 `grid_map.json`；此流程导出的 `assets.gridMap` 为空。
- 高斯可独立校正方向，GLB 沿用 UniGLTF 导入坐标。导出的相机与点位相对于房间坐标根节点；GLB 原文件随包复制。
- 角色线框尺寸用于几何检查，实际角色大小、动作、遮挡、视频投影到 GLB 的效果以及双屏接缝仍需人工检查和设备验证。
- 中文界面使用操作系统字体（macOS Arial Unicode MS / Windows Microsoft YaHei），不打包系统字体文件。
- 首次打开会在 `Assets/LYNOOK/WorldStudio/Editor/` 缓存当前系统的字体资产和图集，供窗口跨 Play Mode 重建使用。

## 验证入口

**Tools → LYNOOK → World Studio Checks → Validate Placement and Export** 在临时附加场景中检查地面、边界、障碍阻挡、点位生成、无效参数、失败保留点位，以及带平移/旋转/缩放坐标根的导出。检查结束恢复原活动场景。

该检查用占位视频文件验证配置发布逻辑，不验证编码；实际编码使用「录制 2 秒取景预览」检查。

**Inspect Imported Room** 检查当前导入房间的高斯资源与渲染准备状态，并在制作目录生成 `main_preview.png`。

## 本次验证（2026-09-13）

- Unity 6000.3.16f1 编辑器编译与上述几何/导出检查通过。
- 实际导入 Cozy Office SPZ（1,920,000 splats）及配套 GLB；源文件副本的 SHA-256 与草稿记录一致。
- 样例保存为 `Assets/LYNOOK/Worlds/world_20260913_145109_dbc0e3/Room.unity`。保留未确认对齐、未放置出生地的状态，供继续人工制作。
- 两秒双屏预览输出为 H.264；主屏 1280×800、侧屏 720×1280，均为 30 fps、60 帧，完整解码检查通过。
- 字体持久化修复后再次运行短录制，正常退出，新的日志段没有异常。
- 本次没有把样例调成正式交付房间，也没有验证真实角色动作、设备播放或物理双屏效果。取景预览输出明确标记为不可交付。

## 已知问题与修复记录

### 2026-09-19：导入后 GaussianSplatRenderer 不显示，需手动重开场景或点 Render Mode 才能加载

#### 现象

`LYNOOKWorldStudioService.Import` 一键导入完成后，Inspector 中 `GaussianSplatRenderer` 显示橙色警告 `Gaussian Splat asset is not assigned or is empty`，Scene 视图不渲染。但磁盘上 `room.asset` 的 YAML 引用、5 个 `.bytes` 子资产的 `.meta` GUID 全部对得上，`m_SplatCount: 1920000` 等字段也正确序列化。

绕过手段（用户最初发现）：
- 手动重新打开 `Room.unity` 场景 → 警告消失、正常渲染。
- 不重开场景，在 Inspector 里随便切换一下 Render Mode 下拉框 → 也能恢复。

#### 根因

`GaussianSplatAssetCreator.ImportFile` 在 `CreateAsset` 内部 `Refresh(ForceUncompressedImport)` 之后调 `LoadAssetAtPath<GaussianSplatAsset>` 返回的资产实例存在 Unity 经典的 **fake-null** 状态：

- C# 对象本身非 null，所有序列化字段（`m_SplatCount`、`m_PosData` 等）都已正确加载；
- 但底层 Unity native asset 还在异步导入队列中没真正就绪，所以 `UnityEngine.Object` 重载的 `==` 运算符把 `asset != null` 判定为 `false`。

`GaussianSplatRenderer.HasValidAsset` 第一项就是 `m_Asset != null`（Unity 重载 `==`），fake-null 直接让整个校验失败；`OnEnable → CreateResourcesForAsset` 也因 `!HasValidAsset` 提前 return，GPU 缓冲不建。Unity 下一帧 `Update` 检测到 `m_PrevAsset != m_Asset`，但因为 `HasValidAsset` 仍为 false，`CreateResourcesForAsset` 还是早退；同时 `m_PrevAsset` 被赋值为 fake-null 的 m_Asset，之后 `Update` 不再触发重建。组件就此卡在无效状态。

用户手动重开场景或点 Inspector 任意字段时，Unity 内部对引用做了一次重新解析，native side 才被加载，`!=` 变为 true，`HasValidAsset` 通过，渲染恢复。

#### 修复

[LYNOOKWorldStudioService.cs](file:///Users/jammie/UnityGaussianSplatting-main/projects/GaussianExample/Assets/Scripts/LYNOOK/Editor/LYNOOKWorldStudioService.cs) 的 `Import` 方法在 `SaveScene` 之后加了 fake-null 修复块：

1. `AssetDatabase.Refresh(ForceSynchronousImport)`：强制 Unity 把所有 pending import（包括 `SaveScene` 刚触发的 `Room.unity` 导入）跑完。
2. `AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath)`：重新拿一个 native side 真正加载好的实例。
3. 校验新实例的 `posData/otherData/colorData/shData` 都非 null 后，重新赋值给 `gaussian.m_Asset`，替换之前的 fake-null 实例。
4. `m_PrevAsset` 仍是 null，下一帧 `Update` 检测到 `m_PrevAsset != m_Asset` 触发 `DisposeResourcesForAsset + CreateResourcesForAsset`，自动重建 GPU 缓冲。

修复块放在 `SaveScene` 之后是关键：`SaveScene` 触发的 `Room.unity` 导入也会进 AssetDatabase 队列，必须等它一起完成才能保证 native side 一致。在 `SaveScene` 之前 Refresh 不顶用——后面 `SaveScene` 还会再触发一次导入把队列搞乱。

#### 修复前后的 Debug.Log 对比

修复前（同一次 Import 调用）：

| 阶段 | `m_Asset != null`（Unity `==`） | `ReferenceEquals(m_Asset, null)` | `HasValidAsset` |
|---|---|---|---|
| 赋值前 asset 状态 | false（fake-null）| false（C# 对象在）| — |
| 赋值后立刻 | false | false | false |
| `SetActive(true)` 后 | false | false | false |
| `SaveScene` 后 | false | false | false |

修复后追加的日志：

| 阶段 | `reloadedAsset != null`（Unity `==`）| `HasValidAsset`（重新赋值后）|
|---|---|---|
| `Refresh + LoadAssetAtPath` 后 | **true** | — |
| 重新赋值给 renderer 后 | **true** | **true** |

`ReferenceEquals(m_Asset, null) = false` 这条日志最初让我们定位到 fake-null：C# 引用在、Unity native side 不在。这是 Unity 经典坑，`asset?.field` 用 C# 默认 `==` 检查能取到字段值（`splatCount=1920000`、`posData=room_pos`），但 `asset != null` 用重载 `==` 直接判 false。

#### 兜底

如果极少数情况下 `Refresh + LoadAssetAtPath` 之后仍 fake-null，会输出 `Debug.LogWarning` 提示。用户在 Inspector 中切换 Render Mode 下拉框可触发 Unity 重新解析引用，等同于本路径的兜底手动版。

#### 同期相关修复

- `LYNOOKWorldStudioService.Import` 给 `Gaussian Visual` 加 `localScale = (1, 1, -1)`：SPZ/PLY 源数据朝 -Z，Unity 默认朝 +Z，沿 Z 轴 scale -1 翻回正确朝向，与原版 sample 场景里手动给 `GaussianSplatRenderer` 加的纠正变换保持一致。
- `FindCamerasJson` 改为向上递归查找 `cameras.json`，行为与 `GaussianSplatAssetCreator.LoadJsonCamerasFile` 一致，支持 INRIA paper 数据集 `point_cloud/iteration_*/cameras.json` 在父目录的布局。
- `manifest.json` 把 UniVRM 三件套（`com.vrmc.gltf` / `com.vrmc.univrm` / `com.vrmc.vrm`）从 v0.131.0 `/Packages/UniGLTF` 单包结构降级为 v0.130.x `/Assets/...` 三包结构，与 lumomobile 项目对齐。原因是 v0.131.0 的 `/Packages` 结构只含 ScriptedImporter，缺少 v0.130.x 包同时提供的 AssetPostprocessor 兜底路径。
