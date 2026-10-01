#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lynook.DualScreen.Editor
{
    [InitializeOnLoad]
    public static class LYNOOKWorldStudioRecording
    {
        const string PendingScene = "LYNOOK.WorldStudio.PendingScene";
        const string PendingFolder = "LYNOOK.WorldStudio.PendingFolder";
        const string ActiveFolder = "LYNOOK.WorldStudio.ActiveFolder";
        const string PreviewOnly = "LYNOOK.WorldStudio.PreviewOnly";
        const string PendingSource = "LYNOOK.WorldStudio.PendingSource";
        const string PendingBatchSceneId = "LYNOOK.WorldStudio.PendingBatchSceneId";
        const string ActiveSource = "LYNOOK.WorldStudio.ActiveSource";
        const string ActiveBatchSceneId = "LYNOOK.WorldStudio.ActiveBatchSceneId";

        static LYNOOKWorldStudioRecording() => EditorApplication.playModeStateChanged += StateChanged;

        /// <summary>本地独立录制（非批量），world_config.json 记 source=manual。</summary>
        public static void Start(LYNOOKWorldAuthoring world) =>
            StartInternal(world, false, LYNOOKRecordingWorldExporter.SourceManual, 0);

        /// <summary>批量队列录制，world_config.json 记 source=batch 与队列 scenes.id。</summary>
        public static void StartBatch(LYNOOKWorldAuthoring world, int batchSceneId) =>
            StartInternal(world, false, LYNOOKRecordingWorldExporter.SourceBatch, batchSceneId);

        public static void StartPreview(LYNOOKWorldAuthoring world) =>
            StartInternal(world, true, LYNOOKRecordingWorldExporter.SourceManual, 0);

        static void StartInternal(LYNOOKWorldAuthoring world, bool previewOnly, string source, int batchSceneId)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("请等待当前运行结束。");
            bool isBatch = source == LYNOOKRecordingWorldExporter.SourceBatch;
            if (!previewOnly) Debug.Log(LYNOOKWorldPersistence.ValidateForRecording(world));
            else if (world == null || world.cameraRig == null || !world.gaussian.HasValidAsset)
                throw new InvalidOperationException("请先导入完整房间。");
            var session = world.cameraRig.GetComponent<LYNOOKDualRecordingSession>();
            if (session == null) throw new InvalidOperationException("缺少录制组件。");
            var serialized = new SerializedObject(session);
            serialized.FindProperty("frameRate").intValue = 30;
            serialized.FindProperty("frameCount").intValue = (previewOnly ? 2 : world.recordingSeconds) * 30;
            // The local workflow has no external encoder prerequisite.
            serialized.FindProperty("outputFormat").enumValueIndex = (int)LYNOOKMovieOutputFormat.H264Mp4;
            serialized.ApplyModifiedProperties();
            LYNOOKWorldPersistence.SaveDraft(world);
            string folder = Path.GetFullPath("Recordings/LYNOOK/WorldStudio/" + world.worldId + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(folder + "/mesh");
            File.Copy(world.collisionSourcePath, folder + "/mesh/collision.glb");
            File.Copy(world.workspacePath + "/world_draft.json", folder + "/world_draft.json");
            File.Copy(world.workspacePath + "/camera_calibration.json", folder + "/camera_calibration.json");
            // status.json 初始即体现录制来源（batch/manual）与批量队列场景 id。
            File.WriteAllText(folder + "/status.json",
                "{\"state\":\"recording\",\"source\":\"" + source + "\"" +
                (isBatch ? ",\"batchSceneId\":" + batchSceneId : "") + "}");
            SessionState.SetString(PendingScene, world.gameObject.scene.path);
            SessionState.SetString(PendingFolder, folder);
            SessionState.SetBool(PreviewOnly, previewOnly);
            SessionState.SetString(PendingSource, source);
            SessionState.SetInt(PendingBatchSceneId, batchSceneId);
            try { EditorApplication.EnterPlaymode(); }
            catch
            {
                SessionState.EraseString(PendingScene); SessionState.EraseString(PendingFolder);
                SessionState.EraseString(PendingSource); SessionState.EraseInt(PendingBatchSceneId);
                File.WriteAllText(folder + "/status.json", "{\"state\":\"failed\",\"source\":\"" + source + "\"}");
                throw;
            }
        }

        static void StateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                string scene = SessionState.GetString(PendingScene, "");
                string folder = SessionState.GetString(PendingFolder, "");
                string pendingSource = SessionState.GetString(PendingSource, LYNOOKRecordingWorldExporter.SourceManual);
                int pendingBatchSceneId = SessionState.GetInt(PendingBatchSceneId, 0);
                SessionState.EraseString(PendingScene); SessionState.EraseString(PendingFolder);
                SessionState.EraseString(PendingSource); SessionState.EraseInt(PendingBatchSceneId);
                if (string.IsNullOrEmpty(scene)) return;
                SessionState.SetString(ActiveFolder, folder);
                // 来源在整个 PlayMode 期间保留，回 EditMode 写最终 status.json 时使用。
                SessionState.SetString(ActiveSource, pendingSource);
                SessionState.SetInt(ActiveBatchSceneId, pendingBatchSceneId);
                try
                {
                    var world = LYNOOKWorldStudioService.Current;
                    if (world == null || world.gameObject.scene.path != scene) throw new InvalidOperationException("制作场景已切换，录制取消。");
                    foreach (var renderer in world.collisionObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                    world.cameraRig.ApplyConfiguration();
                    // 正面第一帧与侧面第一帧各出一张预览。
                    WritePreview(world.cameraRig.MainCaptureCamera, folder + "/preview.png");
                    WritePreview(world.cameraRig.SideCaptureCamera, folder + "/preview_right.png");
                    var session = world.cameraRig.GetComponent<LYNOOKDualRecordingSession>();
                    session.SetReferences(world.cameraRig, null);
                    session.SetOutputNames("main", "right", "preview.mov");
                    session.BeginRecording(folder, !SessionState.GetBool(PreviewOnly, false),
                        pendingSource, pendingBatchSceneId);
                }
                catch (Exception exception) { Debug.LogException(exception); EditorApplication.ExitPlaymode(); }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                string folder = SessionState.GetString(ActiveFolder, "");
                string activeSource = SessionState.GetString(ActiveSource, LYNOOKRecordingWorldExporter.SourceManual);
                int activeBatchSceneId = SessionState.GetInt(ActiveBatchSceneId, 0);
                SessionState.EraseString(ActiveFolder);
                SessionState.EraseString(ActiveSource); SessionState.EraseInt(ActiveBatchSceneId);
                if (string.IsNullOrEmpty(folder)) return;
                bool previewOnly = SessionState.GetBool(PreviewOnly, false);
                SessionState.EraseBool(PreviewOnly);
                bool isBatch = activeSource == LYNOOKRecordingWorldExporter.SourceBatch;
                string sourceTag = "\"source\":\"" + activeSource + "\"" +
                    (isBatch && activeBatchSceneId > 0 ? ",\"batchSceneId\":" + activeBatchSceneId : "");
                bool filesPresent = true;
                // 2 秒取景预览保留 MP4 + 正面首帧；交付录制校验 MOV + world_config +
                // 碰撞 GLB + 正/侧首帧预览（无 mp4）。
                string[] required = previewOnly
                    ? new[] { "main.mp4", "right.mp4", "preview.png" }
                    : new[] { "main.mov", "right.mov", "world_config.json",
                        "mesh/collision.glb", "preview.png", "preview_right.png" };
                foreach (string file in required)
                    filesPresent &= File.Exists(folder + "/" + file) && new FileInfo(folder + "/" + file).Length > 0;
                File.WriteAllText(folder + "/status.json", filesPresent
                    ? (previewOnly ? "{\"state\":\"preview_only\",\"filesPresent\":true,\"deliverable\":false," + sourceTag + "}"
                        : "{\"state\":\"needs_review\",\"filesPresent\":true,\"deviceValidated\":false," + sourceTag + "}")
                    : "{\"state\":\"failed\",\"filesPresent\":false," + sourceTag + "}");
                if (filesPresent) Debug.Log((previewOnly ? "LYNOOK World Studio 2 秒取景预览已生成（非交付包）：" : "LYNOOK World Studio 本地房间包已生成，待人工检查视频和设备效果：") + folder);
                else Debug.LogError("LYNOOK World Studio 录制未完整完成，可重新录制。输出目录：" + folder);
            }
        }

        static void WritePreview(Camera camera, string path)
        {
            var previous = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                camera.Render();
                RenderTexture.active = camera.targetTexture;
                texture = new Texture2D(camera.targetTexture.width, camera.targetTexture.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
#endif
