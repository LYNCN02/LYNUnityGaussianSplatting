#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKCornerBox45RuntimeExporter
    {
        const string ScenePath = LYNOOKSceneCatalog.CornerRoom;
        const string OutputPath = "Recordings/LYNOOK/CornerBox45/corner45_runtime_calibration.json";

        [MenuItem("Tools/LYNOOK/Room Scenes/03 Corner Room - View 45/Export Calibration JSON")]
        public static void ExportFromMenu()
        {
            Export(false);
        }

        public static void ExportFromCommandLine()
        {
            try
            {
                Export(true);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        static void Export(bool commandLine)
        {
            if (!File.Exists(Path.GetFullPath(ScenePath)))
                throw new FileNotFoundException("CornerBox45 scene was not found.", ScenePath);

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            LYNOOKDualCameraRig rig = UnityEngine.Object.FindFirstObjectByType<LYNOOKDualCameraRig>();
            if (rig == null)
                throw new MissingReferenceException("CornerBox45 scene has no LYNOOKDualCameraRig.");

            rig.ApplyConfiguration();
            if (!rig.TryValidate(out string validationReport))
                throw new InvalidOperationException(validationReport);

            GameObject gaussianScene = GameObject.Find("GaussianScene_ModernBedroom");
            if (gaussianScene == null)
                throw new MissingReferenceException("GaussianScene_ModernBedroom was not found.");
            GaussianSplatRenderer gaussianRenderer = gaussianScene.GetComponent<GaussianSplatRenderer>();
            if (gaussianRenderer == null || gaussianRenderer.m_Asset == null)
                throw new MissingReferenceException("GaussianScene_ModernBedroom has no Gaussian source asset.");

            Camera main = rig.MainCaptureCamera;
            Camera side = rig.SideCaptureCamera;
            if (main == null || side == null)
                throw new MissingReferenceException("CornerBox45 capture cameras are incomplete.");

            float sharedEyeDelta = Vector3.Distance(main.transform.position, side.transform.position);
            if (sharedEyeDelta > 0.000001f)
                throw new InvalidOperationException($"Capture cameras do not share one eye: delta={sharedEyeDelta:F9} m.");

            string gaussianAssetPath = AssetDatabase.GetAssetPath(gaussianRenderer.m_Asset);
            string gaussianAssetAbsolutePath = Path.GetFullPath(gaussianAssetPath);
            if (!File.Exists(gaussianAssetAbsolutePath))
                throw new FileNotFoundException("Gaussian source asset was not found.", gaussianAssetAbsolutePath);

            rig.GetScreenGeometries(out LYNOOKScreenGeometry mainScreen, out LYNOOKScreenGeometry sideScreen);
            var payload = new RuntimeCalibrationExport
            {
                schemaVersion = 1,
                profileId = "corner_box_45",
                sourceScene = ScenePath,
                exportedAtUtc = DateTime.UtcNow.ToString("O"),
                calibrationValuesConfirmed = rig.CalibrationValuesConfirmed,
                sharedEyeDeltaMeters = sharedEyeDelta,
                captureRig = TransformData.From(rig.CaptureRig),
                sharedEyeWorldPosition = Vector3Data.From(rig.SharedEyeWorldPosition),
                mainCamera = CameraData.From(main, rig.MainFrustum),
                sideCamera = CameraData.From(side, rig.SideFrustum),
                mainScreen = ScreenData.From(mainScreen),
                sideScreen = ScreenData.From(sideScreen),
                gaussianSceneTransform = TransformData.From(gaussianScene.transform),
                gaussianSource = new SourceAssetData
                {
                    assetPath = gaussianAssetPath,
                    assetGuid = AssetDatabase.AssetPathToGUID(gaussianAssetPath),
                    byteLength = new FileInfo(gaussianAssetAbsolutePath).Length,
                    sha256 = ComputeSha256(gaussianAssetAbsolutePath)
                }
            };

            string outputAbsolutePath = Path.GetFullPath(OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputAbsolutePath));
            File.WriteAllText(outputAbsolutePath, JsonUtility.ToJson(payload, true) + Environment.NewLine);
            Debug.Log(
                $"LYNOOK CornerBox45 runtime calibration exported: {outputAbsolutePath}\n" +
                $"shared eye={rig.SharedEyeWorldPosition:F6}, delta={sharedEyeDelta:F9} m, " +
                $"gaussian asset={payload.gaussianSource.assetPath}, sha256={payload.gaussianSource.sha256}");

            if (!commandLine)
            {
                EditorUtility.RevealInFinder(outputAbsolutePath);
                AssetDatabase.Refresh();
            }
        }

        static string ComputeSha256(string path)
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        [Serializable]
        sealed class RuntimeCalibrationExport
        {
            public int schemaVersion;
            public string profileId;
            public string sourceScene;
            public string exportedAtUtc;
            public bool calibrationValuesConfirmed;
            public float sharedEyeDeltaMeters;
            public TransformData captureRig;
            public Vector3Data sharedEyeWorldPosition;
            public CameraData mainCamera;
            public CameraData sideCamera;
            public ScreenData mainScreen;
            public ScreenData sideScreen;
            public TransformData gaussianSceneTransform;
            public SourceAssetData gaussianSource;
        }

        [Serializable]
        sealed class SourceAssetData
        {
            public string assetPath;
            public string assetGuid;
            public long byteLength;
            public string sha256;
        }

        [Serializable]
        sealed class Vector3Data
        {
            public float x;
            public float y;
            public float z;

            public static Vector3Data From(Vector3 value)
            {
                return new Vector3Data { x = value.x, y = value.y, z = value.z };
            }
        }

        [Serializable]
        sealed class QuaternionData
        {
            public float x;
            public float y;
            public float z;
            public float w;

            public static QuaternionData From(Quaternion value)
            {
                return new QuaternionData { x = value.x, y = value.y, z = value.z, w = value.w };
            }
        }

        [Serializable]
        sealed class TransformData
        {
            public Vector3Data position;
            public Vector3Data rotationEuler;
            public QuaternionData rotationQuaternion;
            public Vector3Data scale;

            public static TransformData From(Transform value)
            {
                return new TransformData
                {
                    position = Vector3Data.From(value.position),
                    rotationEuler = Vector3Data.From(value.eulerAngles),
                    rotationQuaternion = QuaternionData.From(value.rotation),
                    scale = Vector3Data.From(value.lossyScale)
                };
            }
        }

        [Serializable]
        sealed class CameraData
        {
            public TransformData transform;
            public float fieldOfView;
            public int targetDisplay;
            public float nearClipMeters;
            public float farClipMeters;
            public OffAxisFrustumData offAxisFrustum;
            public Matrix4x4Data projectionMatrix;

            public static CameraData From(Camera camera, LYNOOKOffAxisFrustum frustum)
            {
                return new CameraData
                {
                    transform = TransformData.From(camera.transform),
                    fieldOfView = camera.fieldOfView,
                    targetDisplay = camera.targetDisplay,
                    nearClipMeters = camera.nearClipPlane,
                    farClipMeters = camera.farClipPlane,
                    offAxisFrustum = OffAxisFrustumData.From(frustum),
                    projectionMatrix = Matrix4x4Data.From(camera.projectionMatrix)
                };
            }
        }

        [Serializable]
        sealed class OffAxisFrustumData
        {
            public float left;
            public float right;
            public float bottom;
            public float top;
            public float near;
            public float far;

            public static OffAxisFrustumData From(LYNOOKOffAxisFrustum value)
            {
                return new OffAxisFrustumData
                {
                    left = value.left,
                    right = value.right,
                    bottom = value.bottom,
                    top = value.top,
                    near = value.near,
                    far = value.far
                };
            }
        }

        [Serializable]
        sealed class Matrix4x4Data
        {
            public float[] columnMajor;

            public static Matrix4x4Data From(Matrix4x4 value)
            {
                var elements = new float[16];
                for (int column = 0; column < 4; column++)
                {
                    for (int row = 0; row < 4; row++)
                        elements[column * 4 + row] = value[row, column];
                }
                return new Matrix4x4Data { columnMajor = elements };
            }
        }

        [Serializable]
        sealed class ScreenData
        {
            public Vector3Data center;
            public Vector3Data right;
            public Vector3Data up;
            public Vector3Data forward;
            public float widthMeters;
            public float heightMeters;

            public static ScreenData From(LYNOOKScreenGeometry value)
            {
                return new ScreenData
                {
                    center = Vector3Data.From(value.center),
                    right = Vector3Data.From(value.right),
                    up = Vector3Data.From(value.up),
                    forward = Vector3Data.From(value.forward),
                    widthMeters = value.widthMeters,
                    heightMeters = value.heightMeters
                };
            }
        }
    }
}
#endif
