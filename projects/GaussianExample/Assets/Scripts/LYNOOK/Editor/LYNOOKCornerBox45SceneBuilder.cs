#if UNITY_EDITOR
using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Lynook.DualScreen.Editor
{
    /// <summary>
    /// Builds the fixed-sweet-spot convex corner-display proof: two physical panel
    /// feeds receding from the nearest front-right corner, one continuous virtual
    /// volume, and four observer cameras that record the physical model from the
    /// sweet spot, above, left, and right without changing the panel projections.
    /// </summary>
    public static class LYNOOKCornerBox45SceneBuilder
    {
        const string RecorderRoot = "Assets/LYNOOK/DualScreenRecorder";
        const string CornerRoot = "Assets/LYNOOK/CornerBox45";
        const string PrefabPath = RecorderRoot + "/Prefabs/LYNOOK_DualCameraRecorder.prefab";
        const string ScenePath = RecorderRoot + "/Scenes/LYNOOK_DualScreen_CornerBox45Test.unity";
        const string ObserverRtPath = CornerRoot + "/RenderTextures/Corner45ObserverRT.renderTexture";
        const string ObserverTopRtPath = CornerRoot + "/RenderTextures/Corner45ObserverTopRT.renderTexture";
        const string ObserverLeftRtPath = CornerRoot + "/RenderTextures/Corner45ObserverLeftRT.renderTexture";
        const string ObserverRightRtPath = CornerRoot + "/RenderTextures/Corner45ObserverRightRT.renderTexture";
        const string MainPanelMeshPath = CornerRoot + "/Meshes/Corner45MainPanel.mesh";
        const string SidePanelMeshPath = CornerRoot + "/Meshes/Corner45SidePanel.mesh";
        const string GaussianAssetPath = "Assets/GaussianAssets/Modern Bedroom City View.asset";
        const string GaussianPackageRoot = "Packages/org.nesnausk.gaussian-splatting";
        const int ObserverLayer = 2;
        const int ObserverWidth = 1920;
        const int ObserverHeight = 1080;
        const float EyeToCornerDistanceMm = 600f;
        const float ViewingAzimuthDegrees = 45f;
        const float PanelAngleDegrees = 90f;
        const float TestDurationSeconds = 10f;
        const float GaussianDollyBackSourceMeters = 9.00f;

        [MenuItem("Tools/LYNOOK/Create or Open Corner Box 45 Test Scene")]
        public static void CreateOrOpenScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                CreateScene();
            else
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Selection.activeGameObject = GameObject.Find("LYNOOK_CornerBox45_Test");
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log($"LYNOOK CornerBox45 scene is ready: {ScenePath}");
        }

        [MenuItem("Tools/LYNOOK/Open and Record Corner Box 45 Multi-Angle")]
        public static void OpenAndRecord()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before starting the CornerBox45 recording.");

            CreateOrOpenScene();
            EditorApplication.EnterPlaymode();
        }

        [MenuItem("Tools/LYNOOK/Rebuild Corner Box 45 with Gaussian Scene")]
        public static void RebuildWithGaussianScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before rebuilding the CornerBox45 scene.");

            CreateScene();
            Selection.activeGameObject = GameObject.Find("GaussianScene_ModernBedroom");
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        public static void CreateSceneFromCommandLine()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                CreateScene();
            else
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        public static void RebuildGaussianSceneFromCommandLine()
        {
            CreateScene();
        }

        static void CreateScene()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
                throw new FileNotFoundException("Build the LYNOOK Off-Axis recorder prefab first.", PrefabPath);
            if (AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(GaussianAssetPath) == null)
                throw new FileNotFoundException("The previous Modern Bedroom Gaussian asset was not found.", GaussianAssetPath);

            EnsureFolders();
            RenderTexture observerTexture = CreateOrUpdateObserverTexture(
                ObserverRtPath,
                "Corner45ObserverRT");
            RenderTexture observerTopTexture = CreateOrUpdateObserverTexture(
                ObserverTopRtPath,
                "Corner45ObserverTopRT");
            RenderTexture observerLeftTexture = CreateOrUpdateObserverTexture(
                ObserverLeftRtPath,
                "Corner45ObserverLeftRT");
            RenderTexture observerRightTexture = CreateOrUpdateObserverTexture(
                ObserverRightRtPath,
                "Corner45ObserverRightRT");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sceneRoot = new GameObject("LYNOOK_CornerBox45_Test");
            SceneManager.MoveGameObjectToScene(sceneRoot, scene);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var recorderRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            recorderRoot.name = "LYNOOK_CornerBox45_CaptureRig";
            recorderRoot.transform.SetParent(sceneRoot.transform, false);
            var rig = recorderRoot.GetComponent<LYNOOKDualCameraRig>();
            if (rig == null)
                throw new MissingComponentException("The LYNOOK recorder prefab has no LYNOOKDualCameraRig.");

            rig.CaptureRig.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            rig.ConfigureCornerBoxProfile(EyeToCornerDistanceMm, ViewingAzimuthDegrees, PanelAngleDegrees);
            rig.SetGameViewPreviewEnabled(false);
            ConfigureCaptureCameras(rig);
            rig.GetScreenGeometries(out LYNOOKScreenGeometry mainScreen, out LYNOOKScreenGeometry sideScreen);

            BuildGaussianScene(
                sceneRoot.transform,
                rig,
                mainScreen,
                sideScreen);

            var virtualContentRoot = new GameObject("CornerBox_CalibrationOverlay").transform;
            virtualContentRoot.SetParent(sceneRoot.transform, false);
            BuildVirtualCornerBox(
                rig,
                virtualContentRoot,
                mainScreen,
                sideScreen,
                out Transform hero,
                out Transform orbitRoot,
                out Vector3 insidePosition,
                out Vector3 cornerPosition,
                out Vector3 popOutPosition);

            var motion = virtualContentRoot.gameObject.AddComponent<LYNOOKCornerBoxMotion>();
            motion.SetReferences(
                hero,
                orbitRoot,
                insidePosition,
                cornerPosition,
                popOutPosition,
                TestDurationSeconds);
            virtualContentRoot.name = "CornerBox_CalibrationOverlay_DISABLED";
            virtualContentRoot.gameObject.SetActive(false);

            BuildPhysicalObserverMock(
                sceneRoot.transform,
                rig,
                mainScreen,
                sideScreen,
                observerTexture,
                observerTopTexture,
                observerLeftTexture,
                observerRightTexture,
                out Camera observerCamera,
                out Camera observerTopCamera,
                out Camera observerLeftCamera,
                out Camera observerRightCamera);

            var session = recorderRoot.AddComponent<LYNOOKCornerBoxRecordingSession>();
            session.SetReferences(
                rig,
                observerTexture,
                observerTopTexture,
                observerLeftTexture,
                observerRightTexture);

            rig.ApplyConfiguration();
            ConfigureCaptureCameras(rig);
            if (!rig.TryValidate(out string validationReport))
                throw new InvalidOperationException(validationReport);
            if (observerCamera.targetTexture != observerTexture)
                throw new InvalidOperationException("CornerBox45 observer camera is not targeting its recording texture.");
            if (observerTopCamera.targetTexture != observerTopTexture
                || observerLeftCamera.targetTexture != observerLeftTexture
                || observerRightCamera.targetTexture != observerRightTexture)
            {
                throw new InvalidOperationException("One or more CornerBox45 alternate observer cameras have the wrong target texture.");
            }
            ValidateConvexObserverPerspective(observerCamera, mainScreen, sideScreen);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath, false))
                throw new IOException($"Unity failed to save {ScenePath}.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                $"LYNOOK CornerBox45 created. Fixed eye-to-corner={EyeToCornerDistanceMm:F0} mm, "
                + $"convex exterior view azimuth={ViewingAzimuthDegrees:F1} deg, panel angle={PanelAngleDegrees:F1} deg. "
                + "Gaussian scene=Modern Bedroom City View. "
                + "Calibration remains provisional until measured on hardware.");
        }

        static void ValidateConvexObserverPerspective(
            Camera observer,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side)
        {
            float seamMainHeight = ProjectedViewportHeight(observer, main.BottomRight, main.TopRight);
            float outerMainHeight = ProjectedViewportHeight(observer, main.BottomLeft, main.TopLeft);
            float seamSideHeight = ProjectedViewportHeight(observer, side.BottomLeft, side.TopLeft);
            float outerSideHeight = ProjectedViewportHeight(observer, side.BottomRight, side.TopRight);
            if (seamMainHeight <= outerMainHeight || seamSideHeight <= outerSideHeight)
            {
                throw new InvalidOperationException(
                    "CornerBox45 convex perspective failed: the nearest seam must project larger "
                    + $"than both receding outer edges. Main seam/outer={seamMainHeight:F4}/{outerMainHeight:F4}, "
                    + $"side seam/outer={seamSideHeight:F4}/{outerSideHeight:F4}.");
            }

            Debug.Log(
                "LYNOOK CornerBox45 convex perspective validated (near seam larger, outer edges smaller). "
                + $"Main seam/outer={seamMainHeight / outerMainHeight:F3}x; "
                + $"Side seam/outer={seamSideHeight / outerSideHeight:F3}x.");
        }

        static float ProjectedViewportHeight(Camera camera, Vector3 bottom, Vector3 top)
        {
            return Mathf.Abs(
                camera.WorldToViewportPoint(top).y
                - camera.WorldToViewportPoint(bottom).y);
        }

        static void BuildGaussianScene(
            Transform sceneRoot,
            LYNOOKDualCameraRig rig,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side)
        {
            GaussianSplatAsset asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(GaussianAssetPath);
            var gaussianObject = new GameObject("GaussianScene_ModernBedroom");
            gaussianObject.transform.SetParent(sceneRoot, false);

            // Preserve the useful viewpoint from GSTestScene instead of fitting the whole
            // scan by its bounds. Both the old source camera and the Gaussian scene receive
            // the same similarity transform, so the old view lands exactly on the new
            // shared eye and faces the 45-degree corner bisector.
            const float uniformScale = 0.20f;
            Vector3 sourceCameraPosition = new Vector3(0.07f, 1.46f, -2.55f);
            Quaternion sourceCameraRotation = Quaternion.Euler(3.646f, -7.864f, -0.503f);
            Vector3 pulledBackSourceCameraPosition = sourceCameraPosition
                - sourceCameraRotation * Vector3.forward * GaussianDollyBackSourceMeters;
            Vector3 seamCenter = Vector3.Lerp(main.BottomRight, main.TopRight, 0.5f);
            Vector3 eye = rig.SharedEyeWorldPosition;
            Quaternion desiredCameraRotation = Quaternion.LookRotation(
                (seamCenter - eye).normalized,
                main.up);
            Quaternion sceneAlignment = desiredCameraRotation * Quaternion.Inverse(sourceCameraRotation);
            Vector3 signedScale = new Vector3(uniformScale, uniformScale, -uniformScale);
            gaussianObject.transform.rotation = sceneAlignment;
            gaussianObject.transform.localScale = signedScale;
            gaussianObject.transform.position = eye
                - sceneAlignment * (pulledBackSourceCameraPosition * uniformScale);

            var renderer = gaussianObject.AddComponent<GaussianSplatRenderer>();
            renderer.m_Asset = asset;
            renderer.m_RenderOrder = -10;
            renderer.m_SplatScale = 1f;
            renderer.m_OpacityScale = 1f;
            renderer.m_SHOrder = 3;
            renderer.m_SortNthFrame = 1;
            renderer.m_RenderMode = GaussianSplatRenderer.RenderMode.Splats;
            renderer.m_Cutouts = Array.Empty<GaussianCutout>();
            renderer.m_ShaderSplats = LoadRequiredAsset<Shader>(
                GaussianPackageRoot + "/Shaders/RenderGaussianSplats.shader");
            renderer.m_ShaderComposite = LoadRequiredAsset<Shader>(
                GaussianPackageRoot + "/Shaders/GaussianComposite.shader");
            renderer.m_ShaderDebugPoints = LoadRequiredAsset<Shader>(
                GaussianPackageRoot + "/Shaders/GaussianDebugRenderPoints.shader");
            renderer.m_ShaderDebugBoxes = LoadRequiredAsset<Shader>(
                GaussianPackageRoot + "/Shaders/GaussianDebugRenderBoxes.shader");
            renderer.m_CSSplatUtilities = LoadRequiredAsset<ComputeShader>(
                GaussianPackageRoot + "/Shaders/SplatUtilities.compute");

            Debug.Log(
                $"LYNOOK CornerBox45 Gaussian fitted: asset='{asset.name}', splats={asset.splatCount:N0}, "
                + $"scale={uniformScale:F3}, source camera dolly back={GaussianDollyBackSourceMeters:F2} m, "
                + $"pulled-back source view mapped to shared eye={eye:F4}.");
        }

        static void ConfigureCaptureCameras(LYNOOKDualCameraRig rig)
        {
            int contentMask = ~(1 << ObserverLayer);
            Camera main = rig.MainCaptureCamera;
            Camera side = rig.SideCaptureCamera;
            main.clearFlags = CameraClearFlags.SolidColor;
            side.clearFlags = CameraClearFlags.SolidColor;
            main.backgroundColor = Color.black;
            side.backgroundColor = Color.black;
            main.cullingMask = contentMask;
            side.cullingMask = contentMask;
            main.depth = 0f;
            side.depth = 0f;
            main.allowHDR = true;
            side.allowHDR = true;
        }

        static void BuildVirtualCornerBox(
            LYNOOKDualCameraRig rig,
            Transform root,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            out Transform hero,
            out Transform orbitRoot,
            out Vector3 insidePosition,
            out Vector3 cornerPosition,
            out Vector3 popOutPosition)
        {
            Material cageCyan = CreateOrUpdateStandardMaterial(
                "CageCyan", new Color(0.02f, 0.75f, 1f), 0.1f, 0.75f, 1.7f);
            Material cageYellow = CreateOrUpdateStandardMaterial(
                "CageYellow", new Color(1f, 0.76f, 0.02f), 0.15f, 0.7f, 1.5f);
            Material heroMaterial = CreateOrUpdateStandardMaterial(
                "HeroCyan", new Color(0.02f, 0.95f, 0.85f), 0.55f, 0.9f, 0.65f);
            Material accentMaterial = CreateOrUpdateStandardMaterial(
                "HeroMagenta", new Color(1f, 0.08f, 0.65f), 0.35f, 0.8f, 0.8f);
            Material wallMaterial = CreateOrUpdateStandardMaterial(
                "VirtualWall", new Color(0.015f, 0.025f, 0.06f), 0.05f, 0.2f, 0.05f);

            Vector3 eye = rig.SharedEyeWorldPosition;
            var optionalShell = new GameObject("Optional_OpaqueCalibrationShell_DISABLED").transform;
            optionalShell.SetParent(root, false);
            BuildRoomSurfaces(optionalShell, main, side, wallMaterial);
            optionalShell.gameObject.SetActive(false);
            BuildAngularCage(root, eye, main, side, cageCyan, cageYellow);
            BuildFloorGrid(root, eye, main, side, cageCyan, cageYellow);

            var lightRoot = new GameObject("CornerBox_Lighting").transform;
            lightRoot.SetParent(root, false);
            var directional = new GameObject("KeyLight").AddComponent<Light>();
            directional.transform.SetParent(lightRoot, false);
            directional.type = LightType.Directional;
            directional.color = new Color(0.72f, 0.86f, 1f);
            directional.intensity = 1.25f;
            directional.transform.rotation = Quaternion.Euler(34f, -38f, 0f);
            var point = new GameObject("CornerGlow").AddComponent<Light>();
            point.transform.SetParent(lightRoot, false);
            point.type = LightType.Point;
            point.color = new Color(0.1f, 0.6f, 1f);
            point.intensity = 2.4f;
            point.range = 1.4f;

            Vector3 seamCenter = Vector3.Lerp(main.BottomRight, main.TopRight, 0.5f);
            point.transform.position = eye + (seamCenter - eye) * 1.75f + main.up * 0.08f;
            insidePosition = eye + (seamCenter - eye) * 2.45f + main.up * 0.018f;
            cornerPosition = eye + (seamCenter - eye) * 1.15f + main.up * 0.03f;
            popOutPosition = eye + (seamCenter - eye) * 0.90f + main.up * 0.02f;

            hero = new GameObject("Hero_PopOutAcrossCorner").transform;
            hero.SetParent(root, false);
            hero.position = insidePosition;
            CreatePrimitiveLocal(
                hero,
                PrimitiveType.Cube,
                "HeroCore",
                Vector3.zero,
                new Vector3(0.055f, 0.055f, 0.055f),
                Quaternion.Euler(18f, 35f, 12f),
                heroMaterial,
                0);

            orbitRoot = new GameObject("HeroOrbitRoot").transform;
            orbitRoot.SetParent(hero, false);
            CreatePrimitiveLocal(
                orbitRoot,
                PrimitiveType.Sphere,
                "OrbitSatellite_A",
                new Vector3(0.092f, 0.025f, 0f),
                Vector3.one * 0.026f,
                Quaternion.identity,
                accentMaterial,
                0);
            CreatePrimitiveLocal(
                orbitRoot,
                PrimitiveType.Sphere,
                "OrbitSatellite_B",
                new Vector3(-0.07f, -0.035f, 0.055f),
                Vector3.one * 0.018f,
                Quaternion.identity,
                cageYellow,
                0);
        }

        static void BuildRoomSurfaces(
            Transform root,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            Material wallMaterial)
        {
            float floorY = main.BottomLeft.y - 0.012f;
            CreatePrimitiveWorld(
                root,
                PrimitiveType.Cube,
                "VirtualBox_Floor",
                new Vector3(0.16f, floorY, 0.66f),
                new Vector3(0.9f, 0.012f, 0.72f),
                Quaternion.identity,
                wallMaterial,
                0);
            CreatePrimitiveWorld(
                root,
                PrimitiveType.Cube,
                "VirtualBox_BackWall",
                new Vector3(0.12f, 0.055f, 0.99f),
                new Vector3(0.78f, 0.34f, 0.012f),
                Quaternion.identity,
                wallMaterial,
                0);
            CreatePrimitiveWorld(
                root,
                PrimitiveType.Cube,
                "VirtualBox_SideWall",
                new Vector3(0.56f, 0.055f, 0.68f),
                new Vector3(0.012f, 0.34f, 0.64f),
                Quaternion.identity,
                wallMaterial,
                0);
        }

        static void BuildAngularCage(
            Transform root,
            Vector3 eye,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            Material primary,
            Material accent)
        {
            var cageRoot = new GameObject("CornerBox_PerspectiveCage").transform;
            cageRoot.SetParent(root, false);
            float[] scales = { 1.35f, 1.85f, 2.55f };
            for (int i = 0; i < scales.Length; i++)
            {
                float scale = scales[i];
                var frame = new GameObject($"DepthFrame_{i + 1:00}").transform;
                frame.SetParent(cageRoot, false);
                Vector3 mainTopLeft = ProjectFromEye(eye, main.TopLeft, scale);
                Vector3 mainBottomLeft = ProjectFromEye(eye, main.BottomLeft, scale);
                Vector3 sideTopRight = ProjectFromEye(eye, side.TopRight, scale);
                Vector3 sideBottomRight = ProjectFromEye(eye, side.BottomRight, scale);
                Vector3 seamTop = ProjectFromEye(eye, main.TopRight, scale);
                Vector3 seamBottom = ProjectFromEye(eye, main.BottomRight, scale);
                Material material = i == 1 ? accent : primary;
                CreateLine(frame, "Top", mainTopLeft, sideTopRight, 0.0032f, material, 0);
                CreateLine(frame, "Bottom", mainBottomLeft, sideBottomRight, 0.0032f, material, 0);
                CreateLine(frame, "OuterMain", mainBottomLeft, mainTopLeft, 0.0032f, material, 0);
                CreateLine(frame, "OuterSide", sideBottomRight, sideTopRight, 0.0032f, material, 0);
                CreateLine(frame, "CornerVertical", seamBottom, seamTop, 0.0024f, material, 0);
            }

            CreateLine(
                cageRoot,
                "CrossCornerDiagonal_A",
                ProjectFromEye(eye, main.BottomLeft, 1.7f),
                ProjectFromEye(eye, side.TopRight, 1.7f),
                0.0024f,
                primary,
                0);
            CreateLine(
                cageRoot,
                "CrossCornerDiagonal_B",
                ProjectFromEye(eye, main.TopLeft, 2.15f),
                ProjectFromEye(eye, side.BottomRight, 2.15f),
                0.0024f,
                primary,
                0);
        }

        static void BuildFloorGrid(
            Transform root,
            Vector3 eye,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            Material primary,
            Material accent)
        {
            var gridRoot = new GameObject("CornerBox_FloorGrid").transform;
            gridRoot.SetParent(root, false);
            Vector3 mainBottom = main.BottomLeft + main.up * 0.008f;
            Vector3 seamBottom = main.BottomRight + main.up * 0.008f;
            Vector3 sideBottom = side.BottomRight + side.up * 0.008f;
            float[] scales = { 1.08f, 1.3f, 1.55f, 1.85f, 2.2f, 2.6f, 3.0f };
            foreach (float scale in scales)
            {
                CreateLine(
                    gridRoot,
                    $"DepthLine_{scale:F2}",
                    ProjectFromEye(eye, mainBottom, scale),
                    ProjectFromEye(eye, sideBottom, scale),
                    0.0015f,
                    primary,
                    0);
            }

            Vector3[] anchors =
            {
                main.BottomLeft + main.up * 0.008f,
                Vector3.Lerp(main.BottomLeft, main.BottomRight, 0.5f) + main.up * 0.008f,
                seamBottom,
                Vector3.Lerp(side.BottomLeft, side.BottomRight, 0.5f) + side.up * 0.008f,
                sideBottom
            };
            for (int i = 0; i < anchors.Length; i++)
            {
                CreateLine(
                    gridRoot,
                    $"ConvergingRail_{i:00}",
                    ProjectFromEye(eye, anchors[i], 1.04f),
                    ProjectFromEye(eye, anchors[i], 3.05f),
                    i == 2 ? 0.0025f : 0.0014f,
                    i == 2 ? accent : primary,
                    0);
            }
        }

        static void BuildPhysicalObserverMock(
            Transform sceneRoot,
            LYNOOKDualCameraRig rig,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            RenderTexture observerTexture,
            RenderTexture observerTopTexture,
            RenderTexture observerLeftTexture,
            RenderTexture observerRightTexture,
            out Camera observerCamera,
            out Camera observerTopCamera,
            out Camera observerLeftCamera,
            out Camera observerRightCamera)
        {
            var mockRoot = new GameObject("PhysicalCornerDisplay_ObserverOnly").transform;
            mockRoot.SetParent(sceneRoot, false);
            mockRoot.gameObject.layer = ObserverLayer;

            Material mainDisplay = CreateOrUpdateDisplayMaterial("MainPanelDisplay", rig.MainCaptureTexture);
            Material sideDisplay = CreateOrUpdateDisplayMaterial("SidePanelDisplay", rig.SideCaptureTexture);
            Material frameMaterial = CreateOrUpdateUnlitColorMaterial("PhysicalFrame", new Color(0.008f, 0.008f, 0.012f));

            CreatePanelSurface(
                mockRoot,
                "PhysicalMainPanel_172.22x107.64mm",
                main,
                MainPanelMeshPath,
                mainDisplay);
            CreatePanelSurface(
                mockRoot,
                "PhysicalSidePanel_62.1x110.4mm",
                side,
                SidePanelMeshPath,
                sideDisplay);
            CreatePanelFrame(mockRoot, "MainFrame", main, rig.SharedEyeWorldPosition, frameMaterial);
            CreatePanelFrame(mockRoot, "SideFrame", side, rig.SharedEyeWorldPosition, frameMaterial);

            Vector3 eye = rig.SharedEyeWorldPosition;
            Vector3 target = (main.center * 0.66f + side.center * 0.34f);
            Vector3 horizontalView = Vector3.ProjectOnPlane(target - eye, main.up).normalized;
            Vector3 observerRight = Vector3.Cross(main.up, horizontalView).normalized;
            Vector3 leftObserverPosition = eye - observerRight * 0.18f;
            Vector3 rightObserverPosition = eye + observerRight * 0.18f;
            Vector3 topObserverPosition = eye + main.up * 0.36f;

            observerCamera = CreateObserverCamera(
                sceneRoot,
                "Corner45_ObserverCamera_SweetSpot",
                observerTexture,
                eye,
                target,
                main,
                side,
                1.08f);
            observerTopCamera = CreateObserverCamera(
                sceneRoot,
                "Corner45_ObserverCamera_Top",
                observerTopTexture,
                topObserverPosition,
                target,
                main,
                side,
                1.16f);
            observerLeftCamera = CreateObserverCamera(
                sceneRoot,
                "Corner45_ObserverCamera_Left",
                observerLeftTexture,
                leftObserverPosition,
                target,
                main,
                side,
                1.12f);
            observerRightCamera = CreateObserverCamera(
                sceneRoot,
                "Corner45_ObserverCamera_Right",
                observerRightTexture,
                rightObserverPosition,
                target,
                main,
                side,
                1.12f);

            var previewObject = new GameObject("Corner45_GameViewPreview");
            previewObject.transform.SetParent(sceneRoot, false);
            var previewCamera = previewObject.AddComponent<Camera>();
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = Color.black;
            previewCamera.cullingMask = 0;
            previewCamera.depth = 1000f;
            previewCamera.orthographic = true;
            previewCamera.allowHDR = false;
            previewCamera.allowMSAA = false;
            previewCamera.targetDisplay = 0;
            var blitter = previewObject.AddComponent<LYNOOKRenderTexturePreviewBlitter>();
            blitter.Source = observerTexture;
        }

        static Camera CreateObserverCamera(
            Transform sceneRoot,
            string name,
            RenderTexture targetTexture,
            Vector3 position,
            Vector3 target,
            LYNOOKScreenGeometry main,
            LYNOOKScreenGeometry side,
            float framingMargin)
        {
            var observerObject = new GameObject(name);
            observerObject.transform.SetParent(sceneRoot, false);
            Camera observerCamera = observerObject.AddComponent<Camera>();
            observerCamera.clearFlags = CameraClearFlags.SolidColor;
            observerCamera.backgroundColor = new Color(0.012f, 0.014f, 0.022f);
            observerCamera.cullingMask = 1 << ObserverLayer;
            observerCamera.nearClipPlane = 0.01f;
            observerCamera.farClipPlane = 5f;
            observerCamera.allowHDR = false;
            observerCamera.allowMSAA = true;
            observerCamera.depth = 100f;
            observerCamera.targetTexture = targetTexture;
            observerCamera.aspect = (float)ObserverWidth / ObserverHeight;
            observerObject.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation((target - position).normalized, main.up));
            observerCamera.fieldOfView = CalculateFittingVerticalFov(
                observerCamera,
                new[]
                {
                    main.BottomLeft, main.BottomRight, main.TopRight, main.TopLeft,
                    side.BottomLeft, side.BottomRight, side.TopRight, side.TopLeft
                },
                framingMargin);
            return observerCamera;
        }

        static float CalculateFittingVerticalFov(Camera camera, Vector3[] corners, float margin)
        {
            float requiredTan = 0.01f;
            foreach (Vector3 corner in corners)
            {
                Vector3 local = camera.transform.InverseTransformPoint(corner);
                if (local.z <= 0.001f)
                    continue;
                requiredTan = Mathf.Max(requiredTan, Mathf.Abs(local.y / local.z));
                requiredTan = Mathf.Max(requiredTan, Mathf.Abs(local.x / local.z) / camera.aspect);
            }
            return Mathf.Clamp(2f * Mathf.Atan(requiredTan * margin) * Mathf.Rad2Deg, 10f, 100f);
        }

        static void CreatePanelSurface(
            Transform parent,
            string name,
            LYNOOKScreenGeometry screen,
            string meshPath,
            Material material)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = Path.GetFileNameWithoutExtension(meshPath) };
                AssetDatabase.CreateAsset(mesh, meshPath);
            }
            mesh.Clear();
            mesh.vertices = new[] { screen.BottomLeft, screen.BottomRight, screen.TopRight, screen.TopLeft };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            Vector3 viewerNormal = -screen.forward;
            mesh.normals = new[] { viewerNormal, viewerNormal, viewerNormal, viewerNormal };
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            var panel = new GameObject(name) { layer = ObserverLayer };
            panel.transform.SetParent(parent, false);
            var filter = panel.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = panel.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static void CreatePanelFrame(
            Transform parent,
            string name,
            LYNOOKScreenGeometry screen,
            Vector3 eye,
            Material material)
        {
            var frame = new GameObject(name) { layer = ObserverLayer };
            frame.transform.SetParent(parent, false);
            Vector3 towardEye = (eye - screen.center).normalized * 0.00035f;
            Vector3 bl = screen.BottomLeft + towardEye;
            Vector3 br = screen.BottomRight + towardEye;
            Vector3 tr = screen.TopRight + towardEye;
            Vector3 tl = screen.TopLeft + towardEye;
            CreateLine(frame.transform, "Bottom", bl, br, 0.0011f, material, ObserverLayer);
            CreateLine(frame.transform, "Right", br, tr, 0.0011f, material, ObserverLayer);
            CreateLine(frame.transform, "Top", tr, tl, 0.0011f, material, ObserverLayer);
            CreateLine(frame.transform, "Left", tl, bl, 0.0011f, material, ObserverLayer);
        }

        static RenderTexture CreateOrUpdateObserverTexture(string path, string textureName)
        {
            var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (texture == null)
            {
                texture = new RenderTexture(
                    ObserverWidth,
                    ObserverHeight,
                    24,
                    RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default)
                {
                    name = textureName
                };
                AssetDatabase.CreateAsset(texture, path);
            }
            texture.Release();
            texture.width = ObserverWidth;
            texture.height = ObserverHeight;
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

        static Material CreateOrUpdateDisplayMaterial(string name, RenderTexture texture)
        {
            string path = $"{CornerRoot}/Materials/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Unlit/Texture");
                if (shader == null)
                    throw new InvalidOperationException("Built-in Unlit/Texture shader is unavailable.");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.mainTexture = texture;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material CreateOrUpdateUnlitColorMaterial(string name, Color color)
        {
            string path = $"{CornerRoot}/Materials/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null)
                    throw new InvalidOperationException("Built-in Unlit/Color shader is unavailable.");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material CreateOrUpdateStandardMaterial(
            string name,
            Color color,
            float metallic,
            float smoothness,
            float emissionStrength)
        {
            string path = $"{CornerRoot}/Materials/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null)
                    throw new InvalidOperationException("Built-in Standard shader is unavailable.");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", smoothness);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * emissionStrength);
            EditorUtility.SetDirty(material);
            return material;
        }

        static T LoadRequiredAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new FileNotFoundException($"Required asset was not found: {path}", path);
            return asset;
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
            Material material,
            int layer)
        {
            Vector3 delta = end - start;
            if (delta.sqrMagnitude < 0.0000001f)
                throw new ArgumentException($"Cannot create zero-length line: {name}");
            return CreatePrimitiveWorld(
                parent,
                PrimitiveType.Cylinder,
                name,
                (start + end) * 0.5f,
                new Vector3(thickness, delta.magnitude * 0.5f, thickness),
                Quaternion.FromToRotation(Vector3.up, delta.normalized),
                material,
                layer);
        }

        static GameObject CreatePrimitiveWorld(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 position,
            Vector3 scale,
            Quaternion rotation,
            Material material,
            int layer)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.layer = layer;
            primitive.transform.SetParent(parent, false);
            primitive.transform.SetPositionAndRotation(position, rotation);
            primitive.transform.localScale = scale;
            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            var collider = primitive.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
            return primitive;
        }

        static GameObject CreatePrimitiveLocal(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Material material,
            int layer)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.layer = layer;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localRotation = localRotation;
            primitive.transform.localScale = localScale;
            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            var collider = primitive.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
            return primitive;
        }

        static void EnsureFolders()
        {
            EnsureFolder("Assets/LYNOOK");
            EnsureFolder(CornerRoot);
            EnsureFolder(CornerRoot + "/Materials");
            EnsureFolder(CornerRoot + "/Meshes");
            EnsureFolder(CornerRoot + "/RenderTextures");
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
    }
}
#endif
