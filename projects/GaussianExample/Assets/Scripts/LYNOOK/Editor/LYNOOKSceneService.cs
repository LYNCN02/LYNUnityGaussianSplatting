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
    public static class LYNOOKSceneService
    {
        /// <summary>批量录制下载资产的临时目录根（系统临时目录下的 lynook_batch）。</summary>
        public static string BatchTempRoot => Path.Combine(Path.GetTempPath(), "lynook_batch");
        /// <summary>
        /// 拉取待转换（convert_status = pending）的场景列表。
        /// 通过本机 Tools/scene-queue-json.mjs 查询 Postgres，返回结果不做认领，仅展示。
        /// </summary>
        public static LYNOOKWorldStudioService.SceneQueueItem[] FetchSceneQueue(int limit = 50)
        {
            string scriptPath = LYNOOKNodeProcessRunner.ResolveSceneQueueScript();
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("找不到 Tools/scene-queue-json.mjs，请确认仓库根目录。");
            string nodePath = LYNOOKNodeProcessRunner.LocateNode();
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
                var result = JsonUtility.FromJson<LYNOOKWorldStudioService.SceneQueueResult>(json);
                if (result == null || result.rows == null)
                    throw new InvalidOperationException("场景队列 JSON 解析失败。");
                return result.rows;
            }
        }

        /// <summary>下载场景输入资产（SPZ + GLB，不下载 pano）到指定目录。</summary>
        public static LYNOOKWorldStudioService.SceneDownloadResult DownloadSceneAssets(LYNOOKWorldStudioService.SceneQueueItem item, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var args = $"--sceneId={item.id} --splatUrl=\"{item.splatUrl}\" --colliderUrl=\"{item.colliderMeshUrl}\" --outputDir=\"{outputDir}\"";
            return LYNOOKNodeProcessRunner.RunNodeScript<LYNOOKWorldStudioService.SceneDownloadResult>("scene-download.mjs", args);
        }

        public static void DownloadSceneAssetsAsync(LYNOOKWorldStudioService.SceneQueueItem item, string outputDir, Action<LYNOOKWorldStudioService.SceneDownloadResult, string> callback)
        {
            Directory.CreateDirectory(outputDir);
            var args = $"--sceneId={item.id} --splatUrl=\"{item.splatUrl}\" --colliderUrl=\"{item.colliderMeshUrl}\" --outputDir=\"{outputDir}\"";
            LYNOOKNodeProcessRunner.RunNodeScriptAsync("scene-download.mjs", args, callback);
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
                        freed += LYNOOKSceneService.DirSize(new DirectoryInfo(entry));
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
            foreach (var sub in dir.GetDirectories()) size += LYNOOKSceneService.DirSize(sub);
            return size;
        }

        /// <summary>上传录制产物到 Azure，返回各资产的稳定 URL。</summary>
        public static LYNOOKWorldStudioService.SceneUploadResult UploadSceneAssets(int sceneId, string folder)
        {
            return LYNOOKNodeProcessRunner.RunNodeScript<LYNOOKWorldStudioService.SceneUploadResult>("scene-upload.mjs", $"--sceneId={sceneId} --folder=\"{folder}\"");
        }

        public static void UploadSceneAssetsAsync(int sceneId, string folder, Action<LYNOOKWorldStudioService.SceneUploadResult, string> callback)
        {
            LYNOOKNodeProcessRunner.RunNodeScriptAsync("scene-upload.mjs", $"--sceneId={sceneId} --folder=\"{folder}\"", callback);
        }

        /// <summary>直接更新 scenes 表的转换状态。</summary>
        public static LYNOOKWorldStudioService.SceneUpdateResult UpdateSceneStatus(int sceneId, string status, string worldJsonUrl = null, string previewVideoUrl = null, string error = null)
        {
            string args = BuildSceneUpdateArgs(sceneId, status, worldJsonUrl, previewVideoUrl, error);
            return LYNOOKNodeProcessRunner.RunNodeScript<LYNOOKWorldStudioService.SceneUpdateResult>("scene-update.mjs", args);
        }

        public static void UpdateSceneStatusAsync(int sceneId, string status, string worldJsonUrl, string previewVideoUrl, string error, Action<LYNOOKWorldStudioService.SceneUpdateResult, string> callback)
        {
            string args = BuildSceneUpdateArgs(sceneId, status, worldJsonUrl, previewVideoUrl, error);
            LYNOOKNodeProcessRunner.RunNodeScriptAsync("scene-update.mjs", args, callback);
        }

        static string BuildSceneUpdateArgs(int sceneId, string status, string worldJsonUrl, string previewVideoUrl, string error)
        {
            string args = $"--sceneId={sceneId} --status={status}";
            if (status == LynookConvertStatuses.Ready)
            {
                if (string.IsNullOrEmpty(worldJsonUrl)) throw new InvalidOperationException("status=ready 时必须提供 worldJsonUrl。");
                args += $" --worldJsonUrl=\"{worldJsonUrl}\"";
                if (!string.IsNullOrEmpty(previewVideoUrl)) args += $" --previewVideoUrl=\"{previewVideoUrl}\"";
            }
            else if (status == LynookConvertStatuses.Failed && !string.IsNullOrEmpty(error))
            {
                args += $" --error=\"{error.Replace("\"", "\\\"")}\"";
            }
            return args;
        }

    }
}
#endif
