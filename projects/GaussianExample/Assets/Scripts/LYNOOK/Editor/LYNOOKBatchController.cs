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
    /// 流程：下载 → 导入 → 【暂停调对齐】 → 出生点 → 活动点 → 【暂停调相机】 → 录制 → 上传 Azure → 更新数据库。
    /// 状态机约定：所有状态写入都经过 <see cref="Transition"/>（唯一入口），
    /// 进入状态时的默认文案由 <see cref="DefaultMessage"/> 派生；异步/动态细节用 messageOverride。
    /// WaitingForAlignment / WaitingForCamera 无 handler，仅靠 Continue* 方法推进。
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

        // 每个可自动推进的状态对应一个 enter handler；等待状态不在表中（保持暂停）。
        static readonly Dictionary<BatchTaskState, Action<BatchTask>> handlers =
            new Dictionary<BatchTaskState, Action<BatchTask>>
            {
                { BatchTaskState.Pending, BeginDownload },
                { BatchTaskState.Downloading, BeginDownload },
                { BatchTaskState.Importing, BeginImport },
                { BatchTaskState.Spawning, BeginPlaceSpawn },
                { BatchTaskState.GeneratingPoints, BeginGeneratePoints },
                { BatchTaskState.Uploading, BeginUpload },
                { BatchTaskState.UpdatingDB, BeginUpdateDB },
                { BatchTaskState.Done, t => NextTask() },
            };

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
            Undo.RecordObject(task.world, "Batch confirm alignment");
            task.world.alignmentConfirmed = true;
            LYNOOKWorldInteraction.Changed(task.world);
            Transition(task, BatchTaskState.Spawning);
            EditorApplication.delayCall += ProcessCurrent;
        }

        /// <summary>用户调完相机后点击继续，进入录制状态并触发 PlayMode。</summary>
        public static void ContinueFromCamera()
        {
            if (!running || currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            if (task.state != BatchTaskState.WaitingForCamera) return;
            Transition(task, BatchTaskState.Recording);
            try
            {
                // 走批量录制入口：world_config.json / status.json 记 source=batch + scenes.id。
                LYNOOKWorldStudioRecording.StartBatch(task.world, task.item.id);
            }
            catch (Exception exception)
            {
                FailCurrent(exception);
            }
        }

        static void ProcessCurrent()
        {
            if (!running || currentIndex >= tasks.Count) { Finish(); return; }
            var task = tasks[currentIndex];
            try
            {
                // 无 handler 的状态（两个等待态、Recording 由 PlayMode 驱动）保持不动。
                if (handlers.TryGetValue(task.state, out var handler)) handler(task);
            }
            catch (Exception exception)
            {
                FailCurrent(exception);
            }
        }

        // ── 状态写入唯一入口 ──
        static void Transition(BatchTask task, BatchTaskState next, string messageOverride = null)
        {
            task.state = next;
            currentState = next;
            currentMessage = messageOverride ?? DefaultMessage(task, next);
            Notify();
        }

        // ── 各状态默认文案（唯一来源；步骤号与实际进度对应） ──
        static string DefaultMessage(BatchTask task, BatchTaskState state)
        {
            string tag = $"#{task.item.id} {task.item.name}";
            switch (state)
            {
                case BatchTaskState.Pending:
                case BatchTaskState.Downloading:
                    return $"[1/8] 下载资产 {tag}（SPZ + GLB）…";
                case BatchTaskState.Importing:
                    return $"[2/8] 导入房间 {tag}…";
                case BatchTaskState.WaitingForAlignment:
                    return $"[3/8] ⏸ 请手动调整 {tag} 的地面/对齐，确认后点击「继续批量录制」。";
                case BatchTaskState.Spawning:
                    return $"[4/8] 自动放置出生点 {tag}…";
                case BatchTaskState.GeneratingPoints:
                    return $"[5/8] 生成活动点 {tag}…";
                case BatchTaskState.WaitingForCamera:
                    return $"[6/8] ⏸ 请手动调整 {tag} 的相机取景，完成后点击「继续批量录制」。";
                case BatchTaskState.Recording:
                    return $"[7/8] 录制中 {tag}…（PlayMode 自动运行，请勿操作）";
                case BatchTaskState.Uploading:
                    return $"[8/8] 上传产物到 Azure {tag}…";
                case BatchTaskState.UpdatingDB:
                    return $"[8/8] 更新数据库 {tag}…";
                case BatchTaskState.Done:
                    return $"✅ 完成 {tag}。";
                default:
                    return currentMessage ?? "";
            }
        }

        // ── enter handlers ──

        static void BeginDownload(BatchTask task)
        {
            task.inputDir = Path.Combine(LYNOOKWorldStudioService.BatchTempRoot, task.item.id.ToString());
            string splatPath = Path.Combine(task.inputDir, "room.spz");
            string glbPath = Path.Combine(task.inputDir, "collision.glb");
            // 已下载过的资产直接复用，不重复下载（用消息 override 保留缓存明细）。
            if (File.Exists(splatPath) && File.Exists(glbPath) &&
                new FileInfo(splatPath).Length > 0 && new FileInfo(glbPath).Length > 0)
            {
                string cached = $"[1/8] 已有缓存，跳过下载 #{task.item.id} {task.item.name}" +
                    $"（SPZ {FormatSize(new FileInfo(splatPath).Length)} + GLB {FormatSize(new FileInfo(glbPath).Length)}）";
                Transition(task, BatchTaskState.Importing, cached);
                EditorApplication.delayCall += ProcessCurrent;
                return;
            }
            Transition(task, BatchTaskState.Downloading);
            Directory.CreateDirectory(task.inputDir);
            LYNOOKSceneService.DownloadSceneAssetsAsync(task.item, task.inputDir, (result, err) =>
            {
                if (err != null) { FailCurrent(err); return; }
                if (string.IsNullOrEmpty(result.splat) || string.IsNullOrEmpty(result.collider))
                {
                    FailCurrent("下载结果缺少 splat 或 collider。");
                    return;
                }
                string done = $"[1/8] 下载完成：SPZ {FormatSize(result.splatBytes)} + GLB {FormatSize(result.colliderBytes)}";
                Transition(task, BatchTaskState.Importing, done);
                EditorApplication.delayCall += ProcessCurrent;
            });
        }

        static void BeginImport(BatchTask task)
        {
            Transition(task, BatchTaskState.Importing);
            string splatPath = Path.Combine(task.inputDir, "room.spz");
            string glbPath = Path.Combine(task.inputDir, "collision.glb");
            // 数据库里的 splatSemantics（metricScaleFactor / groundPlaneOffset）随导入传入，
            // 创建高斯与配套 GLB 时按官方口径做①米制缩放 + ②地面归零 + ③绕 X 轴 180°
            //（GLB 手性转换已由 glTF 导入器烘焙，外层 z scale 不乘 -1）。
            var semantics = task.item.splatSemantics;
            task.world = LYNOOKWorldSceneBuilder.Import(splatPath, glbPath, task.item.name, task.item.roomType, true, semantics);
            if (semantics != null && semantics.metricScaleFactor > 0f)
                Debug.Log($"[LYNOOKBatchController] #{task.item.id} {task.item.name} 应用 Marble SPZ 语义变换（高斯+GLB）：metricScaleFactor={semantics.metricScaleFactor}, groundPlaneOffset={semantics.groundPlaneOffset}, X轴180°=true");
            // 导入后直接进入手动对齐暂停点（不再需要 Aligning 中转状态）。
            Transition(task, BatchTaskState.WaitingForAlignment);
        }

        static void BeginPlaceSpawn(BatchTask task)
        {
            Transition(task, BatchTaskState.Spawning);
            LYNOOKWorldInteraction.AutoPlaceSpawn(task.world);
            Transition(task, BatchTaskState.GeneratingPoints);
            EditorApplication.delayCall += ProcessCurrent;
        }

        static void BeginGeneratePoints(BatchTask task)
        {
            Transition(task, BatchTaskState.GeneratingPoints);
            LYNOOKWorldInteraction.GeneratePoints(task.world);
            Transition(task, BatchTaskState.WaitingForCamera);
            // 暂停，等用户点继续
        }

        static void BeginUpload(BatchTask task)
        {
            string recordingFolder = FindRecordingFolder(task.world.worldId);
            if (string.IsNullOrEmpty(recordingFolder)) throw new InvalidOperationException("未找到录制输出目录。");
            Debug.Log($"[LYNOOKBatchController] [8/8] 开始上传 Azure #{task.item.id} {task.item.name}：{recordingFolder}");
            LYNOOKSceneService.UploadSceneAssetsAsync(task.item.id, recordingFolder, (result, err) =>
            {
                if (err != null) { FailCurrent(err); return; }
                task.uploadResult = result;
                Debug.Log(
                    $"[LYNOOKBatchController] [8/8] 上传完成 #{task.item.id} {task.item.name}：\n" +
                    $"  world_config: {result.worldJsonUrl}\n" +
                    $"  main.mov: {result.previewVideoUrl}\n" +
                    $"  right.mov: {result.rightVideoUrl}\n" +
                    $"  collision.glb: {result.collisionUrl}\n" +
                    $"  preview.png: {result.previewUrl}\n" +
                    $"  preview_right.png: {result.previewRightUrl}");
                Transition(task, BatchTaskState.UpdatingDB);
                EditorApplication.delayCall += ProcessCurrent;
            });
        }

        static void BeginUpdateDB(BatchTask task)
        {
            var upload = task.uploadResult;
            Debug.Log($"[LYNOOKBatchController] [8/8] 开始更新数据库 #{task.item.id} {task.item.name}：convert_status = {LynookConvertStatuses.Ready}");
            LYNOOKSceneService.UpdateSceneStatusAsync(
                task.item.id, LynookConvertStatuses.Ready,
                worldJsonUrl: upload.worldJsonUrl,
                previewVideoUrl: upload.previewVideoUrl,
                error: null,
                callback: (result, err) =>
                {
                    if (err != null) { FailCurrent(err); return; }
                    Debug.Log(
                        $"[LYNOOKBatchController] [8/8] 数据库更新完成 #{task.item.id} {task.item.name}：" +
                        $"updated={result.updated}, convert_status={result.convertStatus}, sceneId={result.sceneId}");
                    Transition(task, BatchTaskState.Done);
                    EditorApplication.delayCall += ProcessCurrent;
                });
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!running || currentIndex >= tasks.Count) return;
            var task = tasks[currentIndex];
            if (task.state != BatchTaskState.Recording) return;
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                // 录制完成，校验产物后进入上传
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        string recordingFolder = FindRecordingFolder(task.world.worldId);
                        if (string.IsNullOrEmpty(recordingFolder))
                            throw new InvalidOperationException("未找到录制输出目录。");
                        if (!ValidateRecording(recordingFolder))
                            throw new InvalidOperationException("录制产物不完整。");
                        Transition(task, BatchTaskState.Uploading);
                        EditorApplication.delayCall += ProcessCurrent;
                    }
                    catch (Exception exception)
                    {
                        FailCurrent(exception);
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
            // 交付产物：两路 MOV + world_config + 碰撞 GLB + 正/侧首帧预览；无 mp4。
            string[] required =
            {
                "main.mov", "right.mov", "world_config.json",
                "mesh/collision.glb", "preview.png", "preview_right.png"
            };
            foreach (string file in required)
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            }
            return true;
        }

        // ── 失败 / 收尾 ──

        static void FailCurrent(Exception exception)
        {
            // 保留出错状态与完整堆栈，避免只看 Message 无法定位空引用来源。
            string where = currentIndex < tasks.Count ? tasks[currentIndex].state.ToString() : currentState.ToString();
            FailCurrent($"[{where}] {exception.Message}\n{exception.StackTrace}");
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
            LYNOOKSceneService.UpdateSceneStatusAsync(task.item.id, LynookConvertStatuses.Failed, null, null, error, (_, __) => { });
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
