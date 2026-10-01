#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TextCore.Text;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public sealed class LYNOOKWorldStudioWindow : EditorWindow
    {
        const string LayoutPath = "Assets/Scripts/LYNOOK/Editor/LYNOOKWorldStudioWindow.uxml";
        [SerializeField] string gaussianFile, glbFile, roomTitle;
        [SerializeField] string roomType = LynookRoomTypes.Bedroom;
        [SerializeField] bool highQuality;
        static readonly List<string> RoomTypes = new List<string>(LynookRoomTypes.All);
        static readonly List<string> PointTypes = new List<string>(LynookActivityTypes.All);
        LYNOOKWorldAuthoring world;
        VisualElement controls;
        Label status;
        Image mainPreview, sidePreview;
        int pickingMode;
        bool hasPickMarker;
        RaycastHit lastPick;
        string pickMessage = "点击后显示 GLB 碰撞结果；Esc 结束。";
        Label pickResult, polygonInfo;
        bool busy;
        [SerializeField] FontAsset uiFont;

        // 批量录制：待处理任务队列与勾选状态
        List<LYNOOKWorldStudioService.SceneQueueItem> sceneQueueItems = new List<LYNOOKWorldStudioService.SceneQueueItem>();
        HashSet<int> sceneQueueSelected = new HashSet<int>();

        [MenuItem("Tools/LYNOOK/World Studio", false, 0)]
        public static void Open()
        {
            var window = GetWindow<LYNOOKWorldStudioWindow>();
            window.titleContent = new GUIContent("房间制作器");
            window.minSize = new Vector2(440, 560);
            window.Show();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGUI;
            EditorSceneManager.activeSceneChangedInEditMode += SceneChanged;
            Undo.undoRedoPerformed += Rebuild;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            LYNOOKBatchController.StateChanged += OnBatchStateChanged;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGUI;
            EditorSceneManager.activeSceneChangedInEditMode -= SceneChanged;
            Undo.undoRedoPerformed -= Rebuild;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            LYNOOKBatchController.StateChanged -= OnBatchStateChanged;
        }

        void OnBatchStateChanged()
        {
            SetStatus(LYNOOKBatchController.CurrentMessage);
            Rebuild();
        }

        void SceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene next) => Rebuild();

        void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode) Rebuild();
            if (controls != null) controls.SetEnabled(state == PlayModeStateChange.EnteredEditMode);
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null) { rootVisualElement.Add(new Label("找不到房间制作器布局文件。")); return; }
            layout.CloneTree(rootVisualElement);
            if (uiFont == null || !EditorUtility.IsPersistent(uiFont))
            {
                string path = "Assets/LYNOOK/WorldStudio/Editor/StudioFont_" + Application.platform + ".asset";
                uiFont = AssetDatabase.LoadAssetAtPath<FontAsset>(path);
                if (uiFont == null)
                {
                    uiFont = FontAsset.CreateFontAsset(Application.platform == RuntimePlatform.OSXEditor
                        ? "Arial Unicode MS" : "Microsoft YaHei", "Regular", 32);
                    if (uiFont != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        AssetDatabase.Refresh();
                        uiFont.isMultiAtlasTexturesEnabled = true;
                        uiFont.hideFlags = HideFlags.None;
                        AssetDatabase.CreateAsset(uiFont, path);
                        AssetDatabase.AddObjectToAsset(uiFont.material, uiFont);
                        foreach (var texture in uiFont.atlasTextures) AssetDatabase.AddObjectToAsset(texture, uiFont);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
            // An OS font is resolved per machine, avoiding a dependency on the Editor's exhausted CJK fallback atlas.
            if (uiFont != null) rootVisualElement.style.unityFontDefinition = FontDefinition.FromSDFFont(uiFont);
            controls = rootVisualElement.Q<VisualElement>("controls");
            status = rootVisualElement.Q<Label>("status");
            Rebuild();
        }

        void Rebuild()
        {
            if (controls == null) return;
            world = LYNOOKWorldStudioService.Current;
            if (world != null && (world.gaussian == null || world.cameraRig == null || world.activityPoints == null)) world = null;
            pickingMode = 0;
            hasPickMarker = false;
            controls.Clear();
            var files = Section("1 · 本地文件", world == null);
            var title = new TextField("房间名称") { value = roomTitle ?? "" };
            title.RegisterValueChangedCallback(e => roomTitle = e.newValue);
            files.Add(title);
            int typeIndex = RoomTypes.IndexOf(roomType ?? LynookRoomTypes.Bedroom);
            if (typeIndex < 0) typeIndex = 0;
            var typeField = new PopupField<string>("房间类型", RoomTypes, typeIndex);
            typeField.RegisterValueChangedCallback(e => roomType = e.newValue);
            files.Add(typeField);
            FileField(files, "高斯", gaussianFile, "ply,spz", p => gaussianFile = p);
            FileField(files, "配套 GLB", glbFile, "glb", p => glbFile = p);
            var quality = new Toggle("高质量转换（占用更多空间）") { value = highQuality };
            quality.RegisterValueChangedCallback(e => highQuality = e.newValue);
            files.Add(quality);
            var create = Button(files, "导入并创建制作场景", () =>
            {
                if (!File.Exists(gaussianFile) || !File.Exists(glbFile)) throw new InvalidOperationException("请先选择本地高斯和配套 GLB。");
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                busy = true;
                controls.SetEnabled(false);
                SetStatus("正在导入和转换，请等待。大文件可能需要几分钟。");
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        world = LYNOOKWorldSceneBuilder.Import(gaussianFile, glbFile, roomTitle, roomType, highQuality);
                        Rebuild();
                        SetStatus("导入完成。请先显示 GLB，检查对齐，再点击地面确认高度。");
                    }
                    catch (Exception exception) { Debug.LogException(exception); SetStatus(exception.Message); }
                    finally { busy = false; if (controls != null) controls.SetEnabled(true); EditorUtility.ClearProgressBar(); }
                };
            });
            create.AddToClassList("primary");
            Button(files, "打开已有制作场景…", () =>
            {
                string file = EditorUtility.OpenFilePanel("打开制作场景", Path.GetFullPath(LYNOOKWorldStudioService.WorkspaceRoot), "unity");
                if (string.IsNullOrEmpty(file)) return;
                string relative = FileUtil.GetProjectRelativePath(file);
                if (string.IsNullOrEmpty(relative)) throw new InvalidOperationException("请选择本工程 Assets 内的制作场景。");
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(relative);
            });

            // 批量录制：拉取待处理任务并勾选（不依赖当前 world，始终显示）
            var batchFold = new Foldout { text = "8 · 批量录制", value = false };
            controls.Add(batchFold);
            var batchList = new ScrollView(ScrollViewMode.Vertical) { style = { height = 240 } };
            var batchRow = Row(batchFold);
            Button(batchRow, "拉取待处理任务", () => Run(PullSceneQueue));
            Button(batchRow, "全选", () => { foreach (var item in sceneQueueItems) sceneQueueSelected.Add(item.id); RebuildBatchList(batchList); });
            Button(batchRow, "全不选", () => { sceneQueueSelected.Clear(); RebuildBatchList(batchList); });
            batchFold.Add(batchList);
            RebuildBatchList(batchList);

            // 批量进度与控制
            var batchProgress = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4, marginBottom = 4 } };
            if (LYNOOKBatchController.IsRunning)
                batchProgress.text = $"进度 {LYNOOKBatchController.CurrentIndex + 1}/{LYNOOKBatchController.TotalCount}：{LYNOOKBatchController.CurrentMessage}";
            else
                batchProgress.text = LYNOOKBatchController.HasFinished
                    ? LYNOOKBatchController.CurrentMessage
                    : "未运行批量录制。";
            batchFold.Add(batchProgress);

            if (LYNOOKBatchController.IsRunning && LYNOOKBatchController.CurrentState == BatchTaskState.WaitingForAlignment)
            {
                var alignBtn = Button(batchFold, "继续批量录制（对齐已确认）", () =>
                {
                    LYNOOKBatchController.ContinueFromAlignment();
                });
                alignBtn.AddToClassList("primary");
            }
            else if (LYNOOKBatchController.IsRunning && LYNOOKBatchController.CurrentState == BatchTaskState.WaitingForCamera)
            {
                var continueBtn = Button(batchFold, "继续批量录制（相机已调好）", () =>
                {
                    LYNOOKBatchController.ContinueFromCamera();
                });
                continueBtn.AddToClassList("primary");
            }
            else if (!LYNOOKBatchController.IsRunning)
            {
                var batchStart = Button(batchFold, "开始批量录制", () =>
                {
                    if (sceneQueueSelected.Count == 0) throw new InvalidOperationException("请先勾选待录制任务。");
                    var selected = sceneQueueItems.Where(i => sceneQueueSelected.Contains(i.id)).ToList();
                    string names = string.Join("\n  ", selected.Select(i => $"#{i.id} {i.name}"));
                    if (!EditorUtility.DisplayDialog("确认批量录制",
                        $"即将批量录制 {selected.Count} 个场景：\n  {names}\n\n每个场景将：下载 → 导入 → 暂停调对齐 → 出生点 → 活动点 → 暂停调相机 → 录制 → 上传 Azure → 更新数据库。\n\n是否继续？",
                        "确定", "取消")) return;
                    try { LYNOOKBatchController.Start(selected); }
                    catch (Exception e) { Debug.LogException(e); SetStatus(e.Message); }
                });
                batchStart.AddToClassList("primary");
            }
            Help(batchFold, "从 Postgres scenes 表拉取 convert_status = pending 的待转换场景，勾选后批量录制。相机取景步骤需手动调整，调好后点击「继续批量录制」。");

            // 临时目录管理：打开 / 清空批量下载的临时资产目录
            var tempRow = Row(batchFold);
            Button(tempRow, "打开临时目录", () =>
            {
                try { LYNOOKSceneService.OpenBatchTempDirectory(); }
                catch (Exception e) { Debug.LogException(e); SetStatus(e.Message); }
            });
            Button(tempRow, "清空临时目录", () =>
            {
                try
                {
                    string root = LYNOOKWorldStudioService.BatchTempRoot;
                    if (!EditorUtility.DisplayDialog("确认清空临时目录",
                        $"将删除批量录制下载的所有临时资产（SPZ/GLB）：\n{root}\n\n此操作不可恢复，是否继续？",
                        "确定清空", "取消")) return;
                    long freed = LYNOOKSceneService.ClearBatchTempDirectory();
                    SetStatus($"临时目录已清空，释放 {freed / 1024.0 / 1024.0:F1} MB。");
                }
                catch (Exception e) { Debug.LogException(e); SetStatus(e.Message); }
            });

            controls.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode && !busy);
            if (world == null) return;

            controls.Add(new Label("当前房间：" + world.displayName));
            var alignment = Section("2 · 对齐和地面", true);
            var visibility = new Toggle("显示 GLB 网格（取消后仅显示高斯）") { value = world.showCollision };
            visibility.RegisterValueChangedCallback(e => Run(() =>
            {
                Undo.RecordObjects(world.collisionObject.GetComponentsInChildren<Renderer>(true), "Toggle collision preview");
                Undo.RecordObject(world, "Toggle collision preview");
                LYNOOKWorldInteraction.SetCollisionVisible(world, e.newValue);
            }));
            alignment.Add(visibility);
            var alignRow = Row(alignment);
            Button(alignRow, "定位房间", () => LYNOOKWorldInteraction.Frame(world));
            Button(alignRow, "调整高斯对齐", () => Select(world.gaussian.transform));
            Button(alignRow, "整体位置 / 尺度", () => Select(world.coordinateRoot));
            var gaussianTransform = new Foldout { text = "高斯相对 GLB 的位置 / 旋转 / 缩放", value = false };
            alignment.Add(gaussianTransform);
            var splatSettings = new SerializedObject(world.gaussian.transform);
            foreach (string property in new[] { "m_LocalPosition", "m_LocalScale" })
            {
                var field = new PropertyField(splatSettings.FindProperty(property));
                field.Bind(splatSettings); gaussianTransform.Add(field);
            }
            var angles = new Vector3Field("旋转角度") { value = world.gaussian.transform.localEulerAngles };
            angles.RegisterValueChangedCallback(e => Run(() =>
            {
                Undo.RecordObject(world.gaussian.transform, "Align Gaussian rotation");
                world.gaussian.transform.localEulerAngles = e.newValue;
                Undo.RecordObject(world, "Invalidate alignment confirmation"); world.alignmentConfirmed = false;
                LYNOOKWorldInteraction.Changed(world);
            }));
            gaussianTransform.Add(angles);
            Button(gaussianTransform, "高斯绕 X 轴转 180°", () =>
            {
                Undo.RecordObject(world.gaussian.transform, "Rotate Gaussian coordinates");
                world.gaussian.transform.Rotate(180, 0, 0, Space.Self);
                Undo.RecordObject(world, "Invalidate alignment confirmation"); world.alignmentConfirmed = false;
                LYNOOKWorldInteraction.Changed(world); RefreshPreview();
            });
            Property(alignment, "roomType", "房间类型");
            Property(alignment, "alignmentConfirmed", "我已检查高斯与 GLB 对齐");
            var floorRow = Row(alignment);
            Button(floorRow, "自动识别地面范围", () => Run(() =>
            {
                Undo.RecordObject(world, "Auto detect floor");
                LYNOOKFloorDetection.AutoDetectFloor(world);
                Undo.RecordObject(world, "Invalidate alignment confirmation"); world.alignmentConfirmed = false;
                LYNOOKWorldInteraction.Changed(world); Rebuild();
                string shape = world.HasWalkPolygon ? $"{world.walkPolygon.Count} 边形" : $"{world.walkSize.x:F2} × {world.walkSize.y:F2} m";
                SetStatus($"已自动识别地面：高度 {world.floorHeight:F3}，范围 {shape}。");
            }));
            Button(floorRow, "在场景中点击地面", () => BeginPicking(1));
            pickResult = new Label(pickMessage);
            pickResult.style.whiteSpace = WhiteSpace.Normal;
            alignment.Add(pickResult);
            Help(alignment, "连续点击测试 GLB：绿色为水平表面（含反向法线），橙色为墙面或陡坡。点击水平表面后，以该点为起点提取连通地面范围；墙面不改变范围。");
            Property(alignment, "floorHeight", "地面高度（房间坐标）");
            Property(alignment, "walkCenter", "可走范围中心 X / Z");
            Property(alignment, "walkSize", "可走范围宽 / 深（矩形回退）");
            Property(alignment, "walkAreaThickness", "可走范围可视化厚度（米）");

            // 多边形可走范围
            var polyFold = new Foldout { text = "可走范围多边形（不规则地面）", value = world.HasWalkPolygon };
            alignment.Add(polyFold);
            string polyInfo = world.HasWalkPolygon
                ? $"当前 {world.walkPolygon.Count} 边形，边界 {world.walkSize.x:F2} × {world.walkSize.y:F2} m。"
                : "当前使用矩形范围，未定义多边形。";
            polygonInfo = new Label(polyInfo);
            polygonInfo.AddToClassList("description");
            polyFold.Add(polygonInfo);
            var polyRow1 = Row(polyFold);
            Button(polyRow1, "点选添加顶点", () => BeginPicking(4));
            Button(polyRow1, "撤销最后一个顶点", () => Run(() =>
            {
                if (world.walkPolygon.Count == 0) throw new InvalidOperationException("没有可撤销的顶点。");
                Undo.RecordObject(world, "Remove polygon vertex");
                world.walkPolygon.RemoveAt(world.walkPolygon.Count - 1);
                LYNOOKWorldInteraction.Changed(world); Rebuild();
                SetStatus($"已撤销顶点，剩余 {world.walkPolygon.Count} 个。");
            }));
            var polyRow2 = Row(polyFold);
            Button(polyRow2, "清空多边形（用矩形）", () => Run(() =>
            {
                Undo.RecordObject(world, "Clear walk polygon");
                world.walkPolygon.Clear();
                LYNOOKWorldInteraction.Changed(world); Rebuild();
                SetStatus("已清空多边形，改用矩形范围。");
            }));
            Help(polyFold, "点选添加顶点：在 Scene 视图依次点击地面，形成闭合多边形（≥3 个顶点即生效）。Esc 结束添加。");
            Help(alignment, "青色填充是可走范围。自动识别会拟合地面真实多边形轮廓；也可手动点选顶点定义不规则形状。");

            var camera = Section("3 · 相机取景", true);
            var cameraRow = Row(camera);
            Button(cameraRow, "使用当前 Scene 视角", AlignCamera);
            Button(cameraRow, "选择拍摄相机架", () => Select(world.cameraRig.CaptureRig));
            Button(camera, "刷新双屏预览", RefreshPreview);
            Button(camera, "录制 2 秒取景预览（不生成交付配置）", () => LYNOOKWorldStudioRecording.StartPreview(world));
            var previewRow = new VisualElement();
            previewRow.AddToClassList("preview-row");
            camera.Add(previewRow);
            mainPreview = new Image { scaleMode = ScaleMode.ScaleToFit, tooltip = "主屏" };
            sidePreview = new Image { scaleMode = ScaleMode.ScaleToFit, tooltip = "侧屏" };
            mainPreview.AddToClassList("preview"); sidePreview.AddToClassList("preview");
            previewRow.Add(mainPreview); previewRow.Add(sidePreview);
            Help(camera, "取景会移动共享视点相机架；双屏投影仍使用现有模板。以双屏预览为准，实物接缝另行校准。");

            var spawn = Section("4 · 出生地", true);
            Property(spawn, "agentHeight", "预览角色高度");
            Property(spawn, "agentRadius", "预览角色半径");
            var spawnRow = Row(spawn);
            Button(spawnRow, world.spawnPlaced ? "重新点选出生地" : "在地面点选出生地", () => BeginPicking(2));
            Button(spawnRow, "调整出生方向 / 位置", () => Select(world.avatarSpawn));
            Help(spawn, "绿色线框代表角色占用空间，不会录入背景视频。选中出生点后，用 Unity 移动 / 旋转工具微调。");

            var points = Section("5 · 活动点", true);
            Property(points, "pointSpacing", "点位最小间距");
            Property(points, "maximumPoints", "最多生成点数");
            Property(points, "floorTolerance", "地面高度容差");
            Button(points, "生成站立活动点", () =>
            {
                int count = LYNOOKWorldInteraction.GeneratePoints(world);
                Rebuild(); SetStatus("已生成 " + count + " 个站立点。旧点位可通过撤销恢复。");
            });
            var pointsActionRow = Row(points);
            Button(pointsActionRow, "点击地面添加一个活动点", () => BeginPicking(3));
            Button(pointsActionRow, "全部删除", () => Run(() =>
            {
                int count = world.activityPoints.childCount;
                if (count == 0) throw new InvalidOperationException("没有可删除的活动点。");
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                for (int i = world.activityPoints.childCount - 1; i >= 0; i--)
                    Undo.DestroyObjectImmediate(world.activityPoints.GetChild(i).gameObject);
                Undo.CollapseUndoOperations(group);
                LYNOOKWorldInteraction.Changed(world); Rebuild();
                SetStatus("已删除全部 " + count + " 个活动点。");
            }));
            foreach (Transform point in world.activityPoints)
            {
                var captured = point;
                var row = Row(points);
                var label = new Label(point.name); label.AddToClassList("point-label"); row.Add(label);
                var meta = captured.GetComponent<LYNOOKActivityPoint>();
                string currentType = meta != null && !string.IsNullOrWhiteSpace(meta.type) ? meta.type : LynookActivityTypes.Stand;
                int typeIdx = PointTypes.IndexOf(currentType);
                var ptField = new PopupField<string>(PointTypes, typeIdx < 0 ? 0 : typeIdx);
                ptField.style.width = 100;
                ptField.RegisterValueChangedCallback(e => Run(() =>
                {
                    var m = captured.GetComponent<LYNOOKActivityPoint>();
                    if (m == null) { m = captured.gameObject.AddComponent<LYNOOKActivityPoint>(); }
                    Undo.RecordObject(m, "Change point type");
                    m.type = e.newValue;
                    LYNOOKWorldInteraction.Changed(world);
                }));
                row.Add(ptField);
                Button(row, "调整", () => Select(captured));
                Button(row, "删除", () => { Undo.DestroyObjectImmediate(captured.gameObject); LYNOOKWorldInteraction.Changed(world); Rebuild(); });
            }
            Help(points, "每个活动点可选择类型（stand / sit / seat / look / walk / interact），导出时写入 world_config.json 的 activityPoints[].type。");

            var output = Section("6 · 保存和录制", true);
            Property(output, "recordingSeconds", "录制时长（秒）");
            var outputRow = Row(output);
            Button(outputRow, "保存制作草稿", () => { LYNOOKWorldPersistence.SaveDraft(world); SetStatus("已保存制作场景与 world_draft.json。"); });
            Button(outputRow, "检查全部点位", () => SetStatus(LYNOOKWorldPersistence.ValidateForRecording(world)));
            Button(output, "录制双屏并导出本地房间包", () => LYNOOKWorldStudioRecording.Start(world)).AddToClassList("primary");
            Button(output, "打开制作文件夹", () => EditorUtility.RevealInFinder(Path.GetFullPath(world.workspacePath)));
            Button(output, "打开录制输出文件夹", () => EditorUtility.RevealInFinder(Path.GetFullPath("Recordings/LYNOOK/WorldStudio")));

            // 部署：配置目标路径并一键发布到 Unity 部署工程
            var deployFold = new Foldout { text = "7 · 发布到部署工程", value = false };
            controls.Add(deployFold);
            var deployPathRow = Row(deployFold);
            var deployPath = new TextField("部署目标路径") { value = world.deployTargetPath ?? "" };
            deployPath.tooltip = "Unity 部署工程的 Assets 目录（或其子目录）的绝对路径，例如 /path/to/DeployProject/Assets/Worlds";
            deployPath.RegisterValueChangedCallback(e => Run(() =>
            {
                Undo.RecordObject(world, "Set deploy target path");
                world.deployTargetPath = e.newValue;
                LYNOOKWorldInteraction.Changed(world);
            }));
            deployPathRow.Add(deployPath);
            Button(deployPathRow, "浏览…", () =>
            {
                string selected = EditorUtility.OpenFolderPanel("选择部署工程的目标目录", world.deployTargetPath ?? "", "");
                if (!string.IsNullOrEmpty(selected)) deployPath.value = selected;
            });
            Button(deployFold, "一键发布（复制房间到部署工程）", () => Run(() =>
            {
                SetStatus("正在发布，请等待…");
                string result = LYNOOKWorldPersistence.Deploy(world, world.deployTargetPath);
                SetStatus(result);
            })).AddToClassList("primary");
            Help(deployFold, "把当前房间的完整工作区（高斯资产、场景、配置）复制到目标路径，并附带最近一次录制产物（双屏视频、world_config.json 等）。目标需是另一个 Unity 工程的 Assets 目录，由该工程自行导入。");
        }

        void PullSceneQueue()
        {
            SetStatus("正在拉取待处理任务…");
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var items = LYNOOKSceneService.FetchSceneQueue(50);
                    sceneQueueItems = items.ToList();
                    // 保留已勾选的 id，清除已不存在的
                    var validIds = new HashSet<int>(sceneQueueItems.Select(i => i.id));
                    sceneQueueSelected.RemoveWhere(id => !validIds.Contains(id));
                    Rebuild();
                    SetStatus($"拉取完成，共 {sceneQueueItems.Count} 个待处理任务。");
                }
                catch (Exception exception) { Debug.LogException(exception); SetStatus(exception.Message); }
            };
        }

        void RebuildBatchList(VisualElement container)
        {
            container.Clear();
            if (sceneQueueItems.Count == 0)
            {
                container.Add(new Label("暂无待处理任务，点击「拉取待处理任务」。"));
                return;
            }
            foreach (var item in sceneQueueItems)
            {
                var row = Row(container);
                var toggle = new Toggle { value = sceneQueueSelected.Contains(item.id) };
                toggle.style.width = 20;
                int capturedId = item.id;
                toggle.RegisterValueChangedCallback(e =>
                {
                    if (e.newValue) sceneQueueSelected.Add(capturedId);
                    else sceneQueueSelected.Remove(capturedId);
                });
                row.Add(toggle);
                string owner = item.ownerName ?? item.ownerUsername ?? item.ownerEmail ?? "unknown";
                string label = $"#{item.id}  {item.name}  ({owner})  {item.model ?? "-"}";
                row.Add(new Label(label) { style = { flexGrow = 1 } });
            }
        }

        void FileField(VisualElement parent, string label, string value, string extensions, Action<string> setter)
        {
            var row = Row(parent);
            row.AddToClassList("file-row");
            var field = new TextField(label) { value = value ?? "" };
            field.AddToClassList("path");
            field.RegisterValueChangedCallback(e => setter(e.newValue));
            row.Add(field);
            Button(row, "选择…", () =>
            {
                string selected = EditorUtility.OpenFilePanel("选择 " + label, "", extensions);
                if (!string.IsNullOrEmpty(selected)) field.value = selected;
            });
        }

        void Property(VisualElement parent, string name, string label)
        {
            var serialized = new SerializedObject(world);
            var field = new PropertyField(serialized.FindProperty(name), label);
            field.Bind(serialized);
            parent.Add(field);
        }

        Foldout Section(string title, bool expanded)
        {
            var section = new Foldout { text = title, value = expanded };
            section.AddToClassList("section"); controls.Add(section); return section;
        }

        static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row;
        }

        Button Button(VisualElement parent, string label, Action action)
        {
            var button = new Button(() => Run(action)) { text = label };
            parent.Add(button); return button;
        }

        static void Help(VisualElement parent, string text)
        {
            var label = new Label(text); label.AddToClassList("description"); parent.Add(label);
        }

        void Run(Action action)
        {
            if (busy || EditorApplication.isPlayingOrWillChangePlaymode) { SetStatus("请等待导入或录制完成。"); return; }
            try { action(); }
            catch (Exception exception) { SetStatus(exception.Message); Debug.LogException(exception); }
        }

        void SetStatus(string message) { if (status != null) status.text = message; }
        static void Select(Transform target) { Selection.activeGameObject = target.gameObject; Tools.current = Tool.Move; }

        void AlignCamera()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null || view.orthographic) throw new InvalidOperationException("请先在 Scene 窗口用透视视图调整取景。");
            var rig = world.cameraRig;
            rig.ApplyConfiguration();
            Undo.RecordObject(rig.CaptureRig, "Set capture viewpoint");
            Quaternion relative = Quaternion.Inverse(rig.CaptureRig.rotation) * rig.MainCaptureCamera.transform.rotation;
            rig.CaptureRig.rotation = view.camera.transform.rotation * Quaternion.Inverse(relative);
            rig.CaptureRig.position += view.camera.transform.position - rig.SharedEyeWorldPosition;
            rig.ApplyConfiguration();
            LYNOOKWorldInteraction.Changed(world);
            RefreshPreview();
        }

        void RefreshPreview()
        {
            world.gaussian.EnsureMaterials();
            world.gaussian.EnsureSorterAndRegister();
            if (!world.gaussian.HasValidAsset || !world.gaussian.HasValidRenderSetup)
                throw new InvalidOperationException("高斯渲染资源尚未就绪，请等待导入完成。");
            world.cameraRig.ApplyConfiguration();
            world.cameraRig.MainCaptureCamera.Render();
            world.cameraRig.SideCaptureCamera.Render();
            mainPreview.image = world.cameraRig.MainCaptureTexture;
            sidePreview.image = world.cameraRig.SideCaptureTexture;
        }

        void BeginPicking(int mode)
        {
            if (world == null || world.coordinateRoot == null || world.collisionObject == null)
                throw new InvalidOperationException("请先导入房间 GLB。");
            if (mode != 1) LYNOOKWorldPersistence.ValidateSettings(world);
            hasPickMarker = false;
            pickingMode = mode;
            wantsMouseMove = true;
            var view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
            view.wantsMouseMove = true;
            view.Focus();
            view.Repaint();
            SetStatus(mode == 4
                ? "依次点击地面添加多边形顶点，Esc 结束。"
                : "在 Scene 视图点击 GLB 地面。Esc 取消，Alt + 鼠标仍可浏览。");
        }

        void SetPickMessage(string message)
        {
            pickMessage = message;
            if (pickResult != null) pickResult.text = message;
            SetStatus(message);
        }

        static void DrawPickMarker(RaycastHit hit, bool isFloor)
        {
            var previousDepth = Handles.zTest;
            Color previousColor = Handles.color;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Handles.color = isFloor ? Color.green : new Color(1f, 0.55f, 0.1f);
            float size = HandleUtility.GetHandleSize(hit.point) * 0.06f;
            Handles.DrawWireDisc(hit.point, hit.normal, size);
            Handles.DrawLine(hit.point, hit.point + hit.normal * size * 2f);
            Handles.color = previousColor;
            Handles.zTest = previousDepth;
            // Screen-space cross stays visible even over the Gaussian render pass.
            Vector2 point = HandleUtility.WorldToGUIPoint(hit.point);
            Handles.BeginGUI();
            Color previousGUI = GUI.color;
            GUI.color = isFloor ? Color.green : new Color(1f, 0.55f, 0.1f);
            GUI.DrawTexture(new Rect(point.x - 9, point.y - 1, 18, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(point.x - 1, point.y - 9, 2, 18), Texture2D.whiteTexture);
            GUI.color = previousGUI;
            Handles.EndGUI();
        }

        void DuringSceneGUI(SceneView view)
        {
            if (world == null || world.gameObject.scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene() || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (world.spawnPlaced)
                foreach (Transform point in world.activityPoints)
                {
                    Handles.color = new Color(1, 0.7f, 0.2f, 0.7f);
                    Handles.DrawDottedLine(world.avatarSpawn.position, point.position, 5);
                    Handles.Label(point.position + world.coordinateRoot.up * 0.1f, point.name);
                }
            if (pickingMode == 0) return;
            Event current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            { pickingMode = 0; current.Use(); SetStatus("已结束点选。"); return; }
            if (current.alt) return;
            if (current.type == EventType.MouseMove) HandleUtility.Repaint();
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            bool gotHit = LYNOOKWorldInteraction.Raycast(world, ray, out var hit);
            bool isFloor = gotHit && Mathf.Abs(Vector3.Dot(hit.normal, world.coordinateRoot.up)) >= Mathf.Cos(25f * Mathf.Deg2Rad);
            if (current.type == EventType.Repaint)
            {
                if (pickingMode == 1 && hasPickMarker && world.HasWalkPolygon)
                {
                    var depth = Handles.zTest;
                    var color = Handles.color;
                    Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
                    Handles.color = Color.cyan;
                    for (int i = 0; i < world.walkPolygon.Count; i++)
                    {
                        Vector2 a = world.walkPolygon[i], b = world.walkPolygon[(i + 1) % world.walkPolygon.Count];
                        Handles.DrawAAPolyLine(3, world.coordinateRoot.TransformPoint(new Vector3(a.x, world.floorHeight, a.y)),
                            world.coordinateRoot.TransformPoint(new Vector3(b.x, world.floorHeight, b.y)));
                    }
                    Handles.zTest = depth;
                    Handles.color = color;
                }
                if (gotHit && !hasPickMarker) DrawPickMarker(hit, isFloor);
                if (hasPickMarker) DrawPickMarker(lastPick,
                    Mathf.Abs(Vector3.Dot(lastPick.normal, world.coordinateRoot.up)) >= Mathf.Cos(25f * Mathf.Deg2Rad));
                Handles.BeginGUI();
                var panel = new Rect(55, 36, Mathf.Min(440, view.position.width - 70), 76);
                EditorGUI.DrawRect(panel, new Color(0.08f, 0.08f, 0.08f, 1));
                var labelStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, padding = new RectOffset(8, 8, 5, 5) };
                labelStyle.normal.textColor = Color.white;
                RaycastHit displayed = hasPickMarker ? lastPick : hit;
                bool displayedHit = hasPickMarker || gotHit;
                bool displayedFloor = displayedHit && Mathf.Abs(Vector3.Dot(displayed.normal, world.coordinateRoot.up)) >= Mathf.Cos(25f * Mathf.Deg2Rad);
                GUI.Label(panel,
                    $"GLB collision test | {(hasPickMarker ? "Last click" : "Hover")} | Esc: finish\n" +
                    (displayedHit ? $"{(displayedFloor ? "Horizontal surface" : "Wall / steep surface")} | {displayed.collider.name}\nRoom: {world.coordinateRoot.InverseTransformPoint(displayed.point):F3}"
                        : "No GLB collision under cursor") + "\nClick a horizontal surface to select its connected floor area.", labelStyle);
                Handles.EndGUI();
            }
            if (!gotHit)
            {
                if (current.type == EventType.MouseDown && current.button == 0)
                {
                    current.Use();
                    hasPickMarker = false;
                    SetPickMessage("未命中 GLB 碰撞网格；地面高度未修改。");
                    view.Repaint();
                }
                return;
            }

            // 多边形顶点添加模式：预览当前多边形 + 待添加的边
            if (pickingMode == 4 && world.walkPolygon.Count > 0)
            {
                Handles.color = new Color(0.25f, 0.8f, 0.85f, 0.9f);
                Vector3 up = world.coordinateRoot.up;
                Vector3 prev = world.coordinateRoot.TransformPoint(new Vector3(world.walkPolygon[0].x, world.floorHeight, world.walkPolygon[0].y));
                for (int i = 1; i < world.walkPolygon.Count; i++)
                {
                    Vector3 p = world.coordinateRoot.TransformPoint(new Vector3(world.walkPolygon[i].x, world.floorHeight, world.walkPolygon[i].y));
                    Handles.DrawLine(prev, p);
                    prev = p;
                }
                // 从最后一个顶点到当前鼠标点的预览线
                Handles.DrawDottedLine(prev, hit.point, 5f);
            }

            if (current.type == EventType.MouseDown && current.button == 0)
            {
                current.Use();
                Run(() =>
                {
                    if (pickingMode == 1)
                    {
                        lastPick = hit;
                        hasPickMarker = true;
                        Vector3 local = world.coordinateRoot.InverseTransformPoint(hit.point);
                        float directionAngle = Vector3.Angle(hit.normal, world.coordinateRoot.up);
                        float slope = Mathf.Min(directionAngle, 180f - directionAngle);
                        if (isFloor)
                        {
                            Undo.RecordObject(world, "Set floor height from collision");
                            LYNOOKFloorDetection.AutoDetectFloorFromPoint(world, hit.point);
                            if (polygonInfo != null) polygonInfo.text = $"当前 {world.walkPolygon.Count} 边形，边界 {world.walkSize.x:F2} × {world.walkSize.y:F2} m。";
                            LYNOOKWorldInteraction.Changed(world);
                        }
                        SetPickMessage($"{(isFloor ? "已从点击点提取连通地面范围" : "命中墙面/陡坡，未修改地面")}\n碰撞体：{hit.collider.name}；房间坐标：{local:F3}；坡度：{slope:F1}°；法线：{hit.normal:F2}");
                        view.Repaint();
                        return;
                    }
                    else if (pickingMode == 2)
                    {
                        LYNOOKWorldInteraction.PlaceSpawn(world, hit);
                        pickingMode = 0; Rebuild(); SetStatus("出生地已设置。可以继续调整，或保存制作草稿。");
                    }
                    else if (pickingMode == 4)
                    {
                        if (!isFloor) throw new InvalidOperationException("点到了墙面或陡坡，请点击地面。");
                        Undo.RecordObject(world, "Add polygon vertex");
                        Vector3 local = world.coordinateRoot.InverseTransformPoint(hit.point);
                        // 第一个顶点同步地面高度
                        if (world.walkPolygon.Count == 0) LYNOOKFloorDetection.AutoDetectFloorFromPoint(world, hit.point);
                        world.walkPolygon.Add(new Vector2(local.x, local.z));
                        LYNOOKWorldInteraction.Changed(world);
                        // 不退出点选模式，允许继续添加
                        SetStatus($"已添加第 {world.walkPolygon.Count} 个顶点，继续点击或 Esc 结束。");
                        view.Repaint();
                    }
                    else
                    {
                        if (!world.spawnPlaced) throw new InvalidOperationException("先设置出生地。");
                        if (!LYNOOKWorldInteraction.ValidStandingPoint(world, hit.point, out string reason)) throw new InvalidOperationException(reason);
                        if (!LYNOOKWorldInteraction.DirectPathClear(world, world.avatarSpawn.position, hit.point)) throw new InvalidOperationException("出生地到该位置存在障碍。");
                        var point = new GameObject("stand_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                        point.transform.SetParent(world.activityPoints, false);
                        point.transform.position = hit.point;
                        point.transform.rotation = world.avatarSpawn.rotation;
                        var meta = point.AddComponent<LYNOOKActivityPoint>();
                        meta.type = LynookActivityTypes.Stand;
                        Undo.RegisterCreatedObjectUndo(point, "Add activity point");
                        pickingMode = 0; Rebuild(); SetStatus("活动点已添加。");
                    }
                    if (pickingMode != 4) LYNOOKWorldInteraction.Changed(world);
                });
            }
            if (current.type == EventType.MouseMove) view.Repaint();
        }
    }
}
#endif
