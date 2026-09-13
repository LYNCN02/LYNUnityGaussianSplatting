#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lynook.DualScreen.Editor
{
    [InitializeOnLoad]
    public static class LYNOOKRecordingMenu
    {
        const string PendingSceneKey = "LYNOOK.Recording.PendingScene";
        const string PendingFolderKey = "LYNOOK.Recording.PendingFolder";
        static readonly string[] ScenePaths = {
            LYNOOKSceneCatalog.RoomPreview,
            LYNOOKSceneCatalog.SeamCalibration,
            LYNOOKSceneCatalog.SpatialDepth,
            LYNOOKSceneCatalog.CornerRoom
        };

        static LYNOOKRecordingMenu()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("Tools/LYNOOK/Record/00 Room Preview - Perspective")]
        public static void RecordPerspective() => RecordScene(LYNOOKSceneCatalog.RoomPreview);

        [MenuItem("Tools/LYNOOK/Record/01 Dual Screen - Seam Calibration")]
        public static void RecordSeam() => RecordScene(LYNOOKSceneCatalog.SeamCalibration);

        [MenuItem("Tools/LYNOOK/Record/02 Dual Screen - Depth Test")]
        public static void RecordDepth() => RecordScene(LYNOOKSceneCatalog.SpatialDepth);

        [MenuItem("Tools/LYNOOK/Record/03 Corner Room - View 45")]
        public static void RecordCorner() => RecordScene(LYNOOKSceneCatalog.CornerRoom);

        [MenuItem("Tools/LYNOOK/Record Current Scene", false, 50)]
        public static void RecordCurrentScene() => RecordScene(SceneManager.GetActiveScene().path);

        [MenuItem("Tools/LYNOOK/Record/00 Room Preview - Perspective", true)]
        [MenuItem("Tools/LYNOOK/Record/01 Dual Screen - Seam Calibration", true)]
        [MenuItem("Tools/LYNOOK/Record/02 Dual Screen - Depth Test", true)]
        [MenuItem("Tools/LYNOOK/Record/03 Corner Room - View 45", true)]
        static bool CanRecord() => !EditorApplication.isPlayingOrWillChangePlaymode
            && !EditorApplication.isCompiling;

        [MenuItem("Tools/LYNOOK/Record Current Scene", true)]
        static bool CanRecordCurrentScene() => CanRecord()
            && ScenePaths.Contains(SceneManager.GetActiveScene().path);

        public static void RecordScene(string scenePath)
        {
            if (!CanRecord())
                throw new InvalidOperationException("Wait for the current recording or Play Mode to finish before recording again.");
            if (!ScenePaths.Contains(scenePath)
                || AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new FileNotFoundException("Select one of the four LYNOOK room scenes.", scenePath);
            if (SceneManager.GetActiveScene().path != scenePath || SceneManager.sceneCount != 1)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }

            // Recording the already-open scene needs no disk save: Play Mode restores
            // its current edits, and the exporter captures these same in-memory cameras.

            // SessionState survives script reload when entering Play Mode. Only this
            // explicit request starts a recording; pressing Play has no pending request.
            string folder = Path.Combine("Recordings", "LYNOOK", Path.GetFileNameWithoutExtension(scenePath),
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            SessionState.SetString(PendingSceneKey, scenePath);
            SessionState.SetString(PendingFolderKey, folder);
            try
            {
                EditorApplication.EnterPlaymode();
            }
            catch
            {
                ClearRequest();
                throw;
            }
        }

        static void ClearRequest()
        {
            SessionState.EraseString(PendingSceneKey);
            SessionState.EraseString(PendingFolderKey);
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                ClearRequest();
                return;
            }
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;
            string path = SessionState.GetString(PendingSceneKey, string.Empty);
            string folder = SessionState.GetString(PendingFolderKey, string.Empty);
            ClearRequest();
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                Scene scene = SceneManager.GetActiveScene();
                if (scene.path != path)
                    throw new InvalidOperationException("The active scene changed before recording could start.");
                if (path == LYNOOKSceneCatalog.CornerRoom)
                {
                    FindSession<LYNOOKCornerBoxRecordingSession>(scene).BeginRecording(folder);
                }
                else if (path == LYNOOKSceneCatalog.RoomPreview)
                {
                    Camera[] cameras = scene.GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<Camera>()).ToArray();
                    Camera main = cameras.Single(camera => camera.CompareTag("MainCamera"));
                    Camera side = cameras.Single(camera => camera.name == "SideCamera");
                    var host = new GameObject("LYNOOK_PerspectiveRecordingSession");
                    var session = host.AddComponent<LYNOOKDualRecordingSession>();
                    session.SetPerspectiveCameras(main, side);
                    session.SetOutputNames("main_perspective", "right_perspective", "perspective_preview.mov");
                    session.BeginRecording(folder);
                }
                else
                {
                    FindSession<LYNOOKDualRecordingSession>(scene).BeginRecording(folder);
                }
                Debug.Log("LYNOOK recording output: " + Path.GetFullPath(Path.Combine(Application.dataPath, "..", folder)));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.ExitPlaymode();
            }
        }

        static T FindSession<T>(Scene scene) where T : MonoBehaviour
        {
            return scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>())
                .Single(session => session.isActiveAndEnabled);
        }
    }
}
#endif
