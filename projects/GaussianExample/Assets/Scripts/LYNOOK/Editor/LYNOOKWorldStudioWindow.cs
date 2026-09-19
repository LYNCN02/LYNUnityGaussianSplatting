#if UNITY_EDITOR
using System;
using System.IO;
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
        [SerializeField] bool highQuality;
        LYNOOKWorldAuthoring world;
        VisualElement controls;
        Label status;
        Image mainPreview, sidePreview;
        int pickingMode;
        bool busy;
        [SerializeField] FontAsset uiFont;

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
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGUI;
            EditorSceneManager.activeSceneChangedInEditMode -= SceneChanged;
            Undo.undoRedoPerformed -= Rebuild;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
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
            controls.Clear();
            var files = Section("1 · 本地文件", world == null);
            var title = new TextField("房间名称") { value = roomTitle ?? "" };
            title.RegisterValueChangedCallback(e => roomTitle = e.newValue);
            files.Add(title);
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
                        world = LYNOOKWorldStudioService.Import(gaussianFile, glbFile, roomTitle, highQuality);
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
            if (world == null) return;

            controls.Add(new Label("当前房间：" + world.displayName));
            var alignment = Section("2 · 对齐和地面", true);
            var visibility = new Toggle("显示 GLB 网格（取消后仅显示高斯）") { value = world.showCollision };
            visibility.RegisterValueChangedCallback(e => Run(() =>
            {
                Undo.RecordObjects(world.collisionObject.GetComponentsInChildren<Renderer>(true), "Toggle collision preview");
                Undo.RecordObject(world, "Toggle collision preview");
                LYNOOKWorldStudioService.SetCollisionVisible(world, e.newValue);
            }));
            alignment.Add(visibility);
            var alignRow = Row(alignment);
            Button(alignRow, "定位房间", () => LYNOOKWorldStudioService.Frame(world));
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
                LYNOOKWorldStudioService.Changed(world);
            }));
            gaussianTransform.Add(angles);
            Button(gaussianTransform, "高斯绕 X 轴转 180°", () =>
            {
                Undo.RecordObject(world.gaussian.transform, "Rotate Gaussian coordinates");
                world.gaussian.transform.Rotate(180, 0, 0, Space.Self);
                Undo.RecordObject(world, "Invalidate alignment confirmation"); world.alignmentConfirmed = false;
                LYNOOKWorldStudioService.Changed(world); RefreshPreview();
            });
            Property(alignment, "alignmentConfirmed", "我已检查高斯与 GLB 对齐");
            var floorRow = Row(alignment);
            Button(floorRow, "自动识别地面范围", () => Run(() =>
            {
                Undo.RecordObject(world, "Auto detect floor");
                LYNOOKWorldStudioService.AutoDetectFloor(world);
                Undo.RecordObject(world, "Invalidate alignment confirmation"); world.alignmentConfirmed = false;
                LYNOOKWorldStudioService.Changed(world); Rebuild();
                string shape = world.HasWalkPolygon ? $"{world.walkPolygon.Count} 边形" : $"{world.walkSize.x:F2} × {world.walkSize.y:F2} m";
                SetStatus($"已自动识别地面：高度 {world.floorHeight:F3}，范围 {shape}。");
            }));
            Button(floorRow, "在场景中点击地面", () => BeginPicking(1));
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
            Help(polyFold, polyInfo);
            var polyRow1 = Row(polyFold);
            Button(polyRow1, "点选添加顶点", () => BeginPicking(4));
            Button(polyRow1, "撤销最后一个顶点", () => Run(() =>
            {
                if (world.walkPolygon.Count == 0) throw new InvalidOperationException("没有可撤销的顶点。");
                Undo.RecordObject(world, "Remove polygon vertex");
                world.walkPolygon.RemoveAt(world.walkPolygon.Count - 1);
                LYNOOKWorldStudioService.Changed(world); Rebuild();
                SetStatus($"已撤销顶点，剩余 {world.walkPolygon.Count} 个。");
            }));
            var polyRow2 = Row(polyFold);
            Button(polyRow2, "清空多边形（用矩形）", () => Run(() =>
            {
                Undo.RecordObject(world, "Clear walk polygon");
                world.walkPolygon.Clear();
                LYNOOKWorldStudioService.Changed(world); Rebuild();
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
                int count = LYNOOKWorldStudioService.GeneratePoints(world);
                Rebuild(); SetStatus("已生成 " + count + " 个站立点。旧点位可通过撤销恢复。");
            });
            Button(points, "点击地面添加一个活动点", () => BeginPicking(3));
            foreach (Transform point in world.activityPoints)
            {
                var captured = point;
                var row = Row(points);
                var label = new Label(point.name); label.AddToClassList("point-label"); row.Add(label);
                Button(row, "调整", () => Select(captured));
                Button(row, "删除", () => { Undo.DestroyObjectImmediate(captured.gameObject); LYNOOKWorldStudioService.Changed(world); Rebuild(); });
            }
            Help(points, "第一版只生成 stand 点，检查地面、角色净空和各点之间的直线路径。座椅识别与自动寻路尚未接入。");

            var output = Section("6 · 保存和录制", true);
            Property(output, "recordingSeconds", "录制时长（秒）");
            var outputRow = Row(output);
            Button(outputRow, "保存制作草稿", () => { LYNOOKWorldStudioService.SaveDraft(world); SetStatus("已保存制作场景与 world_draft.json。"); });
            Button(outputRow, "检查全部点位", () => SetStatus(LYNOOKWorldStudioService.ValidateForRecording(world)));
            Button(output, "录制双屏并导出本地房间包", () => LYNOOKWorldStudioRecording.Start(world)).AddToClassList("primary");
            Button(output, "打开制作文件夹", () => EditorUtility.RevealInFinder(Path.GetFullPath(world.workspacePath)));
            Button(output, "打开录制输出文件夹", () => EditorUtility.RevealInFinder(Path.GetFullPath("Recordings/LYNOOK/WorldStudio")));
            controls.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode && !busy);
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
            LYNOOKWorldStudioService.Changed(world);
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
            LYNOOKWorldStudioService.ValidateSettings(world);
            pickingMode = mode;
            var view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
            view.Focus();
            SetStatus(mode == 4
                ? "依次点击地面添加多边形顶点，Esc 结束。"
                : "在 Scene 视图点击 GLB 地面。Esc 取消，Alt + 鼠标仍可浏览。");
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
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (!LYNOOKWorldStudioService.Raycast(world, ray, out var hit)) return;
            Handles.color = Color.cyan;
            Handles.DrawWireDisc(hit.point, hit.normal, world.agentRadius * world.coordinateRoot.lossyScale.x);

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
                        if (Vector3.Dot(hit.normal, world.coordinateRoot.up) < 0.9f) throw new InvalidOperationException("请选择平坦地面。");
                        Undo.RecordObject(world, "Set floor");
                        Vector3 local = world.coordinateRoot.InverseTransformPoint(hit.point);
                        world.floorHeight = local.y; world.walkCenter = new Vector2(local.x, local.z);
                        pickingMode = 0; Rebuild(); SetStatus("地面高度与中心已设置。");
                    }
                    else if (pickingMode == 2)
                    {
                        LYNOOKWorldStudioService.PlaceSpawn(world, hit);
                        pickingMode = 0; Rebuild(); SetStatus("出生地已设置。可以继续调整，或保存制作草稿。");
                    }
                    else if (pickingMode == 4)
                    {
                        if (Vector3.Dot(hit.normal, world.coordinateRoot.up) < 0.9f) throw new InvalidOperationException("请选择平坦地面作为多边形顶点。");
                        Undo.RecordObject(world, "Add polygon vertex");
                        Vector3 local = world.coordinateRoot.InverseTransformPoint(hit.point);
                        // 第一个顶点同步地面高度
                        if (world.walkPolygon.Count == 0) world.floorHeight = local.y;
                        world.walkPolygon.Add(new Vector2(local.x, local.z));
                        LYNOOKWorldStudioService.Changed(world);
                        // 不退出点选模式，允许继续添加
                        SetStatus($"已添加第 {world.walkPolygon.Count} 个顶点，继续点击或 Esc 结束。");
                        view.Repaint();
                    }
                    else
                    {
                        if (!world.spawnPlaced) throw new InvalidOperationException("先设置出生地。");
                        if (!LYNOOKWorldStudioService.ValidStandingPoint(world, hit.point, out string reason)) throw new InvalidOperationException(reason);
                        if (!LYNOOKWorldStudioService.DirectPathClear(world, world.avatarSpawn.position, hit.point)) throw new InvalidOperationException("出生地到该位置存在障碍。");
                        var point = new GameObject("stand_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                        point.transform.SetParent(world.activityPoints, false);
                        point.transform.position = hit.point;
                        point.transform.rotation = world.avatarSpawn.rotation;
                        Undo.RegisterCreatedObjectUndo(point, "Add activity point");
                        pickingMode = 0; Rebuild(); SetStatus("活动点已添加。");
                    }
                    if (pickingMode != 4) LYNOOKWorldStudioService.Changed(world);
                });
            }
            if (current.type == EventType.MouseMove) view.Repaint();
        }
    }
}
#endif
