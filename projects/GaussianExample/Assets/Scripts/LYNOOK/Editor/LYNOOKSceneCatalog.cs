#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKSceneCatalog
    {
        public const string SceneFolder = "Assets/LYNOOK/Scenes";
        public const string RoomPreview = SceneFolder + "/00_RoomPreview_Perspective.unity";
        public const string SeamCalibration = SceneFolder + "/01_DualScreen_SeamCalibration.unity";
        public const string SpatialDepth = SceneFolder + "/02_DualScreen_DepthTest.unity";
        public const string CornerRoom = SceneFolder + "/03_CornerRoom_View45.unity";

        [MenuItem("Tools/LYNOOK/Room Scenes/00 Room Preview - Perspective/Open Scene")]
        static void OpenRoomPreview() => OpenScene(RoomPreview);

        [MenuItem("Tools/LYNOOK/Room Scenes/01 Dual Screen - Seam Calibration/Open Scene")]
        static void OpenSeamCalibration() => OpenScene(SeamCalibration);

        [MenuItem("Tools/LYNOOK/Room Scenes/02 Dual Screen - Depth Test/Open Scene")]
        static void OpenSpatialDepth() => OpenScene(SpatialDepth);

        [MenuItem("Tools/LYNOOK/Room Scenes/03 Corner Room - View 45/Open Scene")]
        static void OpenCornerRoom() => OpenScene(CornerRoom);

        static void OpenScene(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new FileNotFoundException("Room scene not found. Check the scene directory.", path);
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        }
    }
}
#endif
