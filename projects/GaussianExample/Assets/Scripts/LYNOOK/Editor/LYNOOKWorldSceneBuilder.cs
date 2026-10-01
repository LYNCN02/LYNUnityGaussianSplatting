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
    public static class LYNOOKWorldSceneBuilder
    {
        public const string WorkspaceRoot = "Assets/LYNOOK/Worlds";
        const string RigPrefab = "Assets/LYNOOK/DualScreenRecorder/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string ShaderRoot = "Packages/org.nesnausk.gaussian-splatting/Shaders/";
        public static LYNOOKWorldAuthoring Import(string gaussianFile, string glbFile, string title, string roomType, bool highQuality, LYNOOKWorldStudioService.SplatSemantics splatSemantics = null)
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
            world.roomType = LynookRoomTypes.Normalize(roomType);
            world.displayName = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(gaussianFile) : title.Trim();
            world.workspacePath = folder;
            world.gaussianSourcePath = sourceGaussian;
            world.collisionSourcePath = sourceGlb;
            world.gaussianSha256 = Sha256(sourceGaussian);
            world.collisionSha256 = Sha256(sourceGlb);
            world.coordinateRoot = LYNOOKWorldSceneBuilder.Child(host.transform, "Room Coordinates");

            // Marble 生成的 SPZ 存的是原始帧 marble_raw_opencv，官方 semantics_metadata 规定固定顺序：
            // ① 中心点与尺寸 × metricScaleFactor 换米制 → ② 中心点 y -= groundPlaneOffset 地面归零
            // （平移只作用中心，不影响尺寸）→ ③ 再绕 X 轴 180°（marble_raw_opencv 的 Y 朝下、Z 朝前，
            // 180° 后 Y 朝上）。批量录制从数据库拿到 splatSemantics 时三步全做；手动本地导入
            // （Scaniverse SPZ/PLY 等，无 semantics）保持原有 identity / (1,1,-1) 行为不变。
            //
            // Gaussian 与配套 GLB 要落到同一坐标系，但两者导入路径不同：
            //  · SPZ 读取器不做任何轴向转换，raw 直接进资产，且 Unity 左手系还要求 Z 取负，
            //    所以 Gaussian 外层 scale = (s, s, -s)；
            //  · GLB 经 glTF 导入器时手性转换（Z 取负）已经烘焙到预制件里，外层只需 (s, s, s)，
            //    即 GLB 的 z scale 不再乘 -1。
            // 两者都绕 X 轴 180°、localPosition.y = +groundPlaneOffset（TRS 平移在父空间、
            // 不受自身缩放影响；经 180° 翻转后正好得到 y = groundOffset − scale·rawY）。
            bool marbleConvention = splatSemantics != null;
            float metricScale = marbleConvention && LYNOOKWorldPersistence.Finite(splatSemantics.metricScaleFactor) && splatSemantics.metricScaleFactor > 0f
                ? splatSemantics.metricScaleFactor
                : 1f;
            float groundOffset = marbleConvention && LYNOOKWorldPersistence.Finite(splatSemantics.groundPlaneOffset)
                ? splatSemantics.groundPlaneOffset
                : 0f;
            var marbleRotation = marbleConvention ? Quaternion.Euler(180f, 0f, 0f) : Quaternion.identity;
            var marblePosition = new Vector3(0f, groundOffset, 0f);
            var gaussianScale = new Vector3(metricScale, metricScale, -metricScale);
            var collisionScale = new Vector3(metricScale, metricScale, metricScale);

            world.collisionObject = (GameObject)PrefabUtility.InstantiatePrefab(collisionPrefab, scene);
            world.collisionObject.transform.SetParent(world.coordinateRoot, false);
            // 与 Gaussian Visual 相同的①②③变换（GLB 的 z scale 不乘 -1）。
            world.collisionObject.transform.localPosition = marblePosition;
            world.collisionObject.transform.localRotation = marbleRotation;
            world.collisionObject.transform.localScale = collisionScale;
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                var collider = filter.GetComponent<MeshCollider>();
                if (collider == null) collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
            }

            var splatObject = LYNOOKWorldSceneBuilder.Child(world.coordinateRoot, "Gaussian Visual").gameObject;
            splatObject.transform.localPosition = marblePosition;
            splatObject.transform.localRotation = marbleRotation;
            splatObject.transform.localScale = gaussianScale;
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

            world.avatarSpawn = LYNOOKWorldSceneBuilder.Child(world.coordinateRoot, "Avatar Spawn");
            world.activityPoints = LYNOOKWorldSceneBuilder.Child(world.coordinateRoot, "Activity Points");
            var export = host.AddComponent<LYNOOKWorldExportSettings>();
            export.worldCoordinateRoot = world.coordinateRoot;
            export.displayName = world.displayName;
            export.gridMap = "";
            var bounds = LYNOOKWorldInteraction.CollisionBounds(world);
            try
            {
                // 优先扫描 GLB 近水平地面并拟合真实轮廓多边形，得到真实可走范围。
                LYNOOKFloorDetection.AutoDetectFloor(world);
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
            LYNOOKWorldInteraction.SetCollisionVisible(world, false);
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
            LYNOOKWorldPersistence.SaveDraft(world);
            Selection.activeGameObject = host;
            LYNOOKWorldInteraction.Frame(world);
            return world;
        }

        internal static Transform Child(Transform parent, string name)
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

    }
}
#endif
