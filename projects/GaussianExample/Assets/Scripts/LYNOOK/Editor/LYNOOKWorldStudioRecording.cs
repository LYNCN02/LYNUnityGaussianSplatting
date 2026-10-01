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

        static LYNOOKWorldStudioRecording() => EditorApplication.playModeStateChanged += StateChanged;

        public static void Start(LYNOOKWorldAuthoring world) => StartInternal(world, false);
        public static void StartPreview(LYNOOKWorldAuthoring world) => StartInternal(world, true);

        static void StartInternal(LYNOOKWorldAuthoring world, bool previewOnly)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("请等待当前运行结束。");
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
            File.WriteAllText(folder + "/status.json", "{\"state\":\"recording\"}");
            SessionState.SetString(PendingScene, world.gameObject.scene.path);
            SessionState.SetString(PendingFolder, folder);
            SessionState.SetBool(PreviewOnly, previewOnly);
            try { EditorApplication.EnterPlaymode(); }
            catch
            {
                SessionState.EraseString(PendingScene); SessionState.EraseString(PendingFolder);
                File.WriteAllText(folder + "/status.json", "{\"state\":\"failed\"}");
                throw;
            }
        }

        static void StateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                string scene = SessionState.GetString(PendingScene, "");
                string folder = SessionState.GetString(PendingFolder, "");
                SessionState.EraseString(PendingScene); SessionState.EraseString(PendingFolder);
                if (string.IsNullOrEmpty(scene)) return;
                SessionState.SetString(ActiveFolder, folder);
                try
                {
                    var world = LYNOOKWorldStudioService.Current;
                    if (world == null || world.gameObject.scene.path != scene) throw new InvalidOperationException("制作场景已切换，录制取消。");
                    foreach (var renderer in world.collisionObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                    world.cameraRig.ApplyConfiguration();
                    WritePreview(world.cameraRig.MainCaptureCamera, folder + "/preview.png");
                    var session = world.cameraRig.GetComponent<LYNOOKDualRecordingSession>();
                    session.SetReferences(world.cameraRig, null);
                    session.SetOutputNames("main", "right", "preview.mov");
                    session.BeginRecording(folder, !SessionState.GetBool(PreviewOnly, false));
                }
                catch (Exception exception) { Debug.LogException(exception); EditorApplication.ExitPlaymode(); }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                string folder = SessionState.GetString(ActiveFolder, "");
                SessionState.EraseString(ActiveFolder);
                if (string.IsNullOrEmpty(folder)) return;
                bool previewOnly = SessionState.GetBool(PreviewOnly, false);
                SessionState.EraseBool(PreviewOnly);
                bool filesPresent = true;
                string[] required = previewOnly ? new[] { "main.mp4", "right.mp4", "preview.png" }
                    : new[] { "main.mp4", "right.mp4", "world_config.json", "mesh/collision.glb", "preview.png" };
                foreach (string file in required)
                    filesPresent &= File.Exists(folder + "/" + file) && new FileInfo(folder + "/" + file).Length > 0;
                File.WriteAllText(folder + "/status.json", filesPresent
                    ? (previewOnly ? "{\"state\":\"preview_only\",\"filesPresent\":true,\"deliverable\":false}"
                        : "{\"state\":\"needs_review\",\"filesPresent\":true,\"deviceValidated\":false}")
                    : "{\"state\":\"failed\",\"filesPresent\":false}");
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
