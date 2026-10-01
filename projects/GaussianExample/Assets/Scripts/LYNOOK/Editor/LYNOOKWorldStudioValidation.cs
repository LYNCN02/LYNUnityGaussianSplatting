#if UNITY_EDITOR
using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKWorldStudioValidation
    {
        [MenuItem("Tools/LYNOOK/World Studio Checks/Inspect Imported Room")]
        public static void InspectImportedRoom()
        {
            var world = LYNOOKWorldStudioService.Current;
            if (world == null) throw new InvalidOperationException("Open an imported World Studio scene.");
            world.gaussian.EnsureMaterials();
            world.gaussian.EnsureSorterAndRegister();
            world.cameraRig.ApplyConfiguration();
            Debug.Log("WORLD_STUDIO_RENDER: validAsset=" + world.gaussian.HasValidAsset + ", validSetup=" + world.gaussian.HasValidRenderSetup
                + ", active=" + world.gaussian.isActiveAndEnabled
                + ", splats=" + world.gaussian.asset.splatCount + ", camera=" + world.cameraRig.MainCaptureCamera.transform.position
                + ", room=" + LYNOOKWorldInteraction.CollisionBounds(world));
            world.cameraRig.MainCaptureCamera.Render();
            var previous = RenderTexture.active;
            var texture = new Texture2D(world.cameraRig.MainCaptureTexture.width, world.cameraRig.MainCaptureTexture.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = world.cameraRig.MainCaptureTexture;
                texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(world.workspacePath + "/main_preview.png", texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(texture); }
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/LYNOOK/World Studio Checks/Validate Placement and Export")]
        public static void Validate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Leave Play Mode first.");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            string temporary = Path.Combine(Path.GetTempPath(), "lynook-studio-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var host = new GameObject("StudioValidation");
                var world = host.AddComponent<LYNOOKWorldAuthoring>();
                world.coordinateRoot = host.transform;
                world.collisionObject = new GameObject("Geometry"); world.collisionObject.transform.SetParent(host.transform, false);
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.transform.SetParent(world.collisionObject.transform, false);
                floor.transform.localPosition = new Vector3(0, -0.25f, 0);
                floor.transform.localScale = new Vector3(12, 0.5f, 12);
                var splat = new GameObject("Gaussian"); splat.transform.SetParent(host.transform); splat.SetActive(false);
                world.gaussian = splat.AddComponent<GaussianSplatRenderer>();
                world.avatarSpawn = new GameObject("Spawn").transform; world.avatarSpawn.SetParent(host.transform, false);
                world.activityPoints = new GameObject("Points").transform; world.activityPoints.SetParent(host.transform, false);
                world.walkSize = new Vector2(6, 6); world.spawnPlaced = true;
                world.maximumPoints = 5;
                LYNOOKWorldPersistence.ValidateSettings(world);
                Assert(LYNOOKWorldInteraction.ValidStandingPoint(world, Vector3.zero, out _), "Valid floor rejected");
                Assert(!LYNOOKWorldInteraction.ValidStandingPoint(world, new Vector3(0, 1, 0), out _), "Floating point accepted");
                Assert(!LYNOOKWorldInteraction.ValidStandingPoint(world, new Vector3(3, 0, 0), out _), "Edge point accepted");
                Assert(LYNOOKWorldInteraction.DirectPathClear(world, Vector3.zero, new Vector3(2, 0, 0)), "Empty path rejected");
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.transform.SetParent(world.collisionObject.transform, false);
                wall.transform.localPosition = new Vector3(1, 1, 0);
                wall.transform.localScale = new Vector3(0.3f, 2, 3);
                Physics.SyncTransforms();
                Assert(!LYNOOKWorldInteraction.DirectPathClear(world, Vector3.zero, new Vector3(2, 0, 0)), "Wall crossing accepted");
                Assert(!LYNOOKWorldInteraction.ValidStandingPoint(world, new Vector3(1, 0, 0), out _), "Occupied capsule accepted");
                Object.DestroyImmediate(wall);
                int count = LYNOOKWorldInteraction.GeneratePoints(world);
                Assert(count == 5, "Unexpected generated point count: " + count);
                world.pointSpacing = 0;
                bool rejected = false;
                try { LYNOOKWorldInteraction.GeneratePoints(world); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Zero spacing was not rejected");
                world.pointSpacing = 0.8f;
                int before = world.activityPoints.childCount;
                world.walkCenter = new Vector2(100, 100);
                try { LYNOOKWorldInteraction.GeneratePoints(world); } catch (InvalidOperationException) { }
                Assert(world.activityPoints.childCount == before, "Failure removed existing points");
                world.walkCenter = Vector2.zero;

                host.transform.SetPositionAndRotation(new Vector3(10, 3, -5), Quaternion.Euler(0, 37, 0));
                host.transform.localScale = Vector3.one * 2;
                var settings = host.AddComponent<LYNOOKWorldExportSettings>(); settings.worldCoordinateRoot = host.transform;
                var main = new GameObject("Main").AddComponent<Camera>();
                var side = new GameObject("Side").AddComponent<Camera>();
                main.transform.SetParent(host.transform, false); side.transform.SetParent(host.transform, false);
                main.transform.localPosition = side.transform.localPosition = new Vector3(0, 1.5f, -3);
                Directory.CreateDirectory(temporary);
                // Sentinel files exercise schema publication, not actual video encoding.
                File.WriteAllBytes(temporary + "/main.mp4", new byte[] { 1 });
                File.WriteAllBytes(temporary + "/right.mp4", new byte[] { 1 });
                var exporter = LYNOOKRecordingWorldExporter.Capture(temporary, main, side, "main", "right");
                Assert(exporter.TryWrite(), "Authored export failed");
                var probe = JsonUtility.FromJson<Probe>(File.ReadAllText(temporary + "/world_config.json"));
                Assert(probe.avatarSpawn.position.Length == 3 && Mathf.Abs(probe.avatarSpawn.position[0]) < 0.0001f, "Spawn not exported in room coordinates");
                Assert(probe.activityPoints.Length == 5 && probe.activityPoints[0].type == LynookActivityTypes.Stand, "Template points leaked into authored export");
                Assert(probe.assets.gridMap == "", "Missing navigation asset advertised");
                Debug.Log("LYNOOK_WORLD_STUDIO_CHECKS_PASS: floor, edge, clearance, wall path, generation, invalid settings, failure preservation, transformed-root export, no template points. Video encoding and device alignment are separate checks.");
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }

        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("World Studio validation: " + message); }
        [Serializable] sealed class Probe { public Placement avatarSpawn; public Point[] activityPoints; public Assets assets; }
        [Serializable] sealed class Placement { public float[] position; }
        [Serializable] sealed class Point { public string type; }
        [Serializable] sealed class Assets { public string gridMap; }
    }
}
#endif
