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
            // 关键加载顺序：Gaussian Visual 必须保持 inactive，直到 SaveScene 之后资产 native
            // side 完全就绪（gaussian.HasValidAsset == true）才能 SetActive(true)。
            // 提前激活时若资产处于 fake-null（C# 字段在、native 未加载），OnEnable 的
            // CreateResourcesForAsset 会因 HasValidAsset == false 直接返回；之后 Update 中
            // fake-null 与 null 经重载 == 比较相等，也不会再重建 GPU 资源——表现为高斯不显示、
            // 后续地面点击/录制出现空引用。激活逻辑见本方法 SaveScene 之后的就绪等待循环。

            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            PrefabUtility.UnpackPrefabInstance(rigObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            rigObject.transform.SetParent(host.transform, false);
            var rig = rigObject.GetComponent<LYNOOKDualCameraRig>();
            if (rig == null) throw new InvalidOperationException("双屏相机模板缺少 LYNOOKDualCameraRig 组件：" + RigPrefab);
            if (rigObject.GetComponent<CameraRigTransformCopy>() == null) rigObject.AddComponent<CameraRigTransformCopy>();
            world.cameraRig = rig;
            var mainTexture = Object.Instantiate(rig.MainCaptureTexture);
            var sideTexture = Object.Instantiate(rig.SideCaptureTexture);
            AssetDatabase.CreateAsset(mainTexture, folder + "/Main.renderTexture");
            AssetDatabase.CreateAsset(sideTexture, folder + "/Side.renderTexture");
            rig.SetReferences(rig.CaptureRig, rig.MainCaptureCamera, rig.SideCaptureCamera, mainTexture, sideTexture);
            var session = rigObject.GetComponent<LYNOOKDualRecordingSession>();
            if (session == null) session = rigObject.AddComponent<LYNOOKDualRecordingSession>();
            // AddComponent 对 Editor 程序集里的 MonoBehaviour 会静默返回 null（Console 另有
            // "Can't add script behaviour ... because it is an editor script" 报错），
            // 不拦截就会在下一行 session.SetReferences 处变成含义不明的空引用。
            if (session == null)
                throw new InvalidOperationException(
                    "无法添加 LYNOOKDualRecordingSession：该组件必须位于非 Editor 程序集（场景物体不能挂 Editor-only asmdef 中的 MonoBehaviour）。");
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
            // 第一次保存时 Gaussian Visual 仍是 inactive：m_Asset 的 GUID/fileID 引用会照常
            // 写入场景，但 SaveScene 触发的 Room.unity 导入会进入 AssetDatabase 队列，可能让
            // 内存中的 GaussianSplatAsset 变成 fake-null（C# 包装在、native side 未加载）。
            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new IOException("无法保存制作场景。");

            // ── 加载顺序：等高斯资产完全 ready 后才在场景里激活渲染器 ──
            // 有界同步重试：每轮强制同步 Refresh → 重新导入全部 .bytes 子资产 → 重新导入并
            // 加载主 .asset → 赋给仍处于 inactive 的 renderer，以 renderer.HasValidAsset
            // （m_Asset 非 fake-null + splatCount>0 + 版本正确 + pos/other/color/sh 全在）
            // 作为唯一就绪判据。inactive 状态下赋值不会触发 OnEnable/Update，只做判定。
            const int maxReadyAttempts = 4;
            GaussianSplatAsset readyAsset = null;
            for (int attempt = 1; attempt <= maxReadyAttempts; attempt++)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                foreach (var sub in subAssetFiles)
                    if (File.Exists(sub))
                        AssetDatabase.ImportAsset(sub, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var candidate = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                gaussian.m_Asset = candidate;
                if (gaussian.HasValidAsset)
                {
                    readyAsset = candidate;
                    break;
                }
                Debug.LogWarning($"[LYNOOK] 高斯资产第 {attempt}/{maxReadyAttempts} 次同步刷新后仍未就绪，重试：{assetPath}");
            }
            if (readyAsset == null)
                throw new InvalidOperationException("高斯资产在多次同步刷新后仍未就绪（fake-null 或子资产缺失），中止导入: " + assetPath);

            // 资产就绪后再激活：SetActive 同步触发 OnEnable，EnsureMaterials / 注册渲染系统 /
            // CreateResourcesForAsset 一次成功；HasValidRenderSetup 确认 GPU 缓冲已建立。
            splatObject.SetActive(true);
            if (!gaussian.HasValidRenderSetup)
                throw new InvalidOperationException("高斯渲染器激活后 GPU 资源未建立，请检查 ComputeShader 引用与显卡 Compute Shader 支持。");

            // 第二次保存：第一次保存时 Gaussian Visual 还是 inactive，必须把激活后的 active
            // 状态与最终 m_Asset 引用持久化进 Room.unity，否则重开场景高斯物体不显示。
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new IOException("无法保存制作场景。");
            // SaveScene 理论上可能再次让 m_Asset 退回 fake-null。渲染器此刻已激活，只需重新
            // 加载并赋值，下一个 Update 会走 m_PrevAsset != m_Asset 的热切换路径，自动
            // DisposeResourcesForAsset + CreateResourcesForAsset 重建 GPU 缓冲。
            if (!gaussian.HasValidAsset)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var postSaveAsset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                if (postSaveAsset != null && postSaveAsset.splatCount > 0 &&
                    postSaveAsset.posData != null && postSaveAsset.otherData != null &&
                    postSaveAsset.colorData != null && postSaveAsset.shData != null)
                {
                    gaussian.m_Asset = postSaveAsset;
                }
                else
                {
                    Debug.LogWarning("[LYNOOK] 二次保存后高斯资产仍 fake-null，已保持物体激活；下次 AssetDatabase 刷新后可在 Inspector 重新指定资产恢复。");
                }
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
