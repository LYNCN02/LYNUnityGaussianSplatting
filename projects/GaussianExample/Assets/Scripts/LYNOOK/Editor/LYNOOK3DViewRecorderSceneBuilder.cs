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
    /// <summary>
    /// Creates a separate depth/parallax recording scene next to the seam-calibration scene.
    /// It deliberately reuses the calibrated shared-eye camera prefab but has independent
    /// output names, so running this test cannot overwrite the seam-test recordings.
    /// </summary>
    public static class LYNOOK3DViewRecorderSceneBuilder
    {
        const string Root = "Assets/LYNOOK/DualScreenRecorder";
        const string SourceScenePath = "Assets/GSTestScene.unity";
        const string PrefabPath = Root + "/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string ScenePath = Root + "/Scenes/LYNOOK_DualScreen_3DViewTest.unity";
        const float TestDurationSeconds = 10f;

        [MenuItem("Tools/LYNOOK/Create or Open 3D View Recording Test Scene")]
        public static void CreateOrOpenScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                CreateScene();
            else
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Selection.activeGameObject = GameObject.Find("LYNOOK_3DViewRecordingTest");
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log($"LYNOOK 3D-view recording test scene is ready: {ScenePath}");
        }

        [MenuItem("Tools/LYNOOK/Open and Record 3D View Pair")]
        public static void OpenAndRecord()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before starting the LYNOOK 3D-view recorder.");

            CreateOrOpenScene();
            EditorApplication.EnterPlaymode();
        }

        // Command-line entry for a reproducible asset-generation and compilation check.
        public static void CreateSceneFromCommandLine()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                CreateScene();
            else
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void CreateScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScenePath) == null)
                throw new FileNotFoundException("GaussianExample source scene was not found.", SourceScenePath);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
                throw new FileNotFoundException(
                    "The LYNOOK recorder prefab is missing. Build the Off-Axis recorder assets first.",
                    PrefabPath);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                throw new InvalidOperationException($"The target scene already exists: {ScenePath}");

            if (!AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
                throw new IOException($"Failed to copy {SourceScenePath} to {ScenePath}.");
            AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceSynchronousImport);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Camera[] sourceCameras = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Transform sourceView = sourceCameras.FirstOrDefault(camera => camera.CompareTag("MainCamera"))?.transform;
            Vector3 rigPosition = sourceView != null ? sourceView.position : new Vector3(0f, 1.6f, -6f);
            Quaternion rigRotation = sourceView != null ? sourceView.rotation : Quaternion.identity;

            foreach (Camera camera in sourceCameras)
            {
                camera.enabled = false;
                var listener = camera.GetComponent<AudioListener>();
                if (listener != null)
                    listener.enabled = false;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var recorderRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var rig = recorderRoot.GetComponent<LYNOOKDualCameraRig>();
            if (rig == null)
                throw new MissingComponentException("The recorder prefab has no LYNOOKDualCameraRig.");
            rig.CaptureRig.SetPositionAndRotation(rigPosition, rigRotation);
            rig.ApplyConfiguration();

            var contentRoot = new GameObject("LYNOOK_3DViewRecordingTest");
            SceneManager.MoveGameObjectToScene(contentRoot, scene);
            BuildDepthAndParallaxContent(
                rig,
                contentRoot.transform,
                out Transform depthProbe,
                out Transform orbitPivot,
                out Vector3 pathStart,
                out Vector3 pathControl,
                out Vector3 pathEnd);

            var motion = contentRoot.AddComponent<LYNOOK3DViewMotion>();
            motion.SetReferences(
                depthProbe,
                orbitPivot,
                pathStart,
                pathControl,
                pathEnd,
                TestDurationSeconds);

            var session = recorderRoot.AddComponent<LYNOOKDualRecordingSession>();
            session.SetReferences(rig, null);
            session.SetOutputNames(
                "main_3dview",
                "right_3dview",
                "3dview_physical_preview.mov");

            if (!rig.TryValidate(out string validationReport))
                throw new InvalidOperationException(validationReport);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath, true))
                throw new IOException($"Unity failed to save {ScenePath}.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static void BuildDepthAndParallaxContent(
            LYNOOKDualCameraRig rig,
            Transform root,
            out Transform depthProbe,
            out Transform orbitPivot,
            out Vector3 pathStart,
            out Vector3 pathControl,
            out Vector3 pathEnd)
        {
            Material cyan = LoadMaterial("Grid");
            Material orange = LoadMaterial("Beam");
            Material yellow = LoadMaterial("SeamMarker");
            Material moving = LoadMaterial("MovingSphere");
            Material green = LoadMaterial("DepthNear");
            Material purple = LoadMaterial("DepthMid");
            Material blue = LoadMaterial("DepthFar");

            rig.ApplyConfiguration();
            rig.GetScreenGeometries(out LYNOOKScreenGeometry main, out LYNOOKScreenGeometry side);
            Vector3 eye = rig.SharedEyeWorldPosition;

            var corridor = new GameObject("PerspectiveCorridor_NearMidFar").transform;
            corridor.SetParent(root, false);
            CreateAngularFrame(corridor, "DepthFrame_Near", eye, main, side, 24f, 0.035f, green);
            CreateAngularFrame(corridor, "DepthFrame_Mid", eye, main, side, 48f, 0.045f, purple);
            CreateAngularFrame(corridor, "DepthFrame_Far", eye, main, side, 82f, 0.06f, blue);

            BuildPerspectiveFloor(root, eye, main, side, cyan, yellow);

            Vector3 mainNearPoint = Vector3.Lerp(main.center, main.TopRight, 0.22f);
            Vector3 seamPoint = Vector3.Lerp(main.BottomRight, main.TopRight, 0.52f);
            Vector3 sideFarPoint = Vector3.Lerp(side.TopLeft, side.center, 0.72f);

            CreatePrimitive(
                root,
                PrimitiveType.Sphere,
                "Near_ParallaxSphere",
                ProjectFromEye(eye, Vector3.Lerp(main.center, main.BottomRight, 0.35f), 23f),
                Vector3.one * 0.72f,
                Quaternion.identity,
                green);
            CreatePrimitive(
                root,
                PrimitiveType.Cube,
                "Mid_ParallaxCube_OnSeam",
                ProjectFromEye(eye, seamPoint, 49f),
                new Vector3(1.05f, 1.05f, 1.05f),
                Quaternion.Euler(15f, 28f, 7f),
                purple);
            CreatePrimitive(
                root,
                PrimitiveType.Capsule,
                "Far_ParallaxCapsule",
                ProjectFromEye(eye, Vector3.Lerp(side.center, side.TopRight, 0.28f), 79f),
                new Vector3(0.9f, 1.8f, 0.9f),
                Quaternion.Euler(0f, -18f, 8f),
                blue);

            pathStart = ProjectFromEye(eye, mainNearPoint, 25f);
            pathControl = ProjectFromEye(eye, seamPoint, 47f) + main.up * 0.55f;
            pathEnd = ProjectFromEye(eye, sideFarPoint, 76f);
            depthProbe = CreatePrimitive(
                root,
                PrimitiveType.Sphere,
                "MovingProbe_AcrossSeamAndDepth",
                pathStart,
                Vector3.one * 0.58f,
                Quaternion.identity,
                moving).transform;

            orbitPivot = new GameObject("OrbitPivot_MidDepth").transform;
            orbitPivot.SetParent(root, false);
            orbitPivot.position = ProjectFromEye(eye, seamPoint, 58f);
            CreatePrimitiveLocal(
                orbitPivot,
                PrimitiveType.Sphere,
                "OrbitCenter",
                Vector3.zero,
                Vector3.one * 0.38f,
                Quaternion.identity,
                orange);
            CreatePrimitiveLocal(
                orbitPivot,
                PrimitiveType.Cube,
                "OrbitingCube",
                new Vector3(1.25f, 0.35f, 0f),
                Vector3.one * 0.42f,
                Quaternion.Euler(20f, 30f, 15f),
                yellow);

            BuildDepthAxis(root, eye, seamPoint, orange, yellow);
        }

        static void BuildPerspectiveFloor(
            Transform root,
            Vector3 eye,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            Material gridMaterial,
            Material accentMaterial)
        {
            var floor = new GameObject("PerspectiveFloorGrid").transform;
            floor.SetParent(root, false);
            Vector3 mainFloor = Vector3.Lerp(main.BottomLeft, main.BottomRight, 0.5f) + main.up * (main.heightMeters * 0.08f);
            Vector3 sideFloor = Vector3.Lerp(side.BottomLeft, side.BottomRight, 0.5f) + side.up * (side.heightMeters * 0.08f);

            float[] depthScales = { 18f, 25f, 34f, 46f, 60f, 78f, 98f };
            foreach (float scale in depthScales)
            {
                Vector3 left = ProjectFromEye(eye, main.BottomLeft + main.up * (main.heightMeters * 0.08f), scale);
                Vector3 right = ProjectFromEye(eye, side.BottomRight + side.up * (side.heightMeters * 0.08f), scale);
                CreateLine(floor, $"DepthGrid_{scale:000}", left, right, 0.018f, gridMaterial);
            }

            for (int i = 0; i <= 6; i++)
            {
                float t = i / 6f;
                Vector3 angularAnchor = t <= 0.7f
                    ? Vector3.Lerp(main.BottomLeft, main.BottomRight, t / 0.7f)
                    : Vector3.Lerp(side.BottomLeft, side.BottomRight, (t - 0.7f) / 0.3f);
                LYNOOKScreenGeometry screen = t <= 0.7f ? main : side;
                angularAnchor += screen.up * (screen.heightMeters * 0.08f);
                CreateLine(
                    floor,
                    $"PerspectiveRail_{i:00}",
                    ProjectFromEye(eye, angularAnchor, 18f),
                    ProjectFromEye(eye, angularAnchor, 98f),
                    i == 4 ? 0.03f : 0.015f,
                    i == 4 ? accentMaterial : gridMaterial);
            }

            CreateLine(
                floor,
                "FloorDirection_MainToSide",
                ProjectFromEye(eye, mainFloor, 18f),
                ProjectFromEye(eye, sideFloor, 98f),
                0.025f,
                accentMaterial);
        }

        static void BuildDepthAxis(
            Transform root,
            Vector3 eye,
            Vector3 angularPoint,
            Material lineMaterial,
            Material markerMaterial)
        {
            var axis = new GameObject("DepthAxis_AlongSeamRay").transform;
            axis.SetParent(root, false);
            Vector3 start = ProjectFromEye(eye, angularPoint, 18f);
            Vector3 end = ProjectFromEye(eye, angularPoint, 94f);
            CreateLine(axis, "DepthAxisLine", start, end, 0.025f, lineMaterial);
            float[] scales = { 22f, 34f, 48f, 64f, 82f, 94f };
            foreach (float scale in scales)
            {
                CreatePrimitive(
                    axis,
                    PrimitiveType.Sphere,
                    $"DepthTick_{scale:000}",
                    ProjectFromEye(eye, angularPoint, scale),
                    Vector3.one * 0.12f,
                    Quaternion.identity,
                    markerMaterial);
            }
        }

        static void CreateAngularFrame(
            Transform root,
            string name,
            Vector3 eye,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            float scale,
            float thickness,
            Material material)
        {
            var frame = new GameObject(name).transform;
            frame.SetParent(root, false);
            Vector3 topLeft = ProjectFromEye(eye, main.TopLeft, scale);
            Vector3 topRight = ProjectFromEye(eye, side.TopRight, scale);
            Vector3 bottomLeft = ProjectFromEye(eye, main.BottomLeft, scale);
            Vector3 bottomRight = ProjectFromEye(eye, side.BottomRight, scale);
            Vector3 seamTop = ProjectFromEye(eye, main.TopRight, scale);
            Vector3 seamBottom = ProjectFromEye(eye, main.BottomRight, scale);

            CreateLine(frame, "Top", topLeft, topRight, thickness, material);
            CreateLine(frame, "Bottom", bottomLeft, bottomRight, thickness, material);
            CreateLine(frame, "Left", bottomLeft, topLeft, thickness, material);
            CreateLine(frame, "Right", bottomRight, topRight, thickness, material);
            CreateLine(frame, "SeamDepthReference", seamBottom, seamTop, thickness * 0.75f, material);
        }

        static Vector3 ProjectFromEye(Vector3 eye, Vector3 screenPoint, float scale)
        {
            return eye + (screenPoint - eye) * scale;
        }

        static GameObject CreateLine(
            Transform parent,
            string name,
            Vector3 start,
            Vector3 end,
            float thickness,
            Material material)
        {
            Vector3 delta = end - start;
            if (delta.sqrMagnitude < 0.000001f)
                throw new ArgumentException($"Cannot create zero-length line: {name}");
            return CreatePrimitive(
                parent,
                PrimitiveType.Cylinder,
                name,
                (start + end) * 0.5f,
                new Vector3(thickness, delta.magnitude * 0.5f, thickness),
                Quaternion.FromToRotation(Vector3.up, delta.normalized),
                material);
        }

        static GameObject CreatePrimitive(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 position,
            Vector3 scale,
            Quaternion rotation,
            Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.position = position;
            primitive.transform.rotation = rotation;
            primitive.transform.localScale = scale;
            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            return primitive;
        }

        static GameObject CreatePrimitiveLocal(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localRotation = localRotation;
            primitive.transform.localScale = localScale;
            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            return primitive;
        }

        static Material LoadMaterial(string name)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                throw new FileNotFoundException($"Required LYNOOK material was not found: {path}", path);
            return material;
        }
    }
}
#endif
