#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(CameraRigTransformCopy))]
public class CameraRigTransformCopyEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var copy = (CameraRigTransformCopy)target;
        if (GUILayout.Button("复制 Transform JSON"))
        {
            GUIUtility.systemCopyBuffer = copy.ToJson();
            Debug.Log("已复制 CameraRig Transform JSON 到剪贴板");
        }

        var offAxisRig = copy.GetComponent<Lynook.DualScreen.LYNOOKDualCameraRig>();
        if (offAxisRig == null)
            return;

        GUILayout.Space(6f);
        if (GUILayout.Button("复制 Off-Axis 标定 JSON"))
        {
            try
            {
                GUIUtility.systemCopyBuffer = copy.ToOffAxisCalibrationJson();
                Debug.Log("已复制 Off-Axis 完整标定 JSON 到剪贴板；原 Transform JSON 格式未改变");
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"无法复制 Off-Axis 标定 JSON：{exception.Message}", copy);
            }
        }
    }
}
#endif
