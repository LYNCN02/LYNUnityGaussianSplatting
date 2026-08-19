#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(CameraRigTransformCopy))]
public class CameraRigTransformCopyEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (GUILayout.Button("复制 Transform JSON"))
        {
            var copy = (CameraRigTransformCopy)target;
            GUIUtility.systemCopyBuffer = copy.ToJson();
            Debug.Log("已复制 CameraRig Transform JSON 到剪贴板");
        }
    }
}
#endif
