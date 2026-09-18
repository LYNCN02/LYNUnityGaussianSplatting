#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKWorldStudioService
    {
        public const string WorkspaceRoot = "Assets/LYNOOK/Worlds";
        const string RigPrefab = "Assets/LYNOOK/DualScreenRecorder/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string ShaderRoot = "Packages/org.nesnausk.gaussian-splatting/Shaders/";

        public static LYNOOKWorldAuthoring Current => SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<LYNOOKWorldAuthoring>(true)).SingleOrDefault();

        public static LYNOOKWorldAuthoring Import(string gaussianFile, string glbFile, string title, bool highQuality)
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
            world.floorHeight = world.coordinateRoot.InverseTransformPoint(bounds.min).y;
            world.walkCenter = new Vector2(bounds.center.x, bounds.center.z);
            world.walkSize = new Vector2(Mathf.Min(4, bounds.size.x), Mathf.Min(4, bounds.size.z));
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
            bool found = false;
            Physics.SyncTransforms();
            foreach (var collider in world.collisionObject.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || !collider.Raycast(ray, out var hit, distance)) continue;
                distance = hit.distance;
                nearest = hit;
                found = true;
            }
            return found;
        }

        public static bool ValidStandingPoint(LYNOOKWorldAuthoring world, Vector3 position, out string reason)
        {
            if (world.coordinateRoot == null || world.collisionObject == null) { reason = "缺少房间坐标或 GLB。"; return false; }
            Vector3 local = world.coordinateRoot.InverseTransformPoint(position);
            Vector2 delta = new Vector2(local.x, local.z) - world.walkCenter;
            if (Mathf.Abs(delta.x) > world.walkSize.x * 0.5f - world.agentRadius ||
                Mathf.Abs(delta.y) > world.walkSize.y * 0.5f - world.agentRadius)
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
            var candidates = new List<Vector3>();
            float scale = world.coordinateRoot.lossyScale.x;
            for (float x = -world.walkSize.x / 2 + world.agentRadius; x <= world.walkSize.x / 2 - world.agentRadius; x += world.pointSpacing)
            for (float z = -world.walkSize.y / 2 + world.agentRadius; z <= world.walkSize.y / 2 - world.agentRadius; z += world.pointSpacing)
            {
                Vector3 local = new Vector3(x + world.walkCenter.x, world.floorHeight + world.floorTolerance + 0.05f, z + world.walkCenter.y);
                if (!Raycast(world, new Ray(world.coordinateRoot.TransformPoint(local), -world.coordinateRoot.up), out var hit,
                        (world.floorTolerance * 2 + 0.1f) * scale)) continue;
                if (Vector3.Distance(hit.point, world.avatarSpawn.position) < world.pointSpacing * scale) continue;
                if (ValidStandingPoint(world, hit.point, out _) && DirectPathClear(world, world.avatarSpawn.position, hit.point)) candidates.Add(hit.point);
            }
            candidates = candidates.OrderBy(p => Vector3.SqrMagnitude(p - world.avatarSpawn.position)).ToList();
            var accepted = new List<Vector3>();
            foreach (var p in candidates)
            {
                // Runtime currently moves directly between activity points. Require every pair to be safe.
                if (accepted.Any(q => Vector3.Distance(p, q) < world.pointSpacing * scale || !DirectPathClear(world, p, q))) continue;
                accepted.Add(p);
                if (accepted.Count >= world.maximumPoints) break;
            }
            if (accepted.Count == 0) throw new InvalidOperationException("没有找到安全点位。请检查地面高度、活动范围和 GLB 对齐；原有点位已保留。");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            foreach (Transform old in world.activityPoints.Cast<Transform>().ToArray()) Undo.DestroyObjectImmediate(old.gameObject);
            for (int i = 0; i < accepted.Count; i++)
            {
                Transform point = Child(world.activityPoints, "stand_" + (i + 1).ToString("D2"));
                point.position = accepted[i];
                point.rotation = world.avatarSpawn.rotation;
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
            if (!Finite(world.pointSpacing) || world.pointSpacing < 0.2f || world.pointSpacing > 10 ||
                !Finite(world.walkSize.x) || !Finite(world.walkSize.y) || world.walkSize.x <= 0 || world.walkSize.y <= 0 ||
                world.walkSize.x / world.pointSpacing * (world.walkSize.y / world.pointSpacing) > 2500 ||
                !Finite(world.agentRadius) || world.agentRadius < 0.05f || !Finite(world.agentHeight) || world.agentHeight < world.agentRadius * 2 + 0.05f ||
                !Finite(world.walkCenter.x) || !Finite(world.walkCenter.y) ||
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
                worldId = world.worldId, displayName = world.displayName,
                gaussian = world.gaussianSourcePath.Substring(world.workspacePath.Length + 1),
                collision = world.collisionSourcePath.Substring(world.workspacePath.Length + 1),
                gaussianSha256 = world.gaussianSha256, collisionSha256 = world.collisionSha256,
                alignmentConfirmed = world.alignmentConfirmed, spawnPlaced = world.spawnPlaced,
                coordinates = Pose.From(world.coordinateRoot), gaussianTransform = Pose.From(world.gaussian.transform),
                cameraRig = Pose.From(world.cameraRig.CaptureRig), spawn = Pose.From(world.avatarSpawn),
                points = world.activityPoints.Cast<Transform>().Select(Pose.From).ToArray(),
                floorHeight = world.floorHeight, walkCenter = world.walkCenter, walkSize = world.walkSize,
                agentRadius = world.agentRadius, agentHeight = world.agentHeight, pointSpacing = world.pointSpacing,
                floorTolerance = world.floorTolerance, maximumPoints = world.maximumPoints, recordingSeconds = world.recordingSeconds
            };
            string path = world.workspacePath + "/world_draft.json";
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(payload, true));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            File.WriteAllText(world.workspacePath + "/camera_calibration.json", world.cameraRig.GetComponent<CameraRigTransformCopy>() != null
                ? world.cameraRig.GetComponent<CameraRigTransformCopy>().ToJson() : JsonUtility.ToJson(world.cameraRig, true));
            AssetDatabase.Refresh();
        }

        [Serializable] public sealed class Pose
        {
            public string name;
            public Vector3 position, rotationEuler, scale;
            public static Pose From(Transform value) => new Pose { name = value.name, position = value.localPosition, rotationEuler = value.localEulerAngles, scale = value.localScale };
        }

        [Serializable] sealed class Draft
        {
            public int schemaVersion = 1;
            public string worldId, displayName, gaussian, collision, gaussianSha256, collisionSha256;
            public bool alignmentConfirmed, spawnPlaced;
            public Pose coordinates, gaussianTransform, cameraRig, spawn;
            public Pose[] points;
            public float floorHeight, agentRadius, agentHeight, pointSpacing, floorTolerance;
            public Vector2 walkCenter, walkSize;
            public int maximumPoints, recordingSeconds;
        }
    }
}
#endif
