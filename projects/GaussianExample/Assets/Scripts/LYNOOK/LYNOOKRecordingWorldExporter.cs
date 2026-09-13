#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lynook.DualScreen
{
    /// <summary>
    /// Snapshots the actual recording cameras, then publishes a runtime WorldConfig
    /// only after both panel files exist. Kept outside Editor/ for recorder references.
    /// </summary>
    public sealed class LYNOOKRecordingWorldExporter
    {
        public const string DefaultsPath = "Assets/LYNOOK/WorldExport/world_config.defaults.json";
        readonly string folder;
        readonly string mainBaseName;
        readonly string sideBaseName;
        readonly Config config;
        bool written;

        LYNOOKRecordingWorldExporter(string folder, string mainBaseName, string sideBaseName, Config config)
        {
            this.folder = folder;
            this.mainBaseName = mainBaseName;
            this.sideBaseName = sideBaseName;
            this.config = config;
        }

        public static LYNOOKRecordingWorldExporter Capture(
            string folder, Camera main, Camera side, string mainBaseName, string sideBaseName)
        {
            if (main == null || side == null || main.gameObject.scene != side.gameObject.scene)
                throw new InvalidOperationException("World export requires two cameras in the recording scene.");

            var settings = main.gameObject.scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<LYNOOKWorldExportSettings>(true))
                .Where(value => value.isActiveAndEnabled).ToArray();
            if (settings.Length > 1)
                throw new InvalidOperationException("Use at most one active LYNOOKWorldExportSettings in the recording scene.");
            var options = settings.SingleOrDefault();
            Transform coordinateRoot = options != null ? options.worldCoordinateRoot : null;
            float unitScale = ValidateCoordinateRoot(coordinateRoot);
            var defaults = AssetDatabase.LoadAssetAtPath<TextAsset>(DefaultsPath);
            if (defaults == null)
                throw new FileNotFoundException("World export defaults were not found.", DefaultsPath);
            Config payload = JsonUtility.FromJson<Config>(defaults.text);
            if (payload == null || payload.assets == null || payload.playerSpawn == null
                || payload.avatarSpawn == null || payload.scenePoints == null || payload.activityPoints == null
                || payload.navigation == null || payload.transition == null || payload.rectBall == null
                || payload.continuityTestGeometry == null)
                throw new InvalidOperationException("World export defaults must include the complete runtime world configuration.");
            // The take folder is the package root; its name is the runtime worldId.
            payload.worldId = new DirectoryInfo(folder).Name;
            payload.displayName = options != null && !string.IsNullOrWhiteSpace(options.displayName)
                ? options.displayName : main.gameObject.scene.name;
            payload.assets = new Assets
            {
                collisionMesh = AssetPath(options != null ? options.collisionMesh : null, payload.assets.collisionMesh),
                gridMap = AssetPath(options != null ? options.gridMap : null, payload.assets.gridMap),
                preview = AssetPath(options != null ? options.preview : null, payload.assets.preview)
            };
            // Capture cameras use absolute runtime coordinates, independent of template cameras.
            payload.worldTransform = new TransformData();
            payload.cameraRig = new Placement();
            payload.mainCamera = CameraPlacement.From(main, coordinateRoot, unitScale, 0);
            payload.sideCamera = CameraPlacement.From(side, coordinateRoot, unitScale, 1);
            payload.videoMaterials = new[]
            {
                new Video { name = "main", targetRendererName = options != null ? options.mainRendererName : "Screen_Main" },
                new Video { name = "right", targetRendererName = options != null ? options.sideRendererName : "Screen_Right" }
            };
            payload.recording = new RecordingInfo
            {
                sourceScene = main.gameObject.scene.path,
                capturedAtUtc = DateTime.UtcNow.ToString("O"),
                coordinateSpace = coordinateRoot != null ? "worldCoordinateRoot" : "recordingScene",
                coordinateRoot = coordinateRoot != null ? coordinateRoot.name : "",
                mainResolution = new[] { main.pixelWidth, main.pixelHeight },
                sideResolution = new[] { side.pixelWidth, side.pixelHeight }
            };
            return new LYNOOKRecordingWorldExporter(folder, mainBaseName, sideBaseName, payload);
        }

        public bool TryWrite()
        {
            if (written)
                return true;
            try
            {
                // Prefer the finalized MOV, but support MP4-only or a failed remux.
                // Each panel is resolved separately, so a partial remux stays usable.
                config.videoMaterials[0].video = FindVideo(mainBaseName);
                config.videoMaterials[1].video = FindVideo(sideBaseName);
                string path = Path.Combine(folder, "world_config.json");
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(config, true) + Environment.NewLine);
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                written = true;
                Debug.Log("LYNOOK world_config.json exported: " + path
                    + ". Supply the referenced GLB separately; coordinate space=" + config.recording.coordinateSpace + ".");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("LYNOOK world_config.json export failed: " + exception.Message);
                return false;
            }
        }

        string FindVideo(string baseName)
        {
            foreach (string extension in new[] { ".mov", ".mp4" })
            {
                string relative = RelativePath(baseName + extension);
                string path = Path.Combine(folder, relative);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return relative;
            }
            throw new FileNotFoundException("No completed panel video for " + baseName + "; world config was not published.");
        }

        static string RelativePath(string value)
        {
            string path = (value ?? "").Replace('\\', '/');
            if (Path.IsPathRooted(path) || path.Contains(":") || path.Split('/').Any(part => part == ".."))
                throw new InvalidOperationException("World asset paths must stay inside the take folder: " + path);
            return path;
        }

        static string AssetPath(string value, string fallback) => RelativePath(string.IsNullOrWhiteSpace(value) ? fallback : value);

        static float ValidateCoordinateRoot(Transform root)
        {
            if (root == null)
                return 1f;
            Matrix4x4 matrix = root.localToWorldMatrix;
            float scale = matrix.MultiplyVector(Vector3.right).magnitude;
            if (scale <= 0.000001f)
                throw new InvalidOperationException("World coordinate root must have positive uniform scale.");
            foreach (Vector3 axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                if ((matrix.MultiplyVector(axis) / scale - root.rotation * axis).magnitude > 0.0001f)
                    throw new InvalidOperationException("World coordinate root must have positive uniform scale, without reflection or shear. Use a separate GLB coordinate frame instead of a mirrored Gaussian transform.");
            }
            return scale;
        }

        static float[] Values(Vector3 value) => new[] { value.x, value.y, value.z };

#pragma warning disable CS0649 // Populated from the editable defaults JSON by JsonUtility.
        [Serializable]
        sealed class Config
        {
            public string worldId;
            public string displayName;
            public int version = 1;
            public Assets assets;
            public TransformData worldTransform;
            public TransformData playerSpawn;
            public Placement avatarSpawn;
            public ScenePoint[] scenePoints;
            public ActivityPoint[] activityPoints;
            public Placement cameraRig = new Placement();
            public CameraPlacement mainCamera;
            public CameraPlacement sideCamera;
            public Video[] videoMaterials;
            public Navigation navigation;
            public Transition transition;
            public RectBall rectBall;
            public ContinuityTestGeometry continuityTestGeometry;
            // Extra metadata is ignored by the receiving project's WorldConfig parser.
            public RecordingInfo recording;
        }

        [Serializable]
        sealed class Assets
        {
            public string collisionMesh;
            public string gridMap;
            public string preview;
        }

        [Serializable]
        class TransformData
        {
            public float[] position = { 0, 0, 0 };
            public float[] rotationEuler = { 0, 0, 0 };
            public float[] scale = { 1, 1, 1 };
        }

        [Serializable]
        class Placement : TransformData
        {
            public bool applyTransform = true;
            public float[] rotationQuaternion = Array.Empty<float>();
        }

        [Serializable]
        sealed class ScenePoint
        {
            public int id;
            public string type;
            public Placement transform;
        }

        [Serializable]
        sealed class ActivityPoint
        {
            public string id;
            public string type;
            public Placement transform;
            public float[] approachPosition;
            public float weight;
            public float minStaySeconds;
            public float maxStaySeconds;
        }

        [Serializable]
        sealed class Navigation
        {
            public float[] gridOrigin;
            public int gridSizeX, gridSizeZ;
            public float cellSize, agentRadius;
        }

        [Serializable]
        sealed class Transition
        {
            public float[] scanOrigin;
            public float scanDuration, scanWidth, scanMaxRadius, scanIntensity;
            public float[] scanColor;
            public bool duplicateCollisionMeshForScan;
        }

        [Serializable]
        sealed class RectBall : TransformData
        {
            public bool enabled;
        }

        [Serializable]
        sealed class ContinuityTestGeometry
        {
            public bool enabled;
            public int gridHalfCount;
            public float gridSpacing;
        }

        [Serializable]
        sealed class CameraPlacement : Placement
        {
            public float fieldOfView;
            public int targetDisplay;
            public Frustum offAxisFrustum;

            public static CameraPlacement From(Camera camera, Transform root, float unitScale, int display)
            {
                Matrix4x4 projection = camera.projectionMatrix;
                if (camera.orthographic || Mathf.Abs(projection.m32 + 1) > 0.00001f
                    || Mathf.Abs(projection.m33) > 0.00001f)
                    throw new InvalidOperationException("World export supports perspective panel cameras only.");
                // Recover the frustum from the projection actually used by the recorder.
                // This also preserves aspect ratio and clipping for ordinary perspective.
                float near = projection.m23 / (projection.m22 - 1f);
                float far = projection.m23 / (projection.m22 + 1f);
                var frustum = new Frustum
                {
                    left = near * (projection.m02 - 1f) / projection.m00 / unitScale,
                    right = near * (projection.m02 + 1f) / projection.m00 / unitScale,
                    bottom = near * (projection.m12 - 1f) / projection.m11 / unitScale,
                    top = near * (projection.m12 + 1f) / projection.m11 / unitScale,
                    near = near / unitScale,
                    far = far / unitScale
                };
                Matrix4x4 rebuilt = Matrix4x4.Frustum(frustum.left * unitScale, frustum.right * unitScale,
                    frustum.bottom * unitScale, frustum.top * unitScale, near, far);
                for (int i = 0; i < 16; i++)
                    if (float.IsNaN(projection[i]) || float.IsInfinity(projection[i])
                        || float.IsNaN(rebuilt[i]) || float.IsInfinity(rebuilt[i])
                        || Mathf.Abs(projection[i] - rebuilt[i]) > 0.0001f)
                        throw new InvalidOperationException("Camera projection cannot be represented by the runtime world frustum.");
                if (!(frustum.near > 0 && frustum.far > frustum.near
                    && frustum.right > frustum.left && frustum.top > frustum.bottom))
                    throw new InvalidOperationException("Invalid recording camera frustum.");
                Vector3 position = root != null ? root.InverseTransformPoint(camera.transform.position) : camera.transform.position;
                Quaternion rotation = root != null ? Quaternion.Inverse(root.rotation) * camera.transform.rotation : camera.transform.rotation;
                return new CameraPlacement
                {
                    position = Values(position),
                    rotationEuler = Values(rotation.eulerAngles),
                    rotationQuaternion = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                    fieldOfView = camera.fieldOfView,
                    targetDisplay = display,
                    offAxisFrustum = frustum
                };
            }
        }

        [Serializable]
        sealed class Frustum
        {
            public bool enabled = true;
            public float left, right, bottom, top, near, far;
        }

        [Serializable]
        sealed class Video
        {
            public string name;
            public string targetRendererName;
            public string video;
            public bool loop = true;
            public bool playOnLoad = true;
            public float volume = 0;
        }

        [Serializable]
        sealed class RecordingInfo
        {
            public string defaultsSource = "world_room_e";
            public string sourceScene;
            public string capturedAtUtc;
            public string coordinateSpace;
            public string coordinateRoot;
            public int[] mainResolution;
            public int[] sideResolution;
            public bool meshAlignmentVerified = false;
        }
#pragma warning restore CS0649
    }
}
#endif
