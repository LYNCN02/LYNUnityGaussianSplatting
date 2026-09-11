# LYNOOK 双屏 Off-Axis 连续场景录制系统

所有房间场景统一放在 `Assets/LYNOOK/Scenes`，请先看[场景用途与命名说明](../Scenes/README.md)。Unity 入口：`Tools > LYNOOK > Room Scenes`。

## 结论与标定边界

本目录已经从“两个同眼位对称透视相机 + 50° FOV + 51.4° yaw”改为共享眼位的物理屏幕 Off-Axis Projection。两台相机仍在同一观察点，但各自朝向与真实屏幕平面法线一致，投影偏移由屏幕四角生成的 `Matrix4x4.Frustum(left, right, bottom, top, near, far)` 表达，不再用 Side Camera 平移或反复微调 yaw 凑接缝。

屏幕尺寸已按需求写入可编辑标定项：

| 可视区域 | 宽 | 高 | 输出 |
|---|---:|---:|---:|
| Main（左，横屏） | 172.22 mm | 107.64 mm | 1280×800 |
| Right（右，竖屏） | 62.1 mm | 110.4 mm | 720×1280 |

两屏顶部对齐；Side 比 Main 高出的 `110.4 - 107.64 = 2.76 mm` 全部保留在底部。

当前硬件真实夹角、边框/接缝距离、目标观察眼位和两块屏幕的实测三维位姿仍未提供。Prefab 中 `Calibration Values Confirmed` 因此保持关闭。当前 `51.4° / 0 mm / Local Eye (0,0,0)` 只是把旧录制构图移入完整非对称系统的临时基线，不是产品最终标定值，也不能据此声明实体屏已经通过。

## 可编辑标定项

选择 `Prefabs/LYNOOK_DualCameraRecorder.prefab` 根节点上的 `LYNOOKDualCameraRig`：

- Main / Side 可视区域宽高（mm）
- `Target Eye Local Mm`：两台相机唯一共享的目标眼位 XYZ
- `Main Screen Center Local Mm` 与 Main 屏幕局部 Euler
- `Side Screen Center Local Mm`（关闭自动铰链推导后可直接使用）
- `Screen Angle Degrees`：两屏法线夹角
- `Seam Gap Mm`：Main 可视右边缘到 Side 可视左边缘、沿 Side 平面的间距
- Side 屏幕 Euler 微调
- `Near Clip Meters / Far Clip Meters`
- `Calibration Values Confirmed`：只有完成实体测量后才应开启

默认开启 `Derive Side Position From Hinge`：Side 左上角从 Main 右上角开始，先沿 Side 平面留出 `Seam Gap Mm`，再由 Side 尺寸求中心，所以顶部始终对齐，Side 的 2.76 mm 高度差只会向下扩展。

## 投影计算

每块屏幕先得到世界空间中心、Right、Up、Forward 和四角：

```text
bottomLeft = center - right * width/2 - up * height/2
topRight   = center + right * width/2 + up * height/2
```

Camera 固定到共享眼位，Rotation 使用 `Quaternion.LookRotation(screen.forward, screen.up)`。把四角转换到该 Camera 的局部坐标后：

```text
left   = bottomLeft.x * near / bottomLeft.z
right  = topRight.x   * near / topRight.z
bottom = bottomLeft.y * near / bottomLeft.z
top    = topRight.y   * near / topRight.z

camera.projectionMatrix = Matrix4x4.Frustum(left, right, bottom, top, near, far)
```

本次实际录制采用的临时值：

- 共享眼位（World）：`(0.11096176, 1.479076, -2.8465776)` m
- CaptureRig 旋转：Quaternion `(0.032035094, -0.06839471, -0.002197312, 0.9971415)`，约 Euler `(3.646°, -7.864°, -0.503°)`
- Main 屏幕中心（CaptureRig Local）：`(0, 0, 115.42)` mm
- Side 屏幕中心（由铰链推导）：约 `(105.4815, -1.3800, 91.1538)` mm
- 临时屏幕夹角：`51.4°`（未实测）
- 临时接缝间距：`0 mm`（未实测）
- near / far：`0.03 / 1000` m

对应 near-plane 范围：

```text
Main  L=-0.02238174  R= 0.02238174  B=-0.01398891  T= 0.01398891
Right L=-0.00785632  R= 0.00551722  B=-0.01218478  T= 0.01159040
```

Right 的 `L/R` 与 `B/T` 都不是中心对称；其投影矩阵偏移项约为 `m02=-0.174906 / m12=-0.025000`。这正是屏幕横向偏心与顶部对齐、底部多 2.76 mm 的投影表达。

## Gaussian Splatting 实际矩阵链路

当前项目是 Built-in Render Pipeline。源码检查到的链路是：

```text
Camera.onPreCull
  -> GaussianSplatRenderSystem.OnPreCullCamera
  -> SortAndRenderSplats(camera, commandBuffer)
  -> GaussianSplatRenderer.CalcViewData(commandBuffer, camera)
  -> SplatUtilities.compute / CSCalcViewData
```

- C# 侧 View 使用 `camera.worldToCameraMatrix`。
- Gaussian 中心位置使用当前 Camera 的 `UNITY_MATRIX_VP`。
- Gaussian 2D covariance 使用同一 Camera 的 `UNITY_MATRIX_P`。
- covariance 的稳定边界已改为读取非对称偏移 `m02/m12`，并分别使用 X/Y 像素焦距，避免顶部、底部或偏心边缘的 splat 被按对称视锥错误夹紧。
- Gaussian 代码没有从 `Camera.fieldOfView` 另建一套透视矩阵。
- 因此写入 `Camera.projectionMatrix` 的自定义 Frustum 会进入 Gaussian 的中心投影和 covariance 计算；本次实际录制也已用这套相机输出 Gaussian 场景。

## Recording interaction

Use `Tools > LYNOOK > Record` for all four modes, or `Record Current Scene` for the
open room scene. The command opens the existing scene, records, then stops
automatically. Pressing Play alone only previews; it does not start recording.
Each new take is saved to `Recordings/LYNOOK/<scene-name>/<yyyyMMdd_HHmmss_fff>/`.
Output paths listed below describe the earlier baseline recordings; new takes use
the same filenames within their own folders. See the [scene guide](../Scenes/README.md).

## 同步录制与输出

一个 `RecorderController` 同时拥有 Main 和 Right 两个 `MovieRecorderSettings`，只调用一次共同的 `PrepareRecording()` 与 `StartRecording()`：

- 固定 `30 fps`
- 帧区间 `0–299`
- 共 `300` 帧 / `10 s`
- 两路无音频
- 同一 Unity Player Loop、同一 Timeline 时间源、同一起止帧

为保护旧文件，不再写 `main.mov / right.mov`。新输出：

```text
Recordings/LYNOOK/main_offaxis.mp4
Recordings/LYNOOK/main_offaxis.mov
Recordings/LYNOOK/right_offaxis.mp4
Recordings/LYNOOK/right_offaxis.mov
Recordings/LYNOOK/offaxis_physical_preview.mov
```

MOV 是对 Recorder H.264 MP4 的 stream-copy 重封装，不重新编码。物理比例预览必须缩放，所以单独重新编码：Main 缩放为 `1996×1248`，下方补 `32 px` 黑色；Right 保持 `720×1280`；顶部对齐后横向拼成 `2716×1280`。

## 跨接缝测试场景

`Assets/LYNOOK/Scenes/01_DualScreen_SeamCalibration.unity` 包含真实 3D 内容：

- 靠近接缝顶部、中部、底部的三条横线
- 沿完整接缝高度的竖线
- 正反两条跨屏斜线
- 接缝处 Near / Mid / Far 三个不同深度物体
- 10 秒内跨过接缝的运动球体
- 接缝附近粒子
- 原 Gaussian 场景

这些不是屏幕空间 UI 或拼接后画线。测试几何和两路 Camera 在同一 3D 世界中渲染。

Editor 菜单：

```text
Tools/LYNOOK/Room Scenes/01 Dual Screen - Seam Calibration/Rebuild Scene and Recorder Assets
Tools/LYNOOK/Room Scenes/Validate Current Dual Screen Rig
Tools/LYNOOK/Record/01 Dual Screen - Seam Calibration
```

## 2026-08-19 当前验证结果

| 层级 | 结果 | 边界 |
|---|---|---|
| 源码与资产 | 通过 | Unity C# 编译无错误；Prefab、RT、Recorder Settings、测试场景已重建 |
| Off-Axis 参数校验 | 通过（临时标定） | 同眼位、四角 Frustum、顶部对齐、Side 底部多 2.76 mm；硬件数据未确认 |
| Editor Play Mode | 通过 | 实际进入 Play Mode，单 Controller 完成 300 帧并自动退出 |
| 实际双路录制 | 通过 | 两路均为 H.264、10.000 s、30 fps、300 帧；Main 1280×800，Right 720×1280 |
| 时间同步 | 通过 | 两路全部 300 个 frame PTS 逐项相同 |
| MOV 重封装 | 通过 | MP4 与对应 MOV 的 H.264 stream SHA-256 相同 |
| 物理比例预览 | 通过 | `2716×1280 / 30 fps / 300 帧 / 10 s` |
| 抽帧视觉检查 | 已执行 | 检查帧 0 / 150 / 299；跨缝斜线、横线在未被深度物遮挡处对齐，运动物在同一帧位置一致 |
| 300 帧逐像素接缝算法检查 | 未执行 | 已核验所有 PTS；未建立逐像素特征跟踪判定器 |
| 实体双屏 | 未验证 | 仍需实测夹角、边框间距、屏幕三维位姿和目标眼位后在 LYNOOK 真机验收 |

旧 `main.mov / right.mov` 的修改时间保持不变，本次流程没有覆盖它们。

## 独立 3D 视角录制测试 Scene

`Assets/LYNOOK/Scenes/02_DualScreen_DepthTest.unity` 与接缝校准 Scene 分开，用来评估双屏显示真实 3D 空间时的纵深、透视和跨屏运动：

- 近 / 中 / 远三层透视框与深度物体
- 沿视线收敛的地面网格和接缝深度轴
- 从 Main 近景经过接缝、再进入 Right 远景的 10 秒运动球
- 中景旋转参照物，用于观察两路画面是否来自同一个世界状态
- 继续使用同一 `LYNOOKDualCameraRig` 共享眼位和物理屏幕 Off-Axis Frustum

该 Scene 使用独立文件名，不覆盖接缝校准输出：

```text
Recordings/LYNOOK/main_3dview.mp4
Recordings/LYNOOK/main_3dview.mov
Recordings/LYNOOK/right_3dview.mp4
Recordings/LYNOOK/right_3dview.mov
Recordings/LYNOOK/3dview_physical_preview.mov
```

Editor 菜单：

```text
Tools/LYNOOK/Room Scenes/02 Dual Screen - Depth Test/Create or Open Scene
Tools/LYNOOK/Record/02 Dual Screen - Depth Test
```

当前 Scene 是通用 3D 观感基线。真实观看距离、是否需要眼位/相机移动、最终展示内容和实体屏标定值补齐后，应替换对应测试参数并重新在真机验收。

## CornerBox45 转角裸眼 3D 概念验证

`Assets/LYNOOK/Scenes/03_CornerRoom_View45.unity` 将 Main 与 Side 当作一个凸盒子的正面和右侧面，而不是两个平铺窗口或向观察者打开的凹角。当前概念验证参数：

- 两块物理显示平面夹角：`90°`
- 固定 Sweet Spot：距转角 `600 mm`
- 水平观看方位：位于产品右前方，沿凸角外侧对角线 `45°`
- 中央转角离观察者最近，正面向左、侧面向右后方延伸；两侧外缘必须满足“远小”，不得生成凹角透视
- Main / Side 继续使用同一观察眼位和各自屏幕四角生成的 Off-Axis Frustum
- 物理屏幕模型只对 Observer Camera 可见，不会被两路内容相机递归拍入
- 复用旧测试中的 `Modern Bedroom City View` 高斯场景；在旧场景尺度中将虚拟镜头后退 `9.00 m`，再映射到新的45°共享眼位；这是当前资产在不退到墙体背后的前提下，能稳定覆盖床、床头柜、植物、椅子和活动地面的整屋取景上限
- 高斯渲染遵守 Camera Culling Mask，Observer 只能通过两块实体屏幕模型看到高斯画面，不会在屏外穿帮
- 校准用虚拟盒子仍保留在 Scene 中，但默认关闭，生活场景录制不显示透视框、网格和测试方块

除了两路屏幕素材，还增加四台只拍实体屏幕模型的 Observer Camera。标准相机位于 Sweet Spot；另外三台分别抬高 `360 mm`、向观察者左侧偏移 `180 mm`、向观察者右侧偏移 `180 mm`。它们只改变产品模型的观察位置，不修改两路共享眼位或 Off-Axis 投影：

```text
Recordings/LYNOOK/CornerBox45/front_corner45.mov
Recordings/LYNOOK/CornerBox45/side_corner45.mov
Recordings/LYNOOK/CornerBox45/corner45_observer_preview.mov
Recordings/LYNOOK/CornerBox45/corner45_observer_top.mov
Recordings/LYNOOK/CornerBox45/corner45_observer_left.mov
Recordings/LYNOOK/CornerBox45/corner45_observer_right.mov
```

六路由同一个 `RecorderController` 按 `30 fps / 300帧 / 10秒` 同步录制。偏离 Sweet Spot 后出现的接缝错位或透视变形是固定眼位方案的真实观察结果，不会通过重算投影进行补偿。Editor 菜单：

```text
Tools/LYNOOK/Room Scenes/03 Corner Room - View 45/Create or Open Scene
Tools/LYNOOK/Room Scenes/03 Corner Room - View 45/Rebuild Room Scene
Tools/LYNOOK/Record/03 Corner Room - View 45
```

Scene Builder 会在保存前自动检查凸角透视：中央接缝的投影高度必须同时大于正面外缘和侧面外缘。当前几何结果为 Main `1.213×`、Side `1.069×`，符合近大远小。

这里的“裸眼3D”是固定甜蜜点的单目强制透视，不是多视点立体显示。当前600 mm、90°和45°是概念验证 Profile；LYNOOK 实体夹角、眼位与边框尺寸仍需测量后替换。
