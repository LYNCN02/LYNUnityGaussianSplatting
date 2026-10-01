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
    public static class LYNOOKWorldPersistence
    {
        public static void ValidateSettings(LYNOOKWorldAuthoring world)
        {
            if (world == null || world.coordinateRoot == null || world.gaussian == null || world.collisionObject == null)
                throw new InvalidOperationException("请先导入高斯和 GLB。");
            Vector3 scale = world.coordinateRoot.lossyScale;
            if (!LYNOOKWorldPersistence.Finite(scale.x) || scale.x <= 0 || Mathf.Abs(scale.x - scale.y) > 0.0001f || Mathf.Abs(scale.x - scale.z) > 0.0001f)
                throw new InvalidOperationException("房间坐标必须使用正数等比缩放。");
            world.WalkBounds(out var center, out var size);
            bool walkValid = LYNOOKWorldPersistence.Finite(center.x) && LYNOOKWorldPersistence.Finite(center.y) && LYNOOKWorldPersistence.Finite(size.x) && LYNOOKWorldPersistence.Finite(size.y) && size.x > 0 && size.y > 0;
            if (!LYNOOKWorldPersistence.Finite(world.pointSpacing) || world.pointSpacing < 0.2f || world.pointSpacing > 10 ||
                !walkValid ||
                size.x / world.pointSpacing * (size.y / world.pointSpacing) > 2500 ||
                !LYNOOKWorldPersistence.Finite(world.agentRadius) || world.agentRadius < 0.05f || !LYNOOKWorldPersistence.Finite(world.agentHeight) || world.agentHeight < world.agentRadius * 2 + 0.05f ||
                !LYNOOKWorldPersistence.Finite(world.floorHeight) || !LYNOOKWorldPersistence.Finite(world.floorTolerance) || world.floorTolerance < 0.01f || world.floorTolerance > 1 ||
                world.maximumPoints < 1 || world.maximumPoints > 40)
                throw new InvalidOperationException("检查活动范围和角色尺寸：间距至少 0.2，采样数不超过 2500，点位数 1–40，角色高度需大于直径。");
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static string ValidateForRecording(LYNOOKWorldAuthoring world)
        {
            LYNOOKWorldPersistence.ValidateSettings(world);
            if (!world.alignmentConfirmed) throw new InvalidOperationException("请先检查高斯与 GLB，并勾选已确认对齐。");
            if (!world.spawnPlaced || !LYNOOKWorldInteraction.ValidStandingPoint(world, world.avatarSpawn.position, out _)) throw new InvalidOperationException("请设置有效出生地。");
            int pointCount = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform point in world.activityPoints)
            {
                if (string.IsNullOrWhiteSpace(point.name) || !names.Add(point.name)) throw new InvalidOperationException("活动点名称必须非空且唯一。");
                if (!LYNOOKWorldInteraction.ValidStandingPoint(world, point.position, out string reason)) throw new InvalidOperationException(point.name + "：" + reason);
                // 不再要求各点与其他点之间直线路径无遮挡：最终运行时由寻路算法兜底，
                // 两点间允许绕墙/绕缺口到达。
                ++pointCount;
            }
            if (!world.cameraRig.TryValidate(out string report)) throw new InvalidOperationException(report);
            if (world.recordingSeconds < 1 || world.recordingSeconds > 120) throw new InvalidOperationException("录制时长应为 1–120 秒。");
            return "出生地及 " + pointCount + " 个活动点通过地面与净空检查。双屏物理效果仍需设备验证；点间通行由运行时寻路算法处理。";
        }

        public static void SaveDraft(LYNOOKWorldAuthoring world)
        {
            if (world == null) throw new InvalidOperationException("没有制作场景。");
            if (!EditorSceneManager.SaveScene(world.gameObject.scene)) throw new IOException("制作场景未保存。");
            var payload = new LYNOOKWorldStudioService.Draft
            {
                worldId = world.worldId, roomType = world.roomType, displayName = world.displayName,
                gaussian = world.gaussianSourcePath.Substring(world.workspacePath.Length + 1),
                collision = world.collisionSourcePath.Substring(world.workspacePath.Length + 1),
                gaussianSha256 = world.gaussianSha256, collisionSha256 = world.collisionSha256,
                alignmentConfirmed = world.alignmentConfirmed, spawnPlaced = world.spawnPlaced,
                coordinates = LYNOOKWorldStudioService.Pose.From(world.coordinateRoot), gaussianTransform = LYNOOKWorldStudioService.Pose.From(world.gaussian.transform),
                collisionTransform = LYNOOKWorldStudioService.Pose.From(world.collisionObject.transform),
                cameraRig = LYNOOKWorldStudioService.Pose.From(world.cameraRig.CaptureRig), spawn = LYNOOKWorldStudioService.Pose.From(world.avatarSpawn),
                points = world.activityPoints.Cast<Transform>().Select(LYNOOKWorldStudioService.Pose.From).ToArray(),
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
            if (LYNOOKWorldPersistence.PathStartsWith(sourceDir, targetRoot))
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
                LYNOOKWorldPersistence.CopyDirectory(sourceDir, destDir, ref fileCount, ref totalBytes);

                // 同时迁移最近一次录制产物（双屏视频 + world_config.json + collision.glb + preview）
                string latestRecording = LYNOOKWorldPersistence.FindLatestRecording(world.worldId);
                string recordingDest = null;
                if (!string.IsNullOrEmpty(latestRecording))
                {
                    EditorUtility.DisplayProgressBar("发布到部署工程", "正在复制最近录制产物…", 0.7f);
                    recordingDest = Path.Combine(destDir, "Recording");
                    LYNOOKWorldPersistence.CopyDirectory(latestRecording, recordingDest, ref fileCount, ref totalBytes);
                }

                // 若目标在本工程 Assets 内，刷新一下；否则仅在目标工程打开时由其自行导入。
                string projectAssets = Path.GetFullPath("Assets");
                if (LYNOOKWorldPersistence.PathStartsWith(targetRoot, projectAssets))
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
                LYNOOKWorldPersistence.CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), ref fileCount, ref totalBytes);
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

    }
}
#endif
