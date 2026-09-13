#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lynook.DualScreen.Editor
{
    /// <summary>Batch regression checks; fixtures are written only under the OS temp folder.</summary>
    public static class LYNOOKWorldExportValidation
    {
        [MenuItem("Tools/LYNOOK/Validation/Validate Recording World Export")]
        public static void RunFromMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Finish Play Mode before running export validation.");
            Run();
        }

        public static void Run()
        {
            Scene original = SceneManager.GetActiveScene();
            var temporaryScenes = new List<Scene>();
            try { RunChecks(temporaryScenes); }
            finally
            {
                if (original.IsValid() && original.isLoaded)
                    SceneManager.SetActiveScene(original);
                foreach (Scene scene in temporaryScenes)
                {
                    if (EditorSceneManager.IsPreviewScene(scene))
                        EditorSceneManager.ClosePreviewScene(scene);
                    else
                        EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        static void RunChecks(List<Scene> temporaryScenes)
        {
            string output = Path.Combine(Path.GetTempPath(), "lynook-world-export-tests", Guid.NewGuid().ToString("N"));
            foreach (string path in new[] { LYNOOKSceneCatalog.RoomPreview, LYNOOKSceneCatalog.SeamCalibration,
                LYNOOKSceneCatalog.SpatialDepth, LYNOOKSceneCatalog.CornerRoom })
            {
                Scene scene = EditorSceneManager.OpenPreviewScene(path);
                temporaryScenes.Add(scene);
                var rig = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<LYNOOKDualCameraRig>()).SingleOrDefault();
                Camera main, side;
                if (rig != null)
                {
                    rig.ApplyConfiguration();
                    main = rig.MainCaptureCamera;
                    side = rig.SideCaptureCamera;
                }
                else
                {
                    Camera[] cameras = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>()).ToArray();
                    main = cameras.Single(camera => camera.CompareTag("MainCamera"));
                    side = cameras.Single(camera => camera.name == "SideCamera");
                    main.aspect = 1280f / 800;
                    side.aspect = 720f / 1280;
                }
                string folder = Path.Combine(output, scene.name);
                Directory.CreateDirectory(folder);
                var snapshot = LYNOOKRecordingWorldExporter.Capture(folder, main, side, "front", "right");
                File.WriteAllText(Path.Combine(folder, "front.mov"), "fixture");
                File.WriteAllText(Path.Combine(folder, "right.mp4"), "fixture");
                Require(snapshot.TryWrite(), "Export " + scene.name);
                ConsumerConfig config = Read(folder);
                Require(config.worldId == scene.name && config.videoMaterials.Length == 2, "Package identity/panel count");
                Require(config.videoMaterials[0].video == "front.mov" && config.videoMaterials[1].video == "right.mp4", "MOV preference / MP4 fallback");
                Require(config.avatarSpawn.applyTransform && Mathf.Abs(config.avatarSpawn.position[0] - 1.52f) < 0.0001f,
                    "Default avatar placement from World E");
                Require(config.playerSpawn != null && config.scenePoints.Length == 1 && config.activityPoints.Length == 4,
                    "Complete spawn and activity defaults");
                Require(config.activityPoints[0].id == "seat_main" && config.activityPoints[0].approachPosition.Length == 3,
                    "Default sitting point and approach");
                Require(config.assets.gridMap == "nav/grid_map.json" && config.assets.preview == "preview.png",
                    "Complete asset paths");
                Require(config.navigation.gridSizeX == 200 && Mathf.Abs(config.navigation.cellSize - 0.1f) < 0.00001f,
                    "Navigation defaults");
                Require(config.transition.scanColor.Length == 4 && config.transition.duplicateCollisionMeshForScan
                    && config.rectBall.enabled && config.rectBall.scale[0] == 3,
                    "Transition and reflection defaults");
                Require(config.continuityTestGeometry != null && !config.continuityTestGeometry.enabled,
                    "Test geometry disabled by default");
                CheckCamera(config.mainCamera, main, null, 0);
                CheckCamera(config.sideCamera, side, null, 1);
            }

            Scene synthetic = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            temporaryScenes.Add(synthetic);
            SceneManager.SetActiveScene(synthetic);
            var root = new GameObject("GLB frame").transform;
            root.SetPositionAndRotation(new Vector3(3, 2, -4), Quaternion.Euler(12, 37, -4));
            root.localScale = Vector3.one * 0.2f;
            var options = root.gameObject.AddComponent<LYNOOKWorldExportSettings>();
            options.worldCoordinateRoot = root;
            options.preview = "images/custom.png";
            options.gridMap = ""; // Existing components with empty paths inherit the complete defaults.
            Camera a = new GameObject("Main").AddComponent<Camera>();
            Camera b = new GameObject("Side").AddComponent<Camera>();
            a.transform.SetPositionAndRotation(root.TransformPoint(new Vector3(1, 2, -3)), root.rotation * Quaternion.Euler(2, 40, 0));
            b.transform.SetPositionAndRotation(a.transform.position, root.rotation * Quaternion.Euler(2, -50, 0));
            a.projectionMatrix = Matrix4x4.Frustum(-0.07f, -0.05f, -0.006f, 0.006f, 0.05f, 100);
            b.projectionMatrix = Matrix4x4.Frustum(0.05f, 0.058f, -0.007f, 0.006f, 0.05f, 100);
            string mapped = Path.Combine(output, "mapped");
            Directory.CreateDirectory(mapped);
            var mappedSnapshot = LYNOOKRecordingWorldExporter.Capture(mapped, a, b, "front", "right");
            File.WriteAllText(Path.Combine(mapped, "front.mp4"), "fixture");
            File.WriteAllText(Path.Combine(mapped, "right.mp4"), "fixture");
            Require(mappedSnapshot.TryWrite(), "Mapped export");
            ConsumerConfig mappedConfig = Read(mapped);
            Require(mappedConfig.assets.preview == "images/custom.png" && mappedConfig.assets.gridMap == "nav/grid_map.json",
                "Custom path override and empty-path default fallback");
            CheckCamera(mappedConfig.mainCamera, a, root, 0);
            CheckCamera(mappedConfig.sideCamera, b, root, 1);

            // Camera changes after capture must not silently change the take's configuration.
            string frozen = Path.Combine(output, "frozen");
            Directory.CreateDirectory(frozen);
            var frozenSnapshot = LYNOOKRecordingWorldExporter.Capture(frozen, a, b, "front", "right");
            a.transform.position += Vector3.one;
            File.WriteAllText(Path.Combine(frozen, "front.mp4"), "fixture");
            File.WriteAllText(Path.Combine(frozen, "right.mp4"), "fixture");
            Require(frozenSnapshot.TryWrite(), "Frozen export");
            Require(Vector3.Distance(Vector(Read(frozen).mainCamera.position), Vector(mappedConfig.mainCamera.position)) < 0.00001f, "Start-of-take snapshot");
            Require(frozenSnapshot.TryWrite(), "Idempotent finalize");

            root.localScale = new Vector3(0.2f, 0.2f, -0.2f);
            bool rejected = false;
            try { LYNOOKRecordingWorldExporter.Capture(mapped, a, b, "front", "right"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Mirrored coordinate root rejected");
            Debug.Log("LYNOOK_WORLD_EXPORT_VALIDATION_PASSED: complete World E defaults, four scene projections, relative paths, MOV/MP4 fallback, coordinate conversion, frozen snapshot, idempotence, mirrored-root rejection. Fixtures: " + output);
        }

        static ConsumerConfig Read(string folder) => JsonUtility.FromJson<ConsumerConfig>(File.ReadAllText(Path.Combine(folder, "world_config.json")));

        static void CheckCamera(ConsumerCamera exported, Camera source, Transform root, int display)
        {
            Require(exported.targetDisplay == display, "Display binding");
            Vector3 position = Vector(exported.position);
            float[] q = exported.rotationQuaternion;
            Quaternion rotation = new Quaternion(q[0], q[1], q[2], q[3]);
            Vector3 recoveredPosition = root != null ? root.TransformPoint(position) : position;
            Quaternion recoveredRotation = root != null ? root.rotation * rotation : rotation;
            Require(Vector3.Distance(recoveredPosition, source.transform.position) < 0.0001f, "World camera position");
            Require(Quaternion.Angle(recoveredRotation, source.transform.rotation) < 0.05f, "World camera rotation");
            var f = exported.offAxisFrustum;
            Require(f.enabled, "Projection enabled");
            Matrix4x4 projection = Matrix4x4.Frustum(f.left, f.right, f.bottom, f.top, f.near, f.far);
            // Compare homogeneous projections of points in front of the capture camera,
            // replaying the exported pose and projection exactly as the runtime does.
            foreach (Vector3 offset in new[] { new Vector3(0, 0, 2), new Vector3(0.3f, -0.2f, 5), new Vector3(-0.1f, 0.7f, 10) })
            {
                Vector3 worldPoint = source.transform.position + source.transform.rotation * offset;
                Vector3 runtimePoint = root != null ? root.InverseTransformPoint(worldPoint) : worldPoint;
                Vector3 local = Quaternion.Inverse(rotation) * (runtimePoint - position);
                Vector3 expected = source.projectionMatrix.MultiplyPoint(new Vector3(offset.x, offset.y, -offset.z));
                Vector3 actual = projection.MultiplyPoint(new Vector3(local.x, local.y, -local.z));
                Require(Vector3.Distance(expected, actual) < 0.001f, "Runtime projection matches recording");
            }
        }

        static Vector3 Vector(float[] a) => new Vector3(a[0], a[1], a[2]);
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("World export validation failed: " + message);
        }

#pragma warning disable CS0649
        [Serializable] sealed class ConsumerConfig
        {
            public string worldId;
            public ConsumerPlacement avatarSpawn;
            public ConsumerPlacement playerSpawn;
            public ConsumerScenePoint[] scenePoints;
            public ConsumerPoint[] activityPoints;
            public ConsumerAssets assets;
            public ConsumerNavigation navigation;
            public ConsumerTransition transition;
            public ConsumerRectBall rectBall;
            public ConsumerContinuity continuityTestGeometry;
            public ConsumerCamera mainCamera, sideCamera;
            public ConsumerVideo[] videoMaterials;
        }
        [Serializable] class ConsumerPlacement
        {
            public bool applyTransform;
            public float[] position;
            public float[] rotationQuaternion;
        }
        [Serializable] sealed class ConsumerCamera : ConsumerPlacement
        {
            public int targetDisplay;
            public ConsumerFrustum offAxisFrustum;
        }
        [Serializable] sealed class ConsumerFrustum
        {
            public bool enabled;
            public float left, right, bottom, top, near, far;
        }
        [Serializable] sealed class ConsumerVideo { public string video; }
        [Serializable] sealed class ConsumerPoint
        {
            public string id;
            public float[] approachPosition;
        }
        [Serializable] sealed class ConsumerScenePoint { public int id; }
        [Serializable] sealed class ConsumerAssets { public string gridMap, preview; }
        [Serializable] sealed class ConsumerNavigation { public int gridSizeX; public float cellSize; }
        [Serializable] sealed class ConsumerTransition { public float[] scanColor; public bool duplicateCollisionMeshForScan; }
        [Serializable] sealed class ConsumerRectBall { public bool enabled; public float[] scale; }
        [Serializable] sealed class ConsumerContinuity { public bool enabled; }
#pragma warning restore CS0649
    }
}
#endif
