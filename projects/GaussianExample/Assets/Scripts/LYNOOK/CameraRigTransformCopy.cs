using System;
using UnityEngine;

public class CameraRigTransformCopy : MonoBehaviour
{
    [Serializable]
    public struct Vec3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public struct TransformData
    {
        public Vec3 position;
        public Vec3 rotation;
        public Vec3 scale;
    }

    [Serializable]
    public struct CameraRigTransformsJson
    {
        public TransformData rig;
        public TransformData mainscamera;
        public TransformData sidecamera;
    }

    [Serializable]
    public struct Vec2
    {
        public float x;
        public float y;
    }

    [Serializable]
    public struct Int2
    {
        public int x;
        public int y;
    }

    [Serializable]
    public struct FrustumData
    {
        public float left;
        public float right;
        public float bottom;
        public float top;
        public float near;
        public float far;
        public float m00;
        public float m02;
        public float m11;
        public float m12;
    }

    [Serializable]
    public struct OffAxisScreenData
    {
        public Vec2 visibleSizeMm;
        public Vector3Data configuredCenterLocalMm;
        public Vector3Data resolvedCenterLocalMm;
        public Vector3Data eulerLocalDegrees;
        public Vector3Data eulerTrimDegrees;
        public TransformData cameraWorldTransform;
        public Int2 outputResolution;
        public FrustumData frustum;
    }

    [Serializable]
    public struct Vector3Data
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public struct OffAxisEyeData
    {
        public Vector3Data localMm;
        public Vector3Data worldMeters;
    }

    [Serializable]
    public struct OffAxisHingeData
    {
        public float screenAngleDegrees;
        public float seamGapMm;
        public bool deriveSidePositionFromHinge;
        public bool topAligned;
        public float sideBottomExtensionMm;
    }

    [Serializable]
    public struct OffAxisClippingData
    {
        public float nearMeters;
        public float farMeters;
    }

    [Serializable]
    public struct OffAxisCalibrationJson
    {
        public int schemaVersion;
        public string mode;
        public string lengthUnits;
        public bool calibrationValuesConfirmed;
        public TransformData captureRigWorldTransform;
        public OffAxisEyeData sharedEye;
        public OffAxisScreenData mainScreen;
        public OffAxisScreenData sideScreen;
        public OffAxisHingeData hinge;
        public OffAxisClippingData clipping;
    }

    public string ToJson()
    {
        Transform rigTransform = transform;
        Transform mainCamera = null;
        Transform sideCamera = null;

        var dualCameraRig = GetComponent<Lynook.DualScreen.LYNOOKDualCameraRig>();
        if (dualCameraRig != null)
        {
            rigTransform = dualCameraRig.CaptureRig != null
                ? dualCameraRig.CaptureRig
                : transform;
            mainCamera = dualCameraRig.MainCaptureCamera != null
                ? dualCameraRig.MainCaptureCamera.transform
                : null;
            sideCamera = dualCameraRig.SideCaptureCamera != null
                ? dualCameraRig.SideCaptureCamera.transform
                : null;
        }

        if (mainCamera == null || sideCamera == null)
        {
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                if (child == transform)
                    continue;

                if (mainCamera == null
                    && (child.name == "Main Camera"
                        || child.name == "MainCaptureCamera"
                        || child.CompareTag("MainCamera")))
                {
                    mainCamera = child;
                }
                else if (sideCamera == null
                    && (child.name == "SideCamera"
                        || child.name == "SideCaptureCamera"
                        || child.tag == "SideC"))
                {
                    sideCamera = child;
                }
            }
        }

        var data = new CameraRigTransformsJson
        {
            rig = FromTransform(rigTransform),
            mainscamera = mainCamera != null ? FromTransform(mainCamera) : default,
            sidecamera = sideCamera != null ? FromTransform(sideCamera) : default,
        };

        return JsonUtility.ToJson(data, true);
    }

    /// <summary>
    /// Exports the complete physical-screen calibration needed to reconstruct the
    /// off-axis cameras. The legacy ToJson() schema above remains unchanged.
    /// </summary>
    public string ToOffAxisCalibrationJson()
    {
        var dualCameraRig = GetComponent<Lynook.DualScreen.LYNOOKDualCameraRig>();
        if (dualCameraRig == null)
        {
            throw new InvalidOperationException(
                "Off-Axis calibration export requires LYNOOKDualCameraRig on the same GameObject.");
        }

        dualCameraRig.ApplyConfiguration();
        if (!dualCameraRig.TryValidate(out string validationReport))
            throw new InvalidOperationException(validationReport);

        dualCameraRig.GetScreenGeometries(out var mainGeometry, out var sideGeometry);
        Transform rigTransform = dualCameraRig.CaptureRig != null
            ? dualCameraRig.CaptureRig
            : transform;
        Vector3 resolvedMainCenterLocalMm = rigTransform.InverseTransformPoint(mainGeometry.center) * 1000f;
        Vector3 resolvedSideCenterLocalMm = rigTransform.InverseTransformPoint(sideGeometry.center) * 1000f;
        Vector3 resolvedMainEulerLocal = (
            Quaternion.Inverse(rigTransform.rotation) * dualCameraRig.MainCaptureCamera.transform.rotation).eulerAngles;
        Vector3 resolvedSideEulerLocal = (
            Quaternion.Inverse(rigTransform.rotation) * dualCameraRig.SideCaptureCamera.transform.rotation).eulerAngles;
        bool topAligned = Mathf.Abs(Vector3.Dot(
            sideGeometry.TopLeft - mainGeometry.TopRight,
            mainGeometry.up)) < 0.00001f;
        float sideBottomExtensionMm = Vector3.Dot(
            mainGeometry.BottomRight - sideGeometry.BottomLeft,
            mainGeometry.up) * 1000f;

        var data = new OffAxisCalibrationJson
        {
            schemaVersion = 1,
            mode = "off_axis_physical_screens_v1",
            lengthUnits = "screen_and_eye_local=millimeters; world_and_clipping=meters",
            calibrationValuesConfirmed = dualCameraRig.CalibrationValuesConfirmed,
            captureRigWorldTransform = FromTransform(rigTransform),
            sharedEye = new OffAxisEyeData
            {
                localMm = FromVector3(dualCameraRig.TargetEyeLocalMm),
                worldMeters = FromVector3(dualCameraRig.SharedEyeWorldPosition),
            },
            mainScreen = new OffAxisScreenData
            {
                visibleSizeMm = new Vec2
                {
                    x = dualCameraRig.MainScreenWidthMm,
                    y = dualCameraRig.MainScreenHeightMm,
                },
                configuredCenterLocalMm = FromVector3(dualCameraRig.MainScreenCenterLocalMm),
                resolvedCenterLocalMm = FromVector3(resolvedMainCenterLocalMm),
                eulerLocalDegrees = FromVector3(resolvedMainEulerLocal),
                eulerTrimDegrees = FromVector3(Vector3.zero),
                cameraWorldTransform = FromTransform(dualCameraRig.MainCaptureCamera.transform),
                outputResolution = ResolutionOf(
                    dualCameraRig.MainCaptureTexture,
                    Lynook.DualScreen.LYNOOKDualCameraRig.MainWidth,
                    Lynook.DualScreen.LYNOOKDualCameraRig.MainHeight),
                frustum = FromFrustum(dualCameraRig.MainFrustum),
            },
            sideScreen = new OffAxisScreenData
            {
                visibleSizeMm = new Vec2
                {
                    x = dualCameraRig.SideScreenWidthMm,
                    y = dualCameraRig.SideScreenHeightMm,
                },
                configuredCenterLocalMm = FromVector3(dualCameraRig.SideScreenCenterLocalMm),
                resolvedCenterLocalMm = FromVector3(resolvedSideCenterLocalMm),
                eulerLocalDegrees = FromVector3(resolvedSideEulerLocal),
                eulerTrimDegrees = FromVector3(dualCameraRig.SideScreenEulerTrimDegrees),
                cameraWorldTransform = FromTransform(dualCameraRig.SideCaptureCamera.transform),
                outputResolution = ResolutionOf(
                    dualCameraRig.SideCaptureTexture,
                    Lynook.DualScreen.LYNOOKDualCameraRig.SideWidth,
                    Lynook.DualScreen.LYNOOKDualCameraRig.SideHeight),
                frustum = FromFrustum(dualCameraRig.SideFrustum),
            },
            hinge = new OffAxisHingeData
            {
                screenAngleDegrees = dualCameraRig.ScreenAngleDegrees,
                seamGapMm = dualCameraRig.SeamGapMm,
                deriveSidePositionFromHinge = dualCameraRig.DeriveSidePositionFromHinge,
                topAligned = topAligned,
                sideBottomExtensionMm = sideBottomExtensionMm,
            },
            clipping = new OffAxisClippingData
            {
                nearMeters = dualCameraRig.NearClipMeters,
                farMeters = dualCameraRig.FarClipMeters,
            },
        };

        return JsonUtility.ToJson(data, true);
    }

    static Int2 ResolutionOf(RenderTexture texture, int fallbackWidth, int fallbackHeight)
    {
        return new Int2
        {
            x = texture != null ? texture.width : fallbackWidth,
            y = texture != null ? texture.height : fallbackHeight,
        };
    }

    static FrustumData FromFrustum(Lynook.DualScreen.LYNOOKOffAxisFrustum frustum)
    {
        Matrix4x4 matrix = frustum.Matrix;
        return new FrustumData
        {
            left = frustum.left,
            right = frustum.right,
            bottom = frustum.bottom,
            top = frustum.top,
            near = frustum.near,
            far = frustum.far,
            m00 = matrix.m00,
            m02 = matrix.m02,
            m11 = matrix.m11,
            m12 = matrix.m12,
        };
    }

    static Vector3Data FromVector3(Vector3 value)
    {
        return new Vector3Data { x = value.x, y = value.y, z = value.z };
    }

    static TransformData FromTransform(Transform t)
    {
        var euler = t.eulerAngles;
        var pos = t.position;
        var scale = t.lossyScale;

        return new TransformData
        {
            position = new Vec3 { x = pos.x, y = pos.y, z = pos.z },
            rotation = new Vec3 { x = euler.x, y = euler.y, z = euler.z },
            scale = new Vec3 { x = scale.x, y = scale.y, z = scale.z },
        };
    }
}
