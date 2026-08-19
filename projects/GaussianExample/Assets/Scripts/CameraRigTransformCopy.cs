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
