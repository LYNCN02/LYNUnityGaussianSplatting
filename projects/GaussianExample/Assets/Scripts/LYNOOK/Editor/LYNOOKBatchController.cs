#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lynook.DualScreen.Editor
{
    public enum BatchTaskState
    {
        Pending,
        Downloading,
        Importing,
        Aligning,
        WaitingForAlignment,
        Spawning,
        GeneratingPoints,
        WaitingForCamera,
        Recording,
        Uploading,
        UpdatingDB,
        Done,
        Failed,
    }

    public sealed class BatchTask
    {
        public LYNOOKWorldStudioService.SceneQueueItem item;
        public BatchTaskState state;
        public string error;
        public string inputDir;
        public LYNOOKWorldAuthoring world;
        public LYNOOKWorldStudioService.SceneUploadResult uploadResult;
    }

    /// <summary>
    /// 批量录制控制器：串行处理勾选的场景任务。
    /// 流程：下载 → 导入 → 自动对齐 → 自动出生点 → 生成活动点 → 【暂停等用户调相机】 → 录制 → 上传 Azure → 更新数据库。
    /// </summary>
    [InitializeOnLoad]
    public static class LYNOOKBatchController
    {
        static readonly List<BatchTask> tasks = new List<BatchTask>();
        static int currentIndex;
        static bool running;
        static bool finished;
        static BatchTaskState currentState;
        static string currentMessage;

        public static event Action StateChanged;
        public static bool IsRunning => running;
        public static bool HasFinished => finished;
        public static int CurrentIndex => currentIndex;
        public static int TotalCount => tasks.Count;
        public static BatchTaskState CurrentState => currentState;
        public static string CurrentMessage => currentMessage;
        public static IReadOnlyList<BatchTask> Tasks => tasks;

        static LYNOOKBatchController()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Start(List<LYNOOKWorldStudioService.SceneQueueItem> selected)
        {
            if (running) throw new InvalidOperationException("批量录制已在运行。");
            if (selected == null || selected.Count == 0) throw new InvalidOperationException("没有勾选任务。");
            tasks.Clear();
            foreach (var item in selected)
                tasks.Add(new BatchTask { item = item, state = BatchTaskState.Pending });
            currentIndex = 0;
            running = true;
            finished = false;
            EditorApplication.delayCall += ProcessCurrent;
            Notify();
        }

        public static void Cancel()
        {
            running = false;
            finished = true;
            currentMessage = "已取消批量录制。";
            Notify();
        }

        /// <summary>用户手动调好地面/对齐后点击继续，确认对齐并进入出生点步骤。</summary>
        public static void ContinueFromAlignment()
        {
            if (!running || currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            if (task.state != BatchTaskState.WaitingForAlignment) return;
            var world = task.world;
            Undo.RecordObject(world, "Batch confirm alignment");
            world.alignmentConfirmed = true;
            LYNOOKWorldStudioService.Changed(world);
            task.state = BatchTaskState.Spawning;
            currentState = BatchTaskState.Spawning;
            currentMessage = $"[4/8] 自动放置出生点 #{task.item.id} {task.item.name}…";
            Notify();
            EditorApplication.delayCall += ProcessCurrent;
        }

        /// <summary>用户调完相机后点击继续，触发录制。</summary>
        public static void ContinueFromCamera()
        {
            if (!running || currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            if (task.state != BatchTaskState.WaitingForCamera) return;
            task.state = BatchTaskState.Recording;
            currentState = BatchTaskState.Recording;
            currentMessage = $"[7/8] 录制中 #{task.item.id} {task.item.name}…（PlayMode 自动运行，请勿操作）";
            Notify();
            try
            {
                LYNOOKWorldStudioRecording.Start(task.world);
            }
            catch (Exception e)
            {
                FailCurrent(e.Message);
            }
        }

        static void ProcessCurrent()
        {
            if (!running || currentIndex >= tasks.Count) { Finish(); return; }
            var task = tasks[currentIndex];
            try
            {
                switch (task.state)
                {
                    case BatchTaskState.Pending:
                    case BatchTaskState.Downloading:
                        Download(task);
                        break;
                    case BatchTaskState.Importing:
                        Import(task);
                        break;
                    case BatchTaskState.Aligning:
                        // 跳过自动对齐，进入手动确认暂停点
                        task.state = BatchTaskState.WaitingForAlignment;
                        currentState = BatchTaskState.WaitingForAlignment;
                        currentMessage = $"[3/8] ⏸ 请手动调整 #{task.item.id} {task.item.name} 的地面/对齐，确认后点击「继续批量录制」。";
                        Notify();
                        break;
                    // WaitingForAlignment：等用户点继续，由 ContinueFromAlignment 推进
                    case BatchTaskState.Spawning:
                        PlaceSpawn(task);
                        break;
                    case BatchTaskState.GeneratingPoints:
                        GeneratePoints(task);
                        break;
                    // WaitingForCamera：等用户点继续，由 ContinueFromCamera 推进
                    case BatchTaskState.Recording:
                        // 录制由 PlayMode 驱动，OnPlayModeStateChanged 处理
                        break;
                    case BatchTaskState.Uploading:
                        Upload(task);
                        break;
                    case BatchTaskState.UpdatingDB:
                        UpdateDB(task);
                        break;
                    case BatchTaskState.Done:
                        NextTask();
                        break;
                }
            }
            catch (Exception e)
            {
                FailCurrent(e.Message);
            }
        }

        static void Download(BatchTask task)
        {
            task.state = BatchTaskState.Downloading;
            currentState = BatchTaskState.Downloading;
            task.inputDir = Path.Combine(LYNOOKWorldStudioService.BatchTempRoot, task.item.id.ToString());
            string splatPath = Path.Combine(task.inputDir, "room.spz");
            string glbPath = Path.Combine(task.inputDir, "collision.glb");
            // 已下载过的资产直接复用，不重复下载
            if (File.Exists(splatPath) && File.Exists(glbPath) && new FileInfo(splatPath).Length > 0 && new FileInfo(glbPath).Length > 0)
            {
                currentMessage = $"[1/8] 已有缓存，跳过下载 #{task.item.id} {task.item.name}（SPZ {FormatSize(new FileInfo(splatPath).Length)} + GLB {FormatSize(new FileInfo(glbPath).Length)}）";
                Notify();
                task.state = BatchTaskState.Importing;
                EditorApplication.delayCall += ProcessCurrent;
                return;
            }
            currentMessage = $"[1/8] 下载资产 #{task.item.id} {task.item.name}（SPZ + GLB）…";
            Notify();
            Directory.CreateDirectory(task.inputDir);
            LYNOOKWorldStudioService.DownloadSceneAssetsAsync(task.item, task.inputDir, (result, err) =>
            {
                if (err != null) { FailCurrent(err); return; }
                if (string.IsNullOrEmpty(result.splat) || string.IsNullOrEmpty(result.collider))
                {
                    FailCurrent("下载结果缺少 splat 或 collider。");
                    return;
                }
                currentMessage = $"[1/8] 下载完成：SPZ {FormatSize(result.splatBytes)} + GLB {FormatSize(result.colliderBytes)}";
                Notify();
                task.state = BatchTaskState.Importing;
                EditorApplication.delayCall += ProcessCurrent;
            });
        }

        static void Import(BatchTask task)
        {
            task.state = BatchTaskState.Importing;
            currentState = BatchTaskState.Importing;
            currentMessage = $"[2/8] 导入房间 #{task.item.id} {task.item.name}…";
            Notify();
            string splatPath = Path.Combine(task.inputDir, "room.spz");
            string glbPath = Path.Combine(task.inputDir, "collision.glb");
            task.world = LYNOOKWorldStudioService.Import(splatPath, glbPath, task.item.name, task.item.roomType, true);
            task.state = BatchTaskState.Aligning;
            EditorApplication.delayCall += ProcessCurrent;
        }

        static void PlaceSpawn(BatchTask task)
        {
            task.state = BatchTaskState.Spawning;
            currentState = BatchTaskState.Spawning;
            currentMessage = $"[4/8] 自动放置出生点 #{task.item.id} {task.item.name}…";
            Notify();
            LYNOOKWorldStudioService.AutoPlaceSpawn(task.world);
            task.state = BatchTaskState.GeneratingPoints;
            EditorApplication.delayCall += ProcessCurrent;
        }

        static void GeneratePoints(BatchTask task)
        {
            task.state = BatchTaskState.GeneratingPoints;
            currentState = BatchTaskState.GeneratingPoints;
            currentMessage = $"[5/8] 生成活动点 #{task.item.id} {task.item.name}…";
            Notify();
            LYNOOKWorldStudioService.GeneratePoints(task.world);
            task.state = BatchTaskState.WaitingForCamera;
            currentState = BatchTaskState.WaitingForCamera;
            currentMessage = $"[6/8] ⏸ 请手动调整 #{task.item.id} {task.item.name} 的相机取景，完成后点击「继续批量录制」。";
            Notify();
            // 暂停，等用户点继续
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!running || currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            if (task.state != BatchTaskState.Recording) return;
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                // 录制完成，校验产物后上传
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        string recordingFolder = FindRecordingFolder(task.world.worldId);
                        if (string.IsNullOrEmpty(recordingFolder))
                            throw new InvalidOperationException("未找到录制输出目录。");
                        if (!ValidateRecording(recordingFolder))
                            throw new InvalidOperationException("录制产物不完整。");
                        task.state = BatchTaskState.Uploading;
                        currentState = BatchTaskState.Uploading;
                        currentMessage = $"[8/8] 上传产物到 Azure #{task.item.id} {task.item.name}…";
                        Notify();
                        EditorApplication.delayCall += ProcessCurrent;
                    }
                    catch (Exception e)
                    {
                        FailCurrent(e.Message);
                    }
                };
            }
        }

        static string FindRecordingFolder(string worldId)
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

        static bool ValidateRecording(string folder)
        {
            string[] required = { "main.mp4", "right.mp4", "world_config.json", "mesh/collision.glb", "preview.png" };
            foreach (string file in required)
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            }
            return true;
        }

        static void Upload(BatchTask task)
        {
            string recordingFolder = FindRecordingFolder(task.world.worldId);
            if (string.IsNullOrEmpty(recordingFolder)) throw new InvalidOperationException("未找到录制输出目录。");
            LYNOOKWorldStudioService.UploadSceneAssetsAsync(task.item.id, recordingFolder, (result, err) =>
            {
                if (err != null) { FailCurrent(err); return; }
                task.uploadResult = result;
                task.state = BatchTaskState.UpdatingDB;
                currentState = BatchTaskState.UpdatingDB;
                currentMessage = $"[8/8] 更新数据库 #{task.item.id} {task.item.name}…";
                Notify();
                EditorApplication.delayCall += ProcessCurrent;
            });
        }

        static void UpdateDB(BatchTask task)
        {
            var upload = task.uploadResult;
            LYNOOKWorldStudioService.UpdateSceneStatusAsync(
                task.item.id, "ready",
                worldJsonUrl: upload.worldJsonUrl,
                previewVideoUrl: upload.previewVideoUrl,
                error: null,
                callback: (result, err) =>
                {
                    if (err != null) { FailCurrent(err); return; }
                    task.state = BatchTaskState.Done;
                    currentMessage = $"✅ 完成 #{task.item.id} {task.item.name}。";
                    Notify();
                    EditorApplication.delayCall += ProcessCurrent;
                });
        }

        static void FailCurrent(string error)
        {
            if (currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            task.state = BatchTaskState.Failed;
            task.error = error;
            currentMessage = $"❌ 失败 #{task.item.id} {task.item.name}：{error}";
            Debug.LogError($"[LYNOOKBatchController] {currentMessage}");
            // 失败也写数据库（异步，不阻塞）
            LYNOOKWorldStudioService.UpdateSceneStatusAsync(task.item.id, "failed", null, null, error, (_, __) => { });
            Notify();
            NextTask();
        }

        static void NextTask()
        {
            currentIndex++;
            if (currentIndex >= tasks.Count) { Finish(); return; }
            EditorApplication.delayCall += ProcessCurrent;
        }

        static void Finish()
        {
            running = false;
            finished = true;
            int done = tasks.Count(t => t.state == BatchTaskState.Done);
            int failed = tasks.Count(t => t.state == BatchTaskState.Failed);
            string summary = $"批量录制结束：成功 {done}，失败 {failed}。";
            // 保留失败任务的错误信息；若全部成功则显示汇总
            var lastFailed = tasks.LastOrDefault(t => t.state == BatchTaskState.Failed);
            currentMessage = lastFailed != null
                ? $"{summary}\n最近失败：#{lastFailed.item.id} {lastFailed.item.name}：{lastFailed.error}"
                : summary;
            Debug.Log($"[LYNOOKBatchController] {currentMessage}");
            Notify();
        }

        static void Notify()
        {
            StateChanged?.Invoke();
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
        }
    }
}
#endif
