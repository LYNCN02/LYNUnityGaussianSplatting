#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKDualRecorderBuilder
    {
        const string Root = "Assets/LYNOOK/DualScreenRecorder";
        const string MainRtPath = Root + "/RenderTextures/MainCaptureRT.renderTexture";
        const string SideRtPath = Root + "/RenderTextures/SideCaptureRT.renderTexture";
        const string PrefabPath = Root + "/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string MainRecorderPath = Root + "/Recorder/MainRecorderSettings.asset";
        const string SideRecorderPath = Root + "/Recorder/SideRecorderSettings.asset";
        const string ControllerPath = Root + "/Recorder/DualRecorderControllerSettings.asset";
        const string AnimationPath = Root + "/Timeline/SeamCrossingAnimation.anim";
        const string TimelinePath = Root + "/Timeline/LYNOOK_SeamTestTimeline.playable";
        const string ScenePath = Root + "/Scenes/LYNOOK_DualScreen_SeamTest.unity";
        const string SourceScenePath = "Assets/GSTestScene.unity";

        const float TestDurationSeconds = 10f;

        [MenuItem("Tools/LYNOOK/Rebuild Off-Axis Recorder Assets")]
        public static void BuildAssets()
        {
            EnsureFolders();

            var mainRt = CreateOrUpdateRenderTexture(
                MainRtPath,
                LYNOOKDualCameraRig.MainWidth,
                LYNOOKDualCameraRig.MainHeight);
            var sideRt = CreateOrUpdateRenderTexture(
                SideRtPath,
                LYNOOKDualCameraRig.SideWidth,
                LYNOOKDualCameraRig.SideHeight);

            CreateOrUpdatePrefab(mainRt, sideRt);
            CreateOrUpdateRecorderAssets(mainRt, sideRt);
            CreateOrUpdateTestScene();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"LYNOOK recorder assets built. Test scene: {ScenePath}");
        }

        [MenuItem("Tools/LYNOOK/Rebuild and Record Off-Axis Pair")]
        public static void BuildAndRecord()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before starting the LYNOOK recorder.");

            BuildAssets();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        [MenuItem("Tools/LYNOOK/Validate Open Off-Axis Rig")]
        public static void ValidateOpenRig()
        {
            var rig = UnityEngine.Object.FindFirstObjectByType<LYNOOKDualCameraRig>(FindObjectsInactive.Include);
            if (rig == null)
                throw new InvalidOperationException("The open scene does not contain a LYNOOKDualCameraRig.");

            rig.ApplyConfiguration();
            if (!rig.TryValidate(out string report))
                throw new InvalidOperationException(report);
            Debug.Log(report);
        }

        public static void SetCurrentRecorderToH264AndFfmpegMov()
        {
            var mainRt = AssetDatabase.LoadAssetAtPath<RenderTexture>(MainRtPath);
            var sideRt = AssetDatabase.LoadAssetAtPath<RenderTexture>(SideRtPath);
            if (mainRt == null || sideRt == null)
                throw new InvalidOperationException("Build the LYNOOK recorder assets before selecting MOV output.");

            CreateOrUpdateRecorderAssets(mainRt, sideRt);

            var sessions = UnityEngine.Object.FindObjectsByType<LYNOOKDualRecordingSession>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var session in sessions)
            {
                var serializedSession = new SerializedObject(session);
                var outputFormat = serializedSession.FindProperty("outputFormat");
                if (outputFormat != null)
                    outputFormat.enumValueIndex = (int)LYNOOKMovieOutputFormat.H264Mp4AndFfmpegMov;
                serializedSession.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(session);
                EditorSceneManager.MarkSceneDirty(session.gameObject.scene);
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("LYNOOK output set to H.264 MP4 + automatic FFmpeg MOV remux: main_offaxis.mov + right_offaxis.mov");
        }

        public static void AttachCameraJsonExporter()
        {
            var prefabContents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                if (prefabContents.GetComponent<CameraRigTransformCopy>() == null)
                    prefabContents.AddComponent<CameraRigTransformCopy>();
                PrefabUtility.SaveAsPrefabAsset(prefabContents, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabContents);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            bool sceneChanged = false;
            var rigs = UnityEngine.Object.FindObjectsByType<LYNOOKDualCameraRig>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var rig in rigs)
            {
                if (rig.GetComponent<CameraRigTransformCopy>() != null)
                    continue;

                UnityEditor.Undo.AddComponent<CameraRigTransformCopy>(rig.gameObject);
                EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
                sceneChanged = true;
            }

            if (sceneChanged)
                EditorSceneManager.SaveOpenScenes();

            foreach (var rig in rigs)
            {
                var exporter = rig.GetComponent<CameraRigTransformCopy>();
                if (exporter != null)
                    Debug.Log("LYNOOK camera JSON exporter verification:\n" + exporter.ToJson());
            }

            Debug.Log("CameraRigTransformCopy attached to LYNOOK_DualCameraRecorder and linked to both capture cameras.");
        }

        // Command-line entry used for reproducible build/compile checks and the actual capture.
        public static void BuildAndRecordFromCommandLine()
        {
            BuildAndRecord();
        }

        public static void BuildOnlyFromCommandLine()
        {
            BuildAssets();
        }

        static void EnsureFolders()
        {
            EnsureFolder("Assets/LYNOOK");
            EnsureFolder(Root);
            EnsureFolder(Root + "/RenderTextures");
            EnsureFolder(Root + "/Prefabs");
            EnsureFolder(Root + "/Recorder");
            EnsureFolder(Root + "/Scenes");
            EnsureFolder(Root + "/Timeline");
            EnsureFolder(Root + "/Materials");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException($"Invalid asset folder: {path}");

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        static RenderTexture CreateOrUpdateRenderTexture(string path, int width, int height)
        {
            var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (texture == null)
            {
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = Path.GetFileNameWithoutExtension(path)
                };
                AssetDatabase.CreateAsset(texture, path);
            }

            texture.Release();
            texture.width = width;
            texture.height = height;
            texture.depth = 24;
            texture.format = RenderTextureFormat.ARGB32;
            texture.antiAliasing = 1;
            texture.useMipMap = false;
            texture.autoGenerateMips = false;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            EditorUtility.SetDirty(texture);
            return texture;
        }

        static void CreateOrUpdatePrefab(RenderTexture mainRt, RenderTexture sideRt)
        {
            var root = new GameObject("LYNOOK_DualCameraRecorder");
            try
            {
                var captureRig = new GameObject("CaptureRig").transform;
                captureRig.SetParent(root.transform, false);

                var mainCameraObject = new GameObject("MainCaptureCamera");
                mainCameraObject.transform.SetParent(captureRig, false);
                mainCameraObject.tag = "MainCamera";
                var mainCamera = mainCameraObject.AddComponent<Camera>();
                mainCameraObject.AddComponent<AudioListener>();
                ConfigureReferenceCamera(mainCamera);

                var sideCameraObject = new GameObject("SideCaptureCamera");
                sideCameraObject.transform.SetParent(captureRig, false);
                var sideCamera = sideCameraObject.AddComponent<Camera>();

                var rig = root.AddComponent<LYNOOKDualCameraRig>();
                rig.SetReferences(captureRig, mainCamera, sideCamera, mainRt, sideRt);
                root.AddComponent<CameraRigTransformCopy>();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void ConfigureReferenceCamera(Camera camera)
        {
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.19215687f, 0.3019608f, 0.4745098f, 1f);
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            camera.cullingMask = ~0;
            camera.renderingPath = RenderingPath.UsePlayerSettings;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.allowDynamicResolution = false;
            camera.depth = 0f;
            camera.useOcclusionCulling = true;
            camera.orthographic = false;
            camera.usePhysicalProperties = false;
            camera.fieldOfView = LYNOOKDualCameraRig.VerticalFieldOfView;
        }

        static void CreateOrUpdateRecorderAssets(RenderTexture mainRt, RenderTexture sideRt)
        {
            var mainSettings = CreateOrUpdateMovieSettings(
                MainRecorderPath,
                "MainRecorderSettings",
                mainRt,
                "main_offaxis");
            var sideSettings = CreateOrUpdateMovieSettings(
                SideRecorderPath,
                "SideRecorderSettings",
                sideRt,
                "right_offaxis");

            var controller = AssetDatabase.LoadAssetAtPath<RecorderControllerSettings>(ControllerPath);
            if (controller == null)
            {
                controller = ScriptableObject.CreateInstance<RecorderControllerSettings>();
                controller.name = "LYNOOK Dual Recorder Controller";
                AssetDatabase.CreateAsset(controller, ControllerPath);
            }

            controller.FrameRatePlayback = FrameRatePlayback.Constant;
            controller.FrameRate = 30f;
            controller.CapFrameRate = true;
            controller.ExitPlayMode = true;
            controller.SetRecordModeToFrameInterval(0, 299);

            var serializedController = new SerializedObject(controller);
            var recorders = serializedController.FindProperty("m_RecorderSettings");
            recorders.arraySize = 2;
            recorders.GetArrayElementAtIndex(0).objectReferenceValue = mainSettings;
            recorders.GetArrayElementAtIndex(1).objectReferenceValue = sideSettings;
            serializedController.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);
        }

        static MovieRecorderSettings CreateOrUpdateMovieSettings(
            string assetPath,
            string recorderName,
            RenderTexture input,
            string outputFileName)
        {
            var settings = AssetDatabase.LoadAssetAtPath<MovieRecorderSettings>(assetPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
                AssetDatabase.CreateAsset(settings, assetPath);
            }

            settings.name = recorderName;
            settings.Enabled = true;
            settings.CaptureAudio = false;
            settings.CaptureAlpha = false;
            settings.FrameRate = 30f;
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.ImageInputSettings = new RenderTextureInputSettings
            {
                RenderTexture = input,
                FlipFinalOutput = false
            };
            settings.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
                EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
                GopSize = 30,
                NumConsecutiveBFrames = 2
            };
            settings.OutputFile = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "Recordings/LYNOOK",
                outputFileName));
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void CreateOrUpdateTestScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScenePath) == null)
                throw new FileNotFoundException("GaussianExample source scene was not found.", SourceScenePath);

            var scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

            var sourceCameras = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Transform sweetSpot = sourceCameras.FirstOrDefault(camera => camera.CompareTag("MainCamera"))?.transform;
            Vector3 rigPosition = sweetSpot != null ? sweetSpot.position : new Vector3(0f, 1.6f, -6f);
            Quaternion rigRotation = sweetSpot != null ? sweetSpot.rotation : Quaternion.identity;

            foreach (var camera in sourceCameras)
            {
                camera.enabled = false;
                var listener = camera.GetComponent<AudioListener>();
                if (listener != null)
                    listener.enabled = false;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var recorderRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var rig = recorderRoot.GetComponent<LYNOOKDualCameraRig>();
            rig.CaptureRig.SetPositionAndRotation(rigPosition, rigRotation);
            rig.ApplyConfiguration();

            var geometryRoot = new GameObject("LYNOOK_SeamContinuityTestGeometry");
            SceneManager.MoveGameObjectToScene(geometryRoot, scene);
            geometryRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            BuildSeamGeometry(
                rig,
                geometryRoot.transform,
                out var movingSphere,
                out var motionStart,
                out var motionSeam,
                out var motionEnd);
            var director = BuildTimeline(geometryRoot, movingSphere, motionStart, motionSeam, motionEnd);
            var seamMotion = geometryRoot.AddComponent<LYNOOKSeamMotion>();
            seamMotion.SetReferences(director, movingSphere.transform);
            seamMotion.SetPath(motionStart, motionSeam, motionEnd);

            var session = recorderRoot.AddComponent<LYNOOKDualRecordingSession>();
            session.SetReferences(rig, director);

            if (!rig.TryValidate(out var validationReport))
                throw new InvalidOperationException(validationReport);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath, true);
        }

        static void BuildSeamGeometry(
            LYNOOKDualCameraRig rig,
            Transform root,
            out GameObject movingSphere,
            out Vector3 motionStart,
            out Vector3 motionSeam,
            out Vector3 motionEnd)
        {
            var gridMaterial = CreateOrUpdateMaterial("Grid", new Color(0.12f, 0.65f, 0.95f), true);
            var beamMaterial = CreateOrUpdateMaterial("Beam", new Color(1f, 0.35f, 0.05f), true);
            var markerMaterial = CreateOrUpdateMaterial("SeamMarker", new Color(1f, 0.95f, 0.05f), true);
            var movingMaterial = CreateOrUpdateMaterial("MovingSphere", new Color(0.05f, 0.95f, 1f), true);
            var nearMaterial = CreateOrUpdateMaterial("DepthNear", new Color(0.1f, 1f, 0.3f), true);
            var midMaterial = CreateOrUpdateMaterial("DepthMid", new Color(0.8f, 0.15f, 1f), true);
            var farMaterial = CreateOrUpdateMaterial("DepthFar", new Color(0.1f, 0.45f, 1f), true);

            rig.ApplyConfiguration();
            rig.GetScreenGeometries(out var mainScreen, out var sideScreen);
            Vector3 eye = rig.SharedEyeWorldPosition;
            const float testDepthScale = 42f;

            // Three true 3D horizontal lines cross the seam near its top, middle and bottom.
            // The right endpoints use the same physical height on the taller side panel, so
            // its extra 2.76 mm remains visible only below the main-panel bottom.
            CreateHorizontalSeamLine(root, "Horizontal_Top_SeamCheck", 0.88f, testDepthScale, eye, mainScreen, sideScreen, beamMaterial);
            CreateHorizontalSeamLine(root, "Horizontal_Middle_SeamCheck", 0.50f, testDepthScale, eye, mainScreen, sideScreen, markerMaterial);
            CreateHorizontalSeamLine(root, "Horizontal_Bottom_SeamCheck", 0.12f, testDepthScale, eye, mainScreen, sideScreen, beamMaterial);

            Vector3 seamBottom = ProjectFromEye(eye, mainScreen.BottomRight, testDepthScale);
            Vector3 seamTop = ProjectFromEye(eye, mainScreen.TopRight, testDepthScale);
            CreateLine(root, "Vertical_AlongFullSeam", seamBottom, seamTop, 0.035f, markerMaterial);

            Vector3 diagonalStart = ProjectFromEye(eye, mainScreen.BottomLeft, testDepthScale);
            Vector3 diagonalEnd = ProjectFromEye(eye, sideScreen.TopRight, testDepthScale);
            CreateLine(root, "Diagonal_AcrossFullSeam", diagonalStart, diagonalEnd, 0.045f, gridMaterial);
            Vector3 reverseStart = ProjectFromEye(eye, mainScreen.TopLeft, testDepthScale * 1.08f);
            Vector3 reverseEnd = ProjectFromEye(eye, sideScreen.BottomRight, testDepthScale * 1.08f);
            CreateLine(root, "Diagonal_ReverseAcrossSeam", reverseStart, reverseEnd, 0.035f, gridMaterial);

            Vector3 seamCenterOnScreen = Vector3.Lerp(mainScreen.BottomRight, mainScreen.TopRight, 0.5f);
            Vector3 seamRay = (seamCenterOnScreen - eye).normalized;
            CreatePrimitive(root, PrimitiveType.Sphere, "DepthReference_Near", eye + seamRay * 3.2f, Vector3.one * 0.55f, Quaternion.identity, nearMaterial);
            CreatePrimitive(root, PrimitiveType.Cube, "DepthReference_Mid", eye + seamRay * 6.0f, Vector3.one * 0.8f, Quaternion.identity, midMaterial);
            CreatePrimitive(root, PrimitiveType.Capsule, "DepthReference_Far", eye + seamRay * 10.0f, new Vector3(0.65f, 1.3f, 0.65f), Quaternion.identity, farMaterial);

            motionStart = ProjectFromEye(eye, Vector3.Lerp(mainScreen.center, mainScreen.TopRight, 0.65f), 35f);
            motionSeam = ProjectFromEye(eye, seamCenterOnScreen, 35f);
            motionEnd = ProjectFromEye(eye, Vector3.Lerp(sideScreen.TopLeft, sideScreen.center, 0.8f), 35f);
            movingSphere = CreatePrimitive(
                root,
                PrimitiveType.Sphere,
                "MovingSphere_AcrossSeam",
                motionStart,
                Vector3.one * 0.7f,
                Quaternion.identity,
                movingMaterial);
            movingSphere.AddComponent<Animator>();

            var particleObject = new GameObject("SeamParticleReference");
            particleObject.transform.SetParent(root, false);
            particleObject.transform.position = eye + seamRay * 7f;
            var particles = particleObject.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.duration = TestDurationSeconds;
            main.loop = true;
            main.startLifetime = 2f;
            main.startSpeed = 0.3f;
            main.startSize = 0.12f;
            main.startColor = Color.cyan;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = particles.emission;
            emission.rateOverTime = 12f;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.35f;
        }

        static PlayableDirector BuildTimeline(
            GameObject host,
            GameObject movingSphere,
            Vector3 start,
            Vector3 seam,
            Vector3 end)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AnimationPath) != null)
                AssetDatabase.DeleteAsset(AnimationPath);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(TimelinePath) != null)
                AssetDatabase.DeleteAsset(TimelinePath);

            var animation = new AnimationClip
            {
                name = "SeamCrossingAnimation",
                frameRate = 30f,
                wrapMode = WrapMode.ClampForever
            };

            SetLinearPositionCurve(animation, "m_LocalPosition.x", start.x, seam.x, end.x);
            SetLinearPositionCurve(animation, "m_LocalPosition.y", start.y, seam.y, end.y);
            SetLinearPositionCurve(animation, "m_LocalPosition.z", start.z, seam.z, end.z);
            AssetDatabase.CreateAsset(animation, AnimationPath);

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "LYNOOK_SeamTestTimeline";
            AssetDatabase.CreateAsset(timeline, TimelinePath);
            var track = timeline.CreateTrack<AnimationTrack>(null, "Shared Seam Motion");
            var clip = track.CreateClip(animation);
            clip.displayName = "10s Seam Crossing";
            clip.start = 0.0;
            clip.duration = TestDurationSeconds;

            var director = host.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.timeUpdateMode = DirectorUpdateMode.GameTime;
            director.extrapolationMode = DirectorWrapMode.Hold;
            director.playOnAwake = false;
            director.SetGenericBinding(track, movingSphere.GetComponent<Animator>());
            return director;
        }

        static void SetLinearPositionCurve(AnimationClip animation, string property, float start, float middle, float end)
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, start),
                new Keyframe(5f, middle),
                new Keyframe(TestDurationSeconds, end));
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            AnimationUtility.SetEditorCurve(animation, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), property), curve);
        }

        static GameObject CreatePrimitive(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Material material)
        {
            var primitive = GameObject.CreatePrimitive(type);
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

        static Material CreateOrUpdateMaterial(string name, Color color, bool emission)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Standard");
                if (shader == null)
                    throw new InvalidOperationException("The built-in Standard shader is unavailable.");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            // Gaussian splats are composed near the transparent stage. Keep the test
            // geometry as real depth-tested meshes, but draw it afterwards so the
            // seam references cannot disappear behind the splat composite.
            material.renderQueue = 3100;
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 1f);
            if (emission && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 1.5f);
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        static void CreateHorizontalSeamLine(
            Transform root,
            string name,
            float normalizedMainHeight,
            float depthScale,
            Vector3 eye,
            LYNOOKScreenGeometry mainScreen,
            LYNOOKScreenGeometry sideScreen,
            Material material)
        {
            Vector3 mainPoint = Vector3.Lerp(mainScreen.BottomLeft, mainScreen.TopLeft, normalizedMainHeight);
            float distanceBelowTop = (1f - normalizedMainHeight) * mainScreen.heightMeters;
            Vector3 sidePoint = sideScreen.TopRight - sideScreen.up * distanceBelowTop;
            CreateLine(
                root,
                name,
                ProjectFromEye(eye, mainPoint, depthScale),
                ProjectFromEye(eye, sidePoint, depthScale),
                0.035f,
                material);
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
            var line = CreatePrimitive(
                parent,
                PrimitiveType.Cylinder,
                name,
                (start + end) * 0.5f,
                new Vector3(thickness, delta.magnitude * 0.5f, thickness),
                Quaternion.FromToRotation(Vector3.up, delta.normalized),
                material);
            return line;
        }
    }
}
#endif
