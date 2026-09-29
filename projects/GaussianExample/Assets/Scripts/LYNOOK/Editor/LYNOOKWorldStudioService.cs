#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKWorldStudioService
    {
        public const string WorkspaceRoot = "Assets/LYNOOK/Worlds";
        /// <summary>批量录制下载资产的临时目录根（系统临时目录下的 lynook_batch）。</summary>
        public static string BatchTempRoot => Path.Combine(Path.GetTempPath(), "lynook_batch");
        const string RigPrefab = "Assets/LYNOOK/DualScreenRecorder/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string ShaderRoot = "Packages/org.nesnausk.gaussian-splatting/Shaders/";

        public static LYNOOKWorldAuthoring Current => SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<LYNOOKWorldAuthoring>(true)).SingleOrDefault();

        public static LYNOOKWorldAuthoring Import(string gaussianFile, string glbFile, string title, string roomType, bool highQuality)
        {
            RequireFile(gaussianFile, ".ply", ".spz");
            RequireFile(glbFile, ".glb");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab);
            if (prefab == null) throw new InvalidOperationException("缺少双屏相机模板。请保留现有 DualScreenRecorder 资源。");
            string id = "world_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string folder = WorkspaceRoot + "/" + id;
            Directory.CreateDirectory(folder + "/Source");
            Directory.CreateDirectory(folder + "/Gaussian");
            string sourceGaussian = folder + "/Source/room" + Path.GetExtension(gaussianFile).ToLowerInvariant();
            string sourceGlb = folder + "/Source/collision.glb";
            File.Copy(gaussianFile, sourceGaussian);
            File.Copy(glbFile, sourceGlb);
            // 与 GaussianSplatAssetCreator.LoadJsonCamerasFile 行为一致：从源文件所在目录
            // 向上递归找 cameras.json，保证 INRIA paper 数据集这类 cameras.json 在父目录的
            // 布局也能被归档复制并写入 GaussianSplatAsset 的 cameras 子资产。
            string camerasJson = FindCamerasJson(gaussianFile);
            if (!string.IsNullOrEmpty(camerasJson)) File.Copy(camerasJson, folder + "/Source/cameras.json");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var collisionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourceGlb);
            if (collisionPrefab == null) throw new InvalidOperationException("GLB 导入失败，请查看 Console。原始文件已保存在 " + folder);
            if (!collisionPrefab.GetComponentsInChildren<MeshFilter>(true).Any(m => m.sharedMesh != null))
                throw new InvalidOperationException("GLB 没有可用于地面点击的网格。");
            GaussianSplatAsset asset = GaussianSplatAssetCreator.ImportFile(sourceGaussian, folder + "/Gaussian", highQuality);
            // ImportFile 内部已经 Refresh+SaveAssets，但 AssetDatabase 的内存缓存有时还没把
            // room_pos.bytes / room_oth.bytes / room_col.bytes / room_shs.bytes / room_chk.bytes
            // 解析为 TextAsset 实例，导致 GaussianSplatAsset 实例的 posData/otherData/... 引用
            // 在内存里是 null（磁盘上 .asset YAML 引用是对的）。直接交给 GaussianSplatRenderer
            // 就会触发「asset is not assigned or is empty」警告，要用户手动重开场景才能恢复。
            // 这里强制同步重导整个 Gaussian 文件夹，让 AssetDatabase 把所有 .bytes 实际解析为
            // TextAsset 实例后再加载 GaussianSplatAsset，确保 HasValidAsset == true 才继续。
            string gaussianFolder = folder + "/Gaussian";
            string baseName = Path.GetFileNameWithoutExtension(sourceGaussian);
            string assetPath = gaussianFolder + "/" + baseName + ".asset";
            string[] subAssetFiles =
            {
                gaussianFolder + "/" + baseName + "_chk.bytes",
                gaussianFolder + "/" + baseName + "_pos.bytes",
                gaussianFolder + "/" + baseName + "_oth.bytes",
                gaussianFolder + "/" + baseName + "_col.bytes",
                gaussianFolder + "/" + baseName + "_shs.bytes",
            };
            // 先把所有子资产 .bytes 同步导入为 TextAsset，再导入主 .asset——主资产导入时
            // 才能把这些子资产引用解析到内存中的 TextAsset 实例。
            foreach (var sub in subAssetFiles)
                if (File.Exists(sub))
                    AssetDatabase.ImportAsset(sub, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
            if (asset == null) throw new InvalidOperationException("无法加载高斯资产: " + assetPath);
            // 若仍有子资产为 null，做一次全局 Refresh 兜底再重新加载。
            if (asset.posData == null || asset.otherData == null || asset.colorData == null || asset.shData == null)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                foreach (var sub in subAssetFiles)
                    if (File.Exists(sub))
                        AssetDatabase.ImportAsset(sub, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
            }
            if (asset == null || asset.splatCount == 0)
                throw new InvalidOperationException("高斯资产为空或未含 splat 数据: " + assetPath);
            if (asset.posData == null || asset.otherData == null || asset.colorData == null || asset.shData == null)
                throw new InvalidOperationException(
                    "高斯子资产未就绪: pos=" + (asset.posData != null) +
                    " other=" + (asset.otherData != null) +
                    " color=" + (asset.colorData != null) +
                    " sh=" + (asset.shData != null) +
                    "。请重试或检查 " + assetPath + " 的导入状态。");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("LYNOOK World — " + (string.IsNullOrWhiteSpace(title) ? id : title.Trim()));
            var world = host.AddComponent<LYNOOKWorldAuthoring>();
            world.worldId = id;
            world.roomType = string.IsNullOrWhiteSpace(roomType) ? "bedroom" : roomType.Trim();
            world.displayName = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(gaussianFile) : title.Trim();
            world.workspacePath = folder;
            world.gaussianSourcePath = sourceGaussian;
            world.collisionSourcePath = sourceGlb;
            world.gaussianSha256 = Sha256(sourceGaussian);
            world.collisionSha256 = Sha256(sourceGlb);
            world.coordinateRoot = Child(host.transform, "Room Coordinates");
            world.collisionObject = (GameObject)PrefabUtility.InstantiatePrefab(collisionPrefab, scene);
            world.collisionObject.transform.SetParent(world.coordinateRoot, false);
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                var collider = filter.GetComponent<MeshCollider>();
                if (collider == null) collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
            }

            var splatObject = Child(world.coordinateRoot, "Gaussian Visual").gameObject;
            // SPZ/PLY 源数据与 Unity 左手系在 Z 轴方向相反：高斯数据朝 -Z，Unity 默认朝 +Z。
            // 直接挂 identity transform 会看到镜像翻转的房间。沿 Z 轴 scale = -1 翻回正确朝向，
            // 与原版 sample 场景里手动给 GaussianSplatRenderer 加的纠正变换保持一致。
            splatObject.transform.localScale = new Vector3(1f, 1f, -1f);
            splatObject.SetActive(false);
            var gaussian = splatObject.AddComponent<GaussianSplatRenderer>();
            gaussian.m_Asset = asset;
            gaussian.m_ShaderSplats = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "RenderGaussianSplats.shader");
            gaussian.m_ShaderComposite = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "GaussianComposite.shader");
            gaussian.m_ShaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "GaussianDebugRenderPoints.shader");
            gaussian.m_ShaderDebugBoxes = AssetDatabase.LoadAssetAtPath<Shader>(ShaderRoot + "GaussianDebugRenderBoxes.shader");
            gaussian.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "SplatUtilities.compute");
            world.gaussian = gaussian;
            splatObject.SetActive(true);

            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            PrefabUtility.UnpackPrefabInstance(rigObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            rigObject.transform.SetParent(host.transform, false);
            var rig = rigObject.GetComponent<LYNOOKDualCameraRig>();
            if (rigObject.GetComponent<CameraRigTransformCopy>() == null) rigObject.AddComponent<CameraRigTransformCopy>();
            world.cameraRig = rig;
            var mainTexture = Object.Instantiate(rig.MainCaptureTexture);
            var sideTexture = Object.Instantiate(rig.SideCaptureTexture);
            AssetDatabase.CreateAsset(mainTexture, folder + "/Main.renderTexture");
            AssetDatabase.CreateAsset(sideTexture, folder + "/Side.renderTexture");
            rig.SetReferences(rig.CaptureRig, rig.MainCaptureCamera, rig.SideCaptureCamera, mainTexture, sideTexture);
            var session = rigObject.GetComponent<LYNOOKDualRecordingSession>();
            if (session == null) session = rigObject.AddComponent<LYNOOKDualRecordingSession>();
            session.SetReferences(rig, null);
            session.SetOutputNames("main", "right", "preview.mov");

            world.avatarSpawn = Child(world.coordinateRoot, "Avatar Spawn");
            world.activityPoints = Child(world.coordinateRoot, "Activity Points");
            var export = host.AddComponent<LYNOOKWorldExportSettings>();
            export.worldCoordinateRoot = world.coordinateRoot;
            export.displayName = world.displayName;
            export.gridMap = "";
            var bounds = CollisionBounds(world);
            try
            {
                // 优先扫描 GLB 近水平地面并拟合真实轮廓多边形，得到真实可走范围。
                AutoDetectFloor(world);
            }
            catch (Exception ex)
            {
                // 无法自动识别（如没有水平地面）时回退到整体包围盒底部。
                Debug.LogWarning($"[LYNOOK] 自动识别地面失败，回退到包围盒底部：{ex.Message}");
                world.floorHeight = world.coordinateRoot.InverseTransformPoint(bounds.min).y;
                world.walkCenter = new Vector2(bounds.center.x, bounds.center.z);
                world.walkSize = new Vector2(Mathf.Min(4, bounds.size.x), Mathf.Min(4, bounds.size.z));
            }
            rig.CaptureRig.position = bounds.center - Vector3.forward * Mathf.Max(3, bounds.size.magnitude * 0.7f);
            rig.CaptureRig.LookAt(bounds.center);
            rig.ApplyConfiguration();
            SetCollisionVisible(world, false);
            string scenePath = folder + "/Room.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new IOException("无法保存制作场景。");
            // ── fake-null 修复 ──
            // ImportFile 返回的 GaussianSplatAsset 实例 C# 字段都正确，但 Unity native side 还没
            // 加载好，所以 m_Asset != null（UnityEngine.Object 重载的 ==）返回 false，导致
            // HasValidAsset 一直 false、渲染不出。这里在 SaveScene 之后强制刷新 AssetDatabase、
            // 等 RefreshV2 把所有 pending import 完成后，重新 LoadAssetAtPath 拿一个 native side
            // 已加载好的实例，再赋值给 renderer。这一步要在 SaveScene 之后做，因为 SaveScene 触发
            // 的 Room.unity 导入也会进入 AssetDatabase 队列，必须等它一起完成才能保证 native side 一致。
            // 详见 docs/lynook-world-studio.md「已知问题与修复记录」。
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            GaussianSplatAsset reloadedAsset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
            if (reloadedAsset != null && reloadedAsset.posData != null && reloadedAsset.otherData != null && reloadedAsset.colorData != null && reloadedAsset.shData != null)
            {
                // m_PrevAsset 仍是 null，下一个 Update 帧会检测到 m_PrevAsset != m_Asset 并触发
                // DisposeResourcesForAsset + CreateResourcesForAsset，自动重建 GPU 缓冲。
                gaussian.m_Asset = reloadedAsset;
            }
            else
            {
                // 极少数情况下 Refresh 后 native side 仍未就绪；用户在 Inspector 中点 Render Mode
                // 下拉框可触发 Unity 重新解析引用，等同于本路径的兜底手动版。
                Debug.LogWarning($"[LYNOOK] Refresh 后 asset 仍 fake-null 或子资产缺失，将通过 inspector 触发延迟加载。reloadedAsset != null = {reloadedAsset != null}, posData={(reloadedAsset?.posData != null ? reloadedAsset.posData.name : "null")}");
            }
            SaveDraft(world);
            Selection.activeGameObject = host;
            Frame(world);
            return world;
        }

        static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        static void RequireFile(string path, params string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("请选择有效的本地文件。", path);
            if (!extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new ArgumentException("不支持的文件类型：" + path);
            if (new FileInfo(path).Length == 0) throw new ArgumentException("文件为空：" + path);
        }

        /// <summary>
        /// 从输入文件所在目录开始向上递归查找 cameras.json，与
        /// GaussianSplatAssetCreator.LoadJsonCamerasFile 的查找范围保持一致，避免
        /// cameras.json 在父目录的数据集（如 INRIA paper 布局）漏拷。
        /// </summary>
        static string FindCamerasJson(string inputPath)
        {
            string current = inputPath;
            while (true)
            {
                string dir = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                string candidate = Path.Combine(dir, "cameras.json");
                if (File.Exists(candidate)) return candidate;
                string full = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(full);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return null;
                current = dir;
            }
        }

        public static string Sha256(string path)
        {
            using var hash = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static Bounds CollisionBounds(LYNOOKWorldAuthoring world)
        {
            var renderers = world.collisionObject.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("GLB 没有可显示的网格。");
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        // 单个地面三角面（房间局部坐标 XZ 的三个顶点 + 世界空间高度）
        struct FloorTri
        {
            public Vector2 a, b, c;
            public float worldY;
            public float area;
        }

        /// <summary>
        /// 扫描 GLB 碰撞网格，找出面积最大的近水平地面，自动设置
        /// <see cref="LYNOOKWorldAuthoring.floorHeight"/> 并把
        /// <see cref="LYNOOKWorldAuthoring.walkPolygon"/> 拟合为地面真实轮廓（任意简单多边形）。
        /// 同时保留 walkCenter/walkSize 作为多边形边界盒回退。
        /// </summary>
        public static void AutoDetectFloor(LYNOOKWorldAuthoring world)
        {
            if (world.coordinateRoot == null || world.collisionObject == null)
                throw new InvalidOperationException("缺少房间坐标或 GLB。");

            Vector3 up = world.coordinateRoot.up;
            float cosThreshold = Mathf.Cos(12f * Mathf.Deg2Rad); // 法线与上方向夹角 ≤12° 视为地面
            float heightTolerance = Mathf.Max(world.floorTolerance * 2f, 0.2f);

            // 收集所有近水平三角面
            var floorTris = new List<FloorTri>();
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts = mesh.vertices;
                int[] tris = mesh.triangles;
                Transform tr = filter.transform;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 wa = tr.TransformPoint(verts[tris[i]]);
                    Vector3 wb = tr.TransformPoint(verts[tris[i + 1]]);
                    Vector3 wc = tr.TransformPoint(verts[tris[i + 2]]);
                    Vector3 n = Vector3.Cross(wb - wa, wc - wa);
                    float area2 = n.magnitude;
                    if (area2 < 1e-8f) continue;
                    if (Vector3.Dot(n / area2, up) < cosThreshold) continue;
                    Vector3 la = world.coordinateRoot.InverseTransformPoint(wa);
                    Vector3 lb = world.coordinateRoot.InverseTransformPoint(wb);
                    Vector3 lc = world.coordinateRoot.InverseTransformPoint(wc);
                    floorTris.Add(new FloorTri
                    {
                        a = new Vector2(la.x, la.z),
                        b = new Vector2(lb.x, lb.z),
                        c = new Vector2(lc.x, lc.z),
                        worldY = (wa.y + wb.y + wc.y) / 3f,
                        area = area2 * 0.5f
                    });
                }
            }

            if (floorTris.Count == 0)
                throw new InvalidOperationException("GLB 中没有检测到接近水平的地面三角面。");

            // 按高度做面积加权直方图，选出占主导的地面高度层
            float minY = floorTris[0].worldY, maxY = minY;
            foreach (var t in floorTris) { if (t.worldY < minY) minY = t.worldY; if (t.worldY > maxY) maxY = t.worldY; }
            int binCount = Mathf.Max(1, Mathf.CeilToInt((maxY - minY) / heightTolerance) + 1);
            float[] areaByBin = new float[binCount];
            float[] sumYByBin = new float[binCount];
            float[] sumAreaByBin = new float[binCount];
            foreach (var t in floorTris)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt((t.worldY - minY) / heightTolerance), 0, binCount - 1);
                areaByBin[bin] += t.area;
                sumYByBin[bin] += t.worldY * t.area;
                sumAreaByBin[bin] += t.area;
            }
            int bestBin = 0;
            for (int i = 1; i < binCount; i++) if (areaByBin[i] > areaByBin[bestBin]) bestBin = i;
            float floorWorldY = sumYByBin[bestBin] / sumAreaByBin[bestBin];

            // 只保留主导高度层的地面三角面
            var mainFloor = floorTris.Where(t => Mathf.Abs(t.worldY - floorWorldY) <= heightTolerance).ToList();
            if (mainFloor.Count == 0) throw new InvalidOperationException("地面范围计算失败。");

            // 面积加权平均得到房间局部坐标下的地面高度
            float sumLocalY = 0f, sumArea = 0f;
            foreach (var t in mainFloor)
            {
                float ly = world.coordinateRoot.InverseTransformPoint(new Vector3(0, t.worldY, 0)).y;
                sumLocalY += ly * t.area;
                sumArea += t.area;
            }
            float floorLocalY = sumLocalY / sumArea;

            // 提取地面轮廓多边形
            var polygon = ExtractFloorPolygon(mainFloor, world.agentRadius);
            if (polygon.Count < 3) throw new InvalidOperationException("地面轮廓提取失败。");

            world.floorHeight = floorLocalY;
            world.walkPolygon = polygon;
            // 同步更新矩形边界盒作为回退表示
            world.WalkBounds(out var center, out var size);
            world.walkCenter = center;
            world.walkSize = size;
        }

        /// <summary>
        /// 从用户点击点出发提取地面：用点击 Y 作为地面高度（跳过直方图），
        /// 在房间局部高度容差内栅格化，以点击 X/Z 选取连通域及闭合轮廓。
        /// </summary>
        public static void AutoDetectFloorFromPoint(LYNOOKWorldAuthoring world, Vector3 hitWorldPoint)
        {
            if (world.coordinateRoot == null || world.collisionObject == null)
                throw new InvalidOperationException("缺少房间坐标或 GLB。");

            Vector3 up = world.coordinateRoot.up;
            // 放宽法线阈值到 25°，吸收颗粒侧面
            float cosThreshold = Mathf.Cos(25f * Mathf.Deg2Rad);
            // Use the configured tolerance; do not merge furniture half a metre above the click.
            float heightTolerance = Mathf.Max(world.floorTolerance, 0.02f);

            Vector3 seedLocal = world.coordinateRoot.InverseTransformPoint(hitWorldPoint);
            float floorLocalY = seedLocal.y;

            // 收集点击高度附近的近水平三角面
            var mainFloor = new List<FloorTri>();
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts = mesh.vertices;
                int[] tris = mesh.triangles;
                Transform tr = filter.transform;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 wa = tr.TransformPoint(verts[tris[i]]);
                    Vector3 wb = tr.TransformPoint(verts[tris[i + 1]]);
                    Vector3 wc = tr.TransformPoint(verts[tris[i + 2]]);
                    Vector3 n = Vector3.Cross(wb - wa, wc - wa);
                    float area2 = n.magnitude;
                    if (area2 < 1e-8f) continue;
                    if (Mathf.Abs(Vector3.Dot(n / area2, up)) < cosThreshold) continue;
                    Vector3 la = world.coordinateRoot.InverseTransformPoint(wa);
                    Vector3 lb = world.coordinateRoot.InverseTransformPoint(wb);
                    Vector3 lc = world.coordinateRoot.InverseTransformPoint(wc);
                    if (Mathf.Max(Mathf.Abs(la.y - floorLocalY), Mathf.Abs(lb.y - floorLocalY), Mathf.Abs(lc.y - floorLocalY)) > heightTolerance) continue;
                    mainFloor.Add(new FloorTri
                    {
                        a = new Vector2(la.x, la.z),
                        b = new Vector2(lb.x, lb.z),
                        c = new Vector2(lc.x, lc.z),
                        worldY = (wa.y + wb.y + wc.y) / 3f,
                        area = area2 * 0.5f
                    });
                }
            }

            if (mainFloor.Count == 0)
                throw new InvalidOperationException("点击位置附近没有检测到地面三角面。");

            // The click's X/Z selects the connected component, never the largest island.
            // This is the selected floor footprint; agent clearance is a separate operation.
            var polygon = ExtractFloorPolygon(mainFloor, 0, new Vector2(seedLocal.x, seedLocal.z));
            if (polygon.Count < 3)
                throw new InvalidOperationException("点击点所在的地面区域无法形成闭合轮廓；原范围保持不变。");

            world.floorHeight = floorLocalY;
            world.walkPolygon = polygon;
            world.WalkBounds(out var center2, out var size2);
            world.walkCenter = center2;
            world.walkSize = size2;
        }

        /// <summary>
        /// 从地面三角面集合提取 2D 轮廓多边形（房间局部 XZ）：
        /// 栅格化 → 最大连通域 → marching squares 等高线 → Douglas-Peucker 简化。
        /// </summary>
        static List<Vector2> ExtractFloorPolygon(List<FloorTri> tris, float margin, Vector2? seed = null)
        {
            // 1. 计算 XZ 包围盒
            float minX = tris[0].a.x, maxX = minX, minZ = tris[0].a.y, maxZ = minZ;
            void Enc(Vector2 p)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }
            foreach (var t in tris) { Enc(t.a); Enc(t.b); Enc(t.c); }

            float cell = 0.08f; // 栅格分辨率，平衡精度与性能
            // Empty border closes contours that touch the source mesh bounds.
            minX -= cell * 2; minZ -= cell * 2;
            maxX += cell * 2; maxZ += cell * 2;
            int cols = Mathf.CeilToInt((maxX - minX) / cell) + 1;
            int rows = Mathf.CeilToInt((maxZ - minZ) / cell) + 1;
            if ((long)cols * rows > 4000000)
                throw new InvalidOperationException("地面范围过大，请检查 GLB 尺度。");

            // 2. 栅格化：每个网格中心若落在任一地面三角面内则占用
            var occ = new bool[cols, rows];
            foreach (var t in tris)
            {
                float tx0 = Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x));
                float tx1 = Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x));
                float tz0 = Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y));
                float tz1 = Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y));
                int cx0 = Mathf.Clamp(Mathf.FloorToInt((tx0 - minX) / cell), 0, cols - 1);
                int cx1 = Mathf.Clamp(Mathf.FloorToInt((tx1 - minX) / cell), 0, cols - 1);
                int cz0 = Mathf.Clamp(Mathf.FloorToInt((tz0 - minZ) / cell), 0, rows - 1);
                int cz1 = Mathf.Clamp(Mathf.FloorToInt((tz1 - minZ) / cell), 0, rows - 1);
                for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    if (occ[cx, cz]) continue;
                    Vector2 p = new Vector2(minX + (cx + 0.5f) * cell, minZ + (cz + 0.5f) * cell);
                    if (PointInTriangle(p, t.a, t.b, t.c)) occ[cx, cz] = true;
                }
            }

            // 3. 找最大连通域（4 邻接 flood fill），并剔除孤立小面积
            int[] labels = new int[cols * rows];
            int labelCount = 0;
            var regionSize = new List<int>();
            for (int cx = 0; cx < cols; cx++)
            for (int cz = 0; cz < rows; cz++)
            {
                if (!occ[cx, cz] || labels[cz * cols + cx] != 0) continue;
                labelCount++;
                int size = 0;
                var stack = new Stack<(int x, int z)>();
                stack.Push((cx, cz));
                labels[cz * cols + cx] = labelCount;
                while (stack.Count > 0)
                {
                    var (x, z) = stack.Pop();
                    size++;
                    if (x > 0 && occ[x - 1, z] && labels[z * cols + x - 1] == 0) { labels[z * cols + x - 1] = labelCount; stack.Push((x - 1, z)); }
                    if (x < cols - 1 && occ[x + 1, z] && labels[z * cols + x + 1] == 0) { labels[z * cols + x + 1] = labelCount; stack.Push((x + 1, z)); }
                    if (z > 0 && occ[x, z - 1] && labels[(z - 1) * cols + x] == 0) { labels[(z - 1) * cols + x] = labelCount; stack.Push((x, z - 1)); }
                    if (z < rows - 1 && occ[x, z + 1] && labels[(z + 1) * cols + x] == 0) { labels[(z + 1) * cols + x] = labelCount; stack.Push((x, z + 1)); }
                }
                regionSize.Add(size);
            }
            if (labelCount == 0) return new List<Vector2>();
            int bestLabel = 1, bestSize = regionSize[0];
            for (int i = 1; i < regionSize.Count; i++)
                if (regionSize[i] > bestSize) { bestSize = regionSize[i]; bestLabel = i + 1; }

            if (seed.HasValue)
            {
                int sx = Mathf.FloorToInt((seed.Value.x - minX) / cell);
                int sz = Mathf.FloorToInt((seed.Value.y - minZ) / cell);
                if (sx < 0 || sx >= cols || sz < 0 || sz >= rows || labels[sz * cols + sx] == 0)
                    throw new InvalidOperationException("点击点不在可提取地面栅格内，请稍向地面内部点击。");
                bestLabel = labels[sz * cols + sx];
            }

            // 4. marching squares 提取最大连通域的外轮廓
            var segments = new List<(Vector2 a, Vector2 b)>();
            for (int cx = 0; cx < cols - 1; cx++)
            for (int cz = 0; cz < rows - 1; cz++)
            {
                int c00 = (labels[cz * cols + cx] == bestLabel) ? 1 : 0;
                int c10 = (labels[cz * cols + cx + 1] == bestLabel) ? 1 : 0;
                int c01 = (labels[(cz + 1) * cols + cx] == bestLabel) ? 1 : 0;
                int c11 = (labels[(cz + 1) * cols + cx + 1] == bestLabel) ? 1 : 0;
                int code = c00 | (c10 << 1) | (c01 << 2) | (c11 << 3);
                Vector2 bl = new Vector2(minX + (cx + 0.5f) * cell, minZ + (cz + 0.5f) * cell);
                Vector2 br = bl + new Vector2(cell, 0);
                Vector2 tl = bl + new Vector2(0, cell);
                Vector2 tr = bl + new Vector2(cell, cell);
                Vector2 mb = (bl + br) * 0.5f, ml = (bl + tl) * 0.5f, mr = (br + tr) * 0.5f, mt = (tl + tr) * 0.5f;
                switch (code)
                {
                    case 0: case 15: break;
                    case 1: segments.Add((mb, ml)); break;
                    case 2: segments.Add((mr, mb)); break;
                    case 3: segments.Add((mr, ml)); break;
                    case 4: segments.Add((ml, mt)); break;
                    case 5: segments.Add((mb, mt)); break; // saddle: pick one diagonal
                    case 6: segments.Add((ml, mt)); segments.Add((mr, mb)); break;
                    case 7: segments.Add((mt, mr)); break;
                    case 8: segments.Add((mt, mr)); break;
                    case 9: segments.Add((mb, ml)); segments.Add((mt, mr)); break;
                    case 10: segments.Add((mb, mt)); break; // saddle
                    case 11: segments.Add((mt, ml)); break;
                    case 12: segments.Add((mr, ml)); break;
                    case 13: segments.Add((mr, mb)); break;
                    case 14: segments.Add((mb, ml)); break;
                }
            }

            // 5. 把线段串成闭合轮廓
            var contour = ChainSegments(segments, seed);
            if (contour.Count < 3) return new List<Vector2>();

            // 6. Douglas-Peucker 简化
            float simplifyTol = cell * 1.5f;
            var simplified = DouglasPeucker(contour, simplifyTol);
            if (seed.HasValue && !LYNOOKWorldAuthoring.PointInPolygon(seed.Value, simplified))
                simplified = contour;

            // 7. 内缩 margin，避免角色贴墙（把多边形整体向质心收缩一小步的近似：
            //    这里直接对每个顶点沿角平分线内移；若退化则回退原轮廓）
            var inset = InsetPolygon(simplified, margin);
            return inset.Count >= 3 ? inset : simplified;
        }

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // 把无序列表线段按端点匹配串成一个闭合折线
        static List<Vector2> ChainSegments(List<(Vector2 a, Vector2 b)> segs, Vector2? seed = null)
        {
            var remaining = new LinkedList<(Vector2 a, Vector2 b)>(segs);
            var best = new List<Vector2>();
            float bestArea = 0;
            const float eps2 = 1e-8f;
            while (remaining.Count > 0)
            {
                var first = remaining.First.Value;
                remaining.RemoveFirst();
                var loop = new List<Vector2> { first.a };
                Vector2 head = first.b;
                while ((head - first.a).sqrMagnitude > eps2)
                {
                    loop.Add(head);
                    var node = remaining.First;
                    while (node != null && (node.Value.a - head).sqrMagnitude > eps2 &&
                           (node.Value.b - head).sqrMagnitude > eps2) node = node.Next;
                    if (node == null) break;
                    head = (node.Value.a - head).sqrMagnitude <= eps2 ? node.Value.b : node.Value.a;
                    remaining.Remove(node);
                }
                if ((head - first.a).sqrMagnitude > eps2 || loop.Count < 3) continue;
                if (seed.HasValue && !LYNOOKWorldAuthoring.PointInPolygon(seed.Value, loop)) continue;
                float area = 0;
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2 p = loop[i], q = loop[(i + 1) % loop.Count];
                    area += p.x * q.y - q.x * p.y;
                }
                if (Mathf.Abs(area) > bestArea) { best = loop; bestArea = Mathf.Abs(area); }
            }
            return best;
        }

        static List<Vector2> DouglasPeucker(List<Vector2> points, float epsilon)
        {
            if (points.Count < 3) return new List<Vector2>(points);
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var stack = new Stack<(int lo, int hi)>();
            stack.Push((0, points.Count - 1));
            while (stack.Count > 0)
            {
                var (lo, hi) = stack.Pop();
                if (hi - lo < 2) continue;
                float maxDist = 0; int index = -1;
                for (int i = lo + 1; i < hi; i++)
                {
                    float d = PerpDistance(points[i], points[lo], points[hi]);
                    if (d > maxDist) { maxDist = d; index = i; }
                }
                if (index >= 0 && maxDist > epsilon)
                {
                    keep[index] = true;
                    stack.Push((lo, index));
                    stack.Push((index, hi));
                }
            }
            var result = new List<Vector2>();
            for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
            return result;
        }

        static float PerpDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return (p - a).magnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }

        // 把多边形每个顶点沿内角平分线向内移动 distance，生成内缩多边形。
        // 若某顶点内缩后导致边自交/退化，保留原顶点。
        static List<Vector2> InsetPolygon(List<Vector2> poly, float distance)
        {
            int n = poly.Count;
            if (n < 3 || distance <= 0f) return new List<Vector2>(poly);
            var result = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = poly[(i - 1 + n) % n];
                Vector2 cur = poly[i];
                Vector2 next = poly[(i + 1) % n];
                Vector2 e1 = (cur - prev).normalized;
                Vector2 e2 = (next - cur).normalized;
                Vector2 bisect = (e1 - e2).normalized; // 指向多边形内部（逆时针时）
                // 根据面积符号判断朝向，修正内缩方向
                double area = 0;
                for (int k = 0; k < n; k++) area += poly[k].x * poly[(k + 1) % n].y - poly[(k + 1) % n].x * poly[k].y;
                if (area < 0) bisect = -bisect;
                Vector2 moved = cur + bisect * distance;
                result.Add(moved);
            }
            // 简单自交检测：若内缩后边数级别的自交则退回原多边形
            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = result[i], a2 = result[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (j == (i + 1) % n || i == (j + 1) % n) continue;
                    Vector2 b1 = result[j], b2 = result[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return new List<Vector2>(poly);
                }
            }
            return result;
        }

        static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
            float d1 = Cross(p4 - p3, p1 - p3);
            float d2 = Cross(p4 - p3, p2 - p3);
            float d3 = Cross(p2 - p1, p3 - p1);
            float d4 = Cross(p2 - p1, p4 - p1);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        public static void Frame(LYNOOKWorldAuthoring world)
        {
            var view = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            view.Frame(CollisionBounds(world), false);
            view.Show();
            view.Focus();
            SceneView.RepaintAll();
        }

        public static void SetCollisionVisible(LYNOOKWorldAuthoring world, bool visible)
        {
            world.showCollision = visible;
            foreach (var renderer in world.collisionObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = visible;
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            SceneView.RepaintAll();
        }

        public static bool Raycast(LYNOOKWorldAuthoring world, Ray ray, out RaycastHit nearest, float distance = 10000)
        {
            nearest = default;
            if (world == null || world.collisionObject == null) return false;
            bool found = false;
            Physics.SyncTransforms();
            // Imported room meshes can face outward. Query both sides when picking
            // from inside, without changing the project's global physics setting.
            bool previousBackfaces = Physics.queriesHitBackfaces;
            try
            {
                Physics.queriesHitBackfaces = true;
                foreach (var collider in world.collisionObject.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled || !collider.Raycast(ray, out var hit, distance)) continue;
                    distance = hit.distance;
                    if (Vector3.Dot(hit.normal, ray.direction) > 0) hit.normal = -hit.normal;
                    nearest = hit;
                    found = true;
                }
            }
            finally { Physics.queriesHitBackfaces = previousBackfaces; }
            return found;
        }

        // Do not look through walls or accept a wall as a fallback floor.
        public static bool RaycastPreferFloor(LYNOOKWorldAuthoring world, Ray ray, out RaycastHit best, float distance = 10000)
        {
            return Raycast(world, ray, out best, distance) &&
                Vector3.Dot(best.normal, world.coordinateRoot.up) >= Mathf.Cos(25f * Mathf.Deg2Rad);
        }

        public static bool ValidStandingPoint(LYNOOKWorldAuthoring world, Vector3 position, out string reason)
        {
            if (world.coordinateRoot == null || world.collisionObject == null) { reason = "缺少房间坐标或 GLB。"; return false; }
            Vector3 local = world.coordinateRoot.InverseTransformPoint(position);
            // 多边形模式下轮廓已内缩 agentRadius，直接判断点在范围内即可；
            // 矩形模式下仍需把边缘 agentRadius 排除。
            bool inside = world.HasWalkPolygon
                ? world.IsInsideWalkArea(new Vector2(local.x, local.z))
                : (Mathf.Abs(local.x - world.walkCenter.x) <= world.walkSize.x * 0.5f - world.agentRadius &&
                   Mathf.Abs(local.z - world.walkCenter.y) <= world.walkSize.y * 0.5f - world.agentRadius);
            if (!inside)
            { reason = "位置在可走范围外，或离边缘太近。"; return false; }
            if (Mathf.Abs(local.y - world.floorHeight) > world.floorTolerance)
            { reason = "位置不在已确认的地面高度附近。"; return false; }
            Vector3 up = world.coordinateRoot.up;
            float scale = world.coordinateRoot.lossyScale.x;
            float probe = (world.floorTolerance + 0.05f) * scale;
            if (!Raycast(world, new Ray(position + up * probe, -up), out var hit, probe * 2) ||
                Vector3.Dot(hit.normal, up) < 0.9f || Vector3.Distance(hit.point, position) > world.floorTolerance * scale)
            { reason = "GLB 中没有找到平坦的支撑地面。"; return false; }
            Capsule(world, position, out var bottom, out var top, out float radius);
            if (Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore)
                .Any(c => c.transform.IsChildOf(world.collisionObject.transform)))
            { reason = "角色站立空间与 GLB 障碍物重叠。"; return false; }
            reason = "可站立";
            return true;
        }

        static void Capsule(LYNOOKWorldAuthoring world, Vector3 foot, out Vector3 bottom, out Vector3 top, out float radius)
        {
            float scale = world.coordinateRoot.lossyScale.x;
            radius = world.agentRadius * scale;
            bottom = foot + world.coordinateRoot.up * (radius + 0.025f * scale);
            top = foot + world.coordinateRoot.up * (world.agentHeight * scale - radius);
        }

        public static bool DirectPathClear(LYNOOKWorldAuthoring world, Vector3 from, Vector3 to)
        {
            Capsule(world, from, out var bottom, out var top, out float radius);
            Vector3 delta = to - from;
            if (delta.magnitude > 0.001f && Physics.CapsuleCastAll(bottom, top, radius, delta.normalized,
                    delta.magnitude, ~0, QueryTriggerInteraction.Ignore)
                .Any(h => h.collider.transform.IsChildOf(world.collisionObject.transform))) return false;
            int steps = Mathf.CeilToInt(delta.magnitude / Mathf.Max(0.05f, radius));
            for (int i = 0; i <= steps; i++)
                if (!ValidStandingPoint(world, Vector3.Lerp(from, to, steps == 0 ? 0 : (float)i / steps), out _)) return false;
            return true;
        }

        public static void PlaceSpawn(LYNOOKWorldAuthoring world, RaycastHit hit)
        {
            if (!ValidStandingPoint(world, hit.point, out string reason)) throw new InvalidOperationException(reason);
            Undo.RecordObjects(new Object[] { world, world.avatarSpawn }, "Set avatar spawn");
            world.avatarSpawn.position = hit.point;
            world.spawnPlaced = true;
            Changed(world);
        }

        public static int GeneratePoints(LYNOOKWorldAuthoring world)
        {
            ValidateSettings(world);
            if (!world.spawnPlaced) throw new InvalidOperationException("请先点选出生地。");
            if (!ValidStandingPoint(world, world.avatarSpawn.position, out string reason)) throw new InvalidOperationException("出生地：" + reason);
            float scale = world.coordinateRoot.lossyScale.x;
            float minDist = world.pointSpacing * scale;
            world.WalkBounds(out var center, out var size);
            float halfX = size.x * 0.5f, halfZ = size.y * 0.5f;
            float minX = center.x - halfX, maxX = center.x + halfX;
            float minZ = center.y - halfZ, maxZ = center.y + halfZ;

            // 1. 在多边形边界盒内随机撒点，过滤出可站立且从出生地可达的候选点。
            //    尝试次数按面积与间距估算，保证大区域也能采到足够候选。
            int attempts = Mathf.Clamp(Mathf.CeilToInt(size.x * size.y / Mathf.Max(0.04f, world.pointSpacing * world.pointSpacing)) * 4, 200, 4000);
            var candidates = new List<Vector3>(attempts);
            var rng = new System.Random(Guid.NewGuid().GetHashCode());
            for (int i = 0; i < attempts; i++)
            {
                Vector2 localXZ = new Vector2(
                    (float)(minX + rng.NextDouble() * (maxX - minX)),
                    (float)(minZ + rng.NextDouble() * (maxZ - minZ)));
                if (!world.IsInsideWalkArea(localXZ)) continue;
                Vector3 local = new Vector3(localXZ.x, world.floorHeight + world.floorTolerance + 0.05f, localXZ.y);
                if (!Raycast(world, new Ray(world.coordinateRoot.TransformPoint(local), -world.coordinateRoot.up), out var hit,
                        (world.floorTolerance * 2 + 0.1f) * scale)) continue;
                if (Vector3.Distance(hit.point, world.avatarSpawn.position) < minDist) continue;
                if (!ValidStandingPoint(world, hit.point, out _)) continue;
                if (!DirectPathClear(world, world.avatarSpawn.position, hit.point)) continue;
                candidates.Add(hit.point);
            }

            // 2. 打乱候选点顺序，按最小间距贪心选择，保证位置随机且分布均匀。
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            var accepted = new List<Vector3>();
            foreach (var p in candidates)
            {
                bool tooClose = false;
                for (int k = 0; k < accepted.Count; k++)
                {
                    if (Vector3.Distance(p, accepted[k]) < minDist) { tooClose = true; break; }
                }
                if (tooClose) continue;
                accepted.Add(p);
                if (accepted.Count >= world.maximumPoints) break;
            }
            if (accepted.Count == 0) throw new InvalidOperationException("没有找到安全点位。请检查地面高度、活动范围和 GLB 对齐；原有点位已保留。");

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            for (int i = world.activityPoints.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(world.activityPoints.GetChild(i).gameObject);
            for (int i = 0; i < accepted.Count; i++)
            {
                Transform point = Child(world.activityPoints, "stand_" + (i + 1).ToString("D2"));
                point.position = accepted[i];
                point.rotation = world.avatarSpawn.rotation;
                var meta = point.gameObject.AddComponent<LYNOOKActivityPoint>();
                meta.type = "stand";
                Undo.RegisterCreatedObjectUndo(point.gameObject, "Generate activity points");
            }
            Undo.CollapseUndoOperations(group);
            Changed(world);
            return accepted.Count;
        }

        public static void ValidateSettings(LYNOOKWorldAuthoring world)
        {
            if (world == null || world.coordinateRoot == null || world.gaussian == null || world.collisionObject == null)
                throw new InvalidOperationException("请先导入高斯和 GLB。");
            Vector3 scale = world.coordinateRoot.lossyScale;
            if (!Finite(scale.x) || scale.x <= 0 || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                throw new InvalidOperationException("房间坐标必须使用正数等比缩放。");
            world.WalkBounds(out var center, out var size);
            bool walkValid = Finite(center.x) && Finite(center.y) && Finite(size.x) && Finite(size.y) && size.x > 0 && size.y > 0;
            if (!Finite(world.pointSpacing) || world.pointSpacing < 0.2f || world.pointSpacing > 10 ||
                !walkValid ||
                size.x / world.pointSpacing * (size.y / world.pointSpacing) > 2500 ||
                !Finite(world.agentRadius) || world.agentRadius < 0.05f || !Finite(world.agentHeight) || world.agentHeight < world.agentRadius * 2 + 0.05f ||
                !Finite(world.floorHeight) || !Finite(world.floorTolerance) || world.floorTolerance < 0.01f || world.floorTolerance > 1 ||
                world.maximumPoints < 1 || world.maximumPoints > 40)
                throw new InvalidOperationException("检查活动范围和角色尺寸：间距至少 0.2，采样数不超过 2500，点位数 1–40，角色高度需大于直径。");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static string ValidateForRecording(LYNOOKWorldAuthoring world)
        {
            ValidateSettings(world);
            if (!world.alignmentConfirmed) throw new InvalidOperationException("请先检查高斯与 GLB，并勾选已确认对齐。");
            if (!world.spawnPlaced || !ValidStandingPoint(world, world.avatarSpawn.position, out _)) throw new InvalidOperationException("请设置有效出生地。");
            var locations = new List<Vector3> { world.avatarSpawn.position };
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform point in world.activityPoints)
            {
                if (string.IsNullOrWhiteSpace(point.name) || !names.Add(point.name)) throw new InvalidOperationException("活动点名称必须非空且唯一。");
                if (!ValidStandingPoint(world, point.position, out string reason)) throw new InvalidOperationException(point.name + "：" + reason);
                if (locations.Any(p => !DirectPathClear(world, p, point.position))) throw new InvalidOperationException(point.name + " 与其他点位之间存在障碍或地面缺口。");
                locations.Add(point.position);
            }
            if (!world.cameraRig.TryValidate(out string report)) throw new InvalidOperationException(report);
            if (world.recordingSeconds < 1 || world.recordingSeconds > 120) throw new InvalidOperationException("录制时长应为 1–120 秒。");
            return "出生地及 " + (locations.Count - 1) + " 个活动点通过地面、净空和直线路径检查。双屏物理效果仍需设备验证。";
        }

        public static void Changed(LYNOOKWorldAuthoring world)
        {
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            SceneView.RepaintAll();
        }

        public static void SaveDraft(LYNOOKWorldAuthoring world)
        {
            if (world == null) throw new InvalidOperationException("没有制作场景。");
            if (!EditorSceneManager.SaveScene(world.gameObject.scene)) throw new IOException("制作场景未保存。");
            var payload = new Draft
            {
                worldId = world.worldId, roomType = world.roomType, displayName = world.displayName,
                gaussian = world.gaussianSourcePath.Substring(world.workspacePath.Length + 1),
                collision = world.collisionSourcePath.Substring(world.workspacePath.Length + 1),
                gaussianSha256 = world.gaussianSha256, collisionSha256 = world.collisionSha256,
                alignmentConfirmed = world.alignmentConfirmed, spawnPlaced = world.spawnPlaced,
                coordinates = Pose.From(world.coordinateRoot), gaussianTransform = Pose.From(world.gaussian.transform),
                cameraRig = Pose.From(world.cameraRig.CaptureRig), spawn = Pose.From(world.avatarSpawn),
                points = world.activityPoints.Cast<Transform>().Select(Pose.From).ToArray(),
                floorHeight = world.floorHeight, walkCenter = world.walkCenter, walkSize = world.walkSize,
                walkPolygon = world.walkPolygon, walkAreaThickness = world.walkAreaThickness,
                agentRadius = world.agentRadius, agentHeight = world.agentHeight, pointSpacing = world.pointSpacing,
                floorTolerance = world.floorTolerance, maximumPoints = world.maximumPoints, recordingSeconds = world.recordingSeconds,
                deployTargetPath = world.deployTargetPath
            };
            string path = world.workspacePath + "/world_draft.json";
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(payload, true));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            File.WriteAllText(world.workspacePath + "/camera_calibration.json", world.cameraRig.GetComponent<CameraRigTransformCopy>() != null
                ? world.cameraRig.GetComponent<CameraRigTransformCopy>().ToJson() : JsonUtility.ToJson(world.cameraRig, true));
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 一键发布：把当前房间的完整工作区（高斯资产、制作场景、配置等）复制到部署工程的目标路径下，
        /// 同时把最近一次录制产物（双屏视频、world_config.json 等）一并迁移过去。
        /// 目标路径应为另一个 Unity 工程的 Assets 目录（或其子目录）的绝对路径。
        /// </summary>
        public static string Deploy(LYNOOKWorldAuthoring world, string targetPath)
        {
            if (world == null) throw new InvalidOperationException("没有制作场景。");
            if (string.IsNullOrWhiteSpace(targetPath)) throw new InvalidOperationException("请先在「部署目标路径」中填写 Unity 部署工程的 Assets 目录。");
            targetPath = Environment.ExpandEnvironmentVariables(targetPath.Trim());
            if (!Path.IsPathRooted(targetPath))
                throw new InvalidOperationException("部署目标路径必须是绝对路径：" + targetPath);
            if (!Directory.Exists(targetPath))
                throw new InvalidOperationException("部署目标路径不存在，请先在部署工程中创建该目录：" + targetPath);

            string sourceDir = Path.GetFullPath(world.workspacePath);
            string targetRoot = Path.GetFullPath(targetPath);
            // 防止把工作区复制到自身内部（无限递归 / 自拷贝）。
            if (PathStartsWith(sourceDir, targetRoot))
                throw new InvalidOperationException("部署目标路径不能位于当前工程的房间工作区内部。");

            string destDir = Path.Combine(targetRoot, world.worldId);
            if (Directory.Exists(destDir))
            {
                if (!EditorUtility.DisplayDialog("发布到部署工程",
                    $"目标位置已存在同名房间目录：\n{destDir}\n\n是否覆盖？", "覆盖", "取消"))
                    throw new InvalidOperationException("用户取消发布。");
            }

            int fileCount = 0;
            long totalBytes = 0;
            try
            {
                EditorUtility.DisplayProgressBar("发布到部署工程", "正在复制房间工作区…", 0.1f);
                CopyDirectory(sourceDir, destDir, ref fileCount, ref totalBytes);

                // 同时迁移最近一次录制产物（双屏视频 + world_config.json + collision.glb + preview）
                string latestRecording = FindLatestRecording(world.worldId);
                string recordingDest = null;
                if (!string.IsNullOrEmpty(latestRecording))
                {
                    EditorUtility.DisplayProgressBar("发布到部署工程", "正在复制最近录制产物…", 0.7f);
                    recordingDest = Path.Combine(destDir, "Recording");
                    CopyDirectory(latestRecording, recordingDest, ref fileCount, ref totalBytes);
                }

                // 若目标在本工程 Assets 内，刷新一下；否则仅在目标工程打开时由其自行导入。
                string projectAssets = Path.GetFullPath("Assets");
                if (PathStartsWith(targetRoot, projectAssets))
                {
                    EditorUtility.DisplayProgressBar("发布到部署工程", "刷新 AssetDatabase…", 0.95f);
                    AssetDatabase.Refresh();
                }

                string sizeMB = (totalBytes / 1024.0 / 1024.0).ToString("F2");
                string extra = string.IsNullOrEmpty(recordingDest) ? "" : $"，并包含最近录制产物 {recordingDest}";
                return $"已发布 {fileCount} 个文件（约 {sizeMB} MB）到 {destDir}{extra}。请在部署工程中等待 Unity 导入完成。";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static bool PathStartsWith(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        static void CopyDirectory(string source, string dest, ref int fileCount, ref long totalBytes)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                string targetFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
                fileCount++;
                totalBytes += new FileInfo(targetFile).Length;
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), ref fileCount, ref totalBytes);
        }

        static string FindLatestRecording(string worldId)
        {
            string recordingsRoot = Path.GetFullPath("Recordings/LYNOOK/WorldStudio");
            if (!Directory.Exists(recordingsRoot)) return null;
            string prefix = worldId + "_";
            string latest = null;
            DateTime latestTime = DateTime.MinValue;
            foreach (var dir in Directory.GetDirectories(recordingsRoot))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var time = Directory.GetCreationTimeUtc(dir);
                if (time > latestTime) { latestTime = time; latest = dir; }
            }
            return latest;
        }

        [Serializable] public sealed class Pose
        {
            public string name;
            public Vector3 position, rotationEuler, scale;
            public static Pose From(Transform value) => new Pose { name = value.name, position = value.localPosition, rotationEuler = value.localEulerAngles, scale = value.localScale };
        }

        // ---- 场景队列 / 批量录制 ----

        /// <summary>数据库 scenes 表中一条待转换（待录制）场景。</summary>
        [Serializable] public sealed class SceneQueueItem
        {
            public int id;
            public string name, prompt, model, status, convertStatus, convertTaskId, roomType;
            public string operationId, providerSceneId, marbleUrl, thumbnailUrl;
            public string colliderMeshUrl, splatUrl, panoUrl, hdrPanoUrl;
            public string worldJsonUrl, previewVideoUrl;
            public string error, convertError;
            public string createdAt, convertQueuedAt;
            public string ownerEmail, ownerUsername, ownerName;
            // spzUrls 是 JSON 对象，JsonUtility 不支持字典；保留原始字符串供后续解析。
            public string spzUrls;
        }

        [Serializable] sealed class SceneQueueResult
        {
            public SceneQueueItem[] rows;
        }

        /// <summary>
        /// 拉取待转换（convert_status = pending）的场景列表。
        /// 通过本机 Tools/scene-queue-json.mjs 查询 Postgres，返回结果不做认领，仅展示。
        /// </summary>
        public static SceneQueueItem[] FetchSceneQueue(int limit = 50)
        {
            string scriptPath = ResolveSceneQueueScript();
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("找不到 Tools/scene-queue-json.mjs，请确认仓库根目录。");
            string nodePath = LocateNode();
            var start = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{scriptPath}\" --limit={limit}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("无法启动 node 进程。");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"查询场景队列失败（exit {process.ExitCode}）：{stderr.Trim()}");
                if (string.IsNullOrWhiteSpace(stdout))
                    throw new InvalidOperationException("场景队列查询无输出：" + stderr.Trim());
                // 取出首个 { 到末尾，避免 dotenv/启动信息污染。
                int brace = stdout.IndexOf('{');
                if (brace < 0) throw new InvalidOperationException("场景队列输出不是 JSON：" + stdout.Trim());
                string json = stdout.Substring(brace);
                var result = JsonUtility.FromJson<SceneQueueResult>(json);
                if (result == null || result.rows == null)
                    throw new InvalidOperationException("场景队列 JSON 解析失败。");
                return result.rows;
            }
        }

        static string ResolveSceneQueueScript()
        {
            // GaussianExample 工程位于 <repo>/projects/GaussianExample，Tools 在 <repo>/Tools。
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "Tools", "scene-queue-json.mjs")),
                Path.GetFullPath(Path.Combine(projectRoot, "Tools", "scene-queue-json.mjs")),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        static string LocateNode()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir, "node");
                if (File.Exists(candidate)) return candidate;
            }
            // macOS 常见位置兜底
            foreach (var fallback in new[] { "/usr/local/bin/node", "/opt/homebrew/bin/node", "/usr/bin/node" })
                if (File.Exists(fallback)) return fallback;
            throw new InvalidOperationException("未找到 node 可执行文件，请确认 PATH 或安装 Node.js。");
        }

        // ---- 批量录制辅助 ----

        /// <summary>运行一个 Tools 下的 node 脚本，解析 stdout 的 JSON。</summary>
        static T RunNodeScript<T>(string scriptName, string arguments) where T : class
        {
            string scriptPath = ResolveToolScript(scriptName);
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("找不到脚本 Tools/" + scriptName);
            var start = new ProcessStartInfo
            {
                FileName = LocateNode(),
                Arguments = $"\"{scriptPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("无法启动 node 进程。");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"脚本 {scriptName} 失败（exit {process.ExitCode}）：{stderr.Trim()}");
                int brace = stdout.IndexOf('{');
                if (brace < 0) throw new InvalidOperationException($"{scriptName} 输出不是 JSON：{stdout.Trim()}");
                string json = stdout.Substring(brace);
                var result = JsonUtility.FromJson<T>(json);
                if (result == null) throw new InvalidOperationException($"{scriptName} JSON 解析失败。");
                return result;
            }
        }

        /// <summary>
        /// 异步运行 node 脚本，不阻塞 Unity 主线程。完成后通过 callback 在主线程回调。
        /// callback 参数：(result, error) —— 成功时 error 为 null，失败时 result 为 null。
        /// </summary>
        static void RunNodeScriptAsync<T>(string scriptName, string arguments, Action<T, string> callback) where T : class
        {
            string scriptPath = ResolveToolScript(scriptName);
            if (string.IsNullOrEmpty(scriptPath))
            {
                callback(null, "找不到脚本 Tools/" + scriptName);
                return;
            }
            var start = new ProcessStartInfo
            {
                FileName = LocateNode(),
                Arguments = $"\"{scriptPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var stdoutBuf = new System.Text.StringBuilder();
            var stderrBuf = new System.Text.StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuf.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
            process.Exited += (sender, e) =>
            {
                try
                {
                    string stdout = stdoutBuf.ToString();
                    string stderr = stderrBuf.ToString();
                    if (process.ExitCode != 0)
                    {
                        string err = $"脚本 {scriptName} 失败（exit {process.ExitCode}）：{stderr.Trim()}";
                        EditorApplication.delayCall += () => callback(null, err);
                        return;
                    }
                    int brace = stdout.IndexOf('{');
                    if (brace < 0)
                    {
                        EditorApplication.delayCall += () => callback(null, $"{scriptName} 输出不是 JSON：{stdout.Trim()}");
                        return;
                    }
                    string json = stdout.Substring(brace);
                    var result = JsonUtility.FromJson<T>(json);
                    if (result == null)
                    {
                        EditorApplication.delayCall += () => callback(null, $"{scriptName} JSON 解析失败。");
                        return;
                    }
                    EditorApplication.delayCall += () => callback(result, null);
                }
                finally { process.Dispose(); }
            };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                callback(null, "启动 node 进程失败：" + ex.Message);
            }
        }

        static string ResolveToolScript(string name)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "Tools", name)),
                Path.GetFullPath(Path.Combine(projectRoot, "Tools", name)),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>自动把出生点放在可走范围中心。</summary>
        public static void AutoPlaceSpawn(LYNOOKWorldAuthoring world)
        {
            if (world.coordinateRoot == null) throw new InvalidOperationException("缺少房间坐标。");
            world.WalkBounds(out var center, out _);
            Vector3 localSpawn = new Vector3(center.x, world.floorHeight, center.y);
            Vector3 worldSpawn = world.coordinateRoot.TransformPoint(localSpawn);
            if (!ValidStandingPoint(world, worldSpawn, out string reason))
                throw new InvalidOperationException("自动出生点无效：" + reason);
            Undo.RecordObjects(new Object[] { world, world.avatarSpawn }, "Auto place avatar spawn");
            world.avatarSpawn.position = worldSpawn;
            world.spawnPlaced = true;
            Changed(world);
        }

        [Serializable] public sealed class SceneDownloadResult
        {
            public string splat, collider;
            public long splatBytes, colliderBytes;
        }

        /// <summary>下载场景输入资产（SPZ + GLB，不下载 pano）到指定目录。</summary>
        public static SceneDownloadResult DownloadSceneAssets(SceneQueueItem item, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var args = $"--sceneId={item.id} --splatUrl=\"{item.splatUrl}\" --colliderUrl=\"{item.colliderMeshUrl}\" --outputDir=\"{outputDir}\"";
            return RunNodeScript<SceneDownloadResult>("scene-download.mjs", args);
        }

        public static void DownloadSceneAssetsAsync(SceneQueueItem item, string outputDir, Action<SceneDownloadResult, string> callback)
        {
            Directory.CreateDirectory(outputDir);
            var args = $"--sceneId={item.id} --splatUrl=\"{item.splatUrl}\" --colliderUrl=\"{item.colliderMeshUrl}\" --outputDir=\"{outputDir}\"";
            RunNodeScriptAsync("scene-download.mjs", args, callback);
        }

        /// <summary>在系统文件管理器中打开批量录制临时目录（不存在则先创建）。</summary>
        public static void OpenBatchTempDirectory()
        {
            Directory.CreateDirectory(BatchTempRoot);
            EditorUtility.RevealInFinder(BatchTempRoot);
        }

        /// <summary>
        /// 清空批量录制临时目录下的所有子目录与文件（保留 lynook_batch 根目录本身）。
        /// 批量录制运行时会拒绝清空，避免删掉正在下载/导入的资产。
        /// 返回释放的字节数。
        /// </summary>
        public static long ClearBatchTempDirectory()
        {
            if (LYNOOKBatchController.IsRunning)
                throw new InvalidOperationException("批量录制正在运行，暂不能清空临时目录。");
            if (!Directory.Exists(BatchTempRoot)) return 0;
            long freed = 0;
            foreach (var entry in Directory.GetFileSystemEntries(BatchTempRoot))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        freed += DirSize(new DirectoryInfo(entry));
                        Directory.Delete(entry, true);
                    }
                    else
                    {
                        var fi = new FileInfo(entry);
                        freed += fi.Length;
                        File.Delete(entry);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"清理临时目录失败 {entry}: {e.Message}");
                }
            }
            return freed;
        }

        static long DirSize(DirectoryInfo dir)
        {
            long size = 0;
            foreach (var fi in dir.GetFiles()) size += fi.Length;
            foreach (var sub in dir.GetDirectories()) size += DirSize(sub);
            return size;
        }

        [Serializable] public sealed class SceneUploadResult
        {
            public string worldJsonUrl, previewVideoUrl, rightVideoUrl, collisionUrl, previewUrl;
        }

        /// <summary>上传录制产物到 Azure，返回各资产的稳定 URL。</summary>
        public static SceneUploadResult UploadSceneAssets(int sceneId, string folder)
        {
            return RunNodeScript<SceneUploadResult>("scene-upload.mjs", $"--sceneId={sceneId} --folder=\"{folder}\"");
        }

        public static void UploadSceneAssetsAsync(int sceneId, string folder, Action<SceneUploadResult, string> callback)
        {
            RunNodeScriptAsync("scene-upload.mjs", $"--sceneId={sceneId} --folder=\"{folder}\"", callback);
        }

        [Serializable] public sealed class SceneUpdateResult
        {
            public bool updated;
            public int sceneId;
            public string convertStatus;
        }

        /// <summary>直接更新 scenes 表的转换状态。</summary>
        public static SceneUpdateResult UpdateSceneStatus(int sceneId, string status, string worldJsonUrl = null, string previewVideoUrl = null, string error = null)
        {
            string args = BuildSceneUpdateArgs(sceneId, status, worldJsonUrl, previewVideoUrl, error);
            return RunNodeScript<SceneUpdateResult>("scene-update.mjs", args);
        }

        public static void UpdateSceneStatusAsync(int sceneId, string status, string worldJsonUrl, string previewVideoUrl, string error, Action<SceneUpdateResult, string> callback)
        {
            string args = BuildSceneUpdateArgs(sceneId, status, worldJsonUrl, previewVideoUrl, error);
            RunNodeScriptAsync("scene-update.mjs", args, callback);
        }

        static string BuildSceneUpdateArgs(int sceneId, string status, string worldJsonUrl, string previewVideoUrl, string error)
        {
            string args = $"--sceneId={sceneId} --status={status}";
            if (status == "ready")
            {
                if (string.IsNullOrEmpty(worldJsonUrl)) throw new InvalidOperationException("status=ready 时必须提供 worldJsonUrl。");
                args += $" --worldJsonUrl=\"{worldJsonUrl}\"";
                if (!string.IsNullOrEmpty(previewVideoUrl)) args += $" --previewVideoUrl=\"{previewVideoUrl}\"";
            }
            else if (status == "failed" && !string.IsNullOrEmpty(error))
            {
                args += $" --error=\"{error.Replace("\"", "\\\"")}\"";
            }
            return args;
        }

        [Serializable] sealed class Draft
        {
            public int schemaVersion = 1;
            public string worldId, roomType, displayName, gaussian, collision, gaussianSha256, collisionSha256;
            public bool alignmentConfirmed, spawnPlaced;
            public Pose coordinates, gaussianTransform, cameraRig, spawn;
            public Pose[] points;
            public float floorHeight, agentRadius, agentHeight, pointSpacing, floorTolerance;
            public Vector2 walkCenter, walkSize;
            public List<Vector2> walkPolygon;
            public float walkAreaThickness;
            public int maximumPoints, recordingSeconds;
            public string deployTargetPath;
        }
    }
}
#endif
