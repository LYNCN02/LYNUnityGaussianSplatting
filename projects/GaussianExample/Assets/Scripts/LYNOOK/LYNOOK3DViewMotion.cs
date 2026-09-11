using UnityEngine;

namespace Lynook.DualScreen
{
    /// <summary>
    /// Deterministic, shared-world motion for the dual-screen 3D-view recording test.
    /// Both capture cameras observe these same transforms in the same player loop.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LYNOOK3DViewMotion : MonoBehaviour
    {
        [SerializeField] Transform depthProbe;
        [SerializeField] Transform orbitPivot;
        [SerializeField] Vector3 pathStart;
        [SerializeField] Vector3 pathControl;
        [SerializeField] Vector3 pathEnd;
        [SerializeField, Min(0.1f)] float durationSeconds = 10f;
        [SerializeField] float orbitDegreesPerSecond = 36f;

        Vector3 initialProbeScale = Vector3.one;

        public void SetReferences(
            Transform movingDepthProbe,
            Transform rotatingOrbitPivot,
            Vector3 start,
            Vector3 control,
            Vector3 end,
            float duration)
        {
            depthProbe = movingDepthProbe;
            orbitPivot = rotatingOrbitPivot;
            pathStart = start;
            pathControl = control;
            pathEnd = end;
            durationSeconds = Mathf.Max(0.1f, duration);
            if (depthProbe != null)
                initialProbeScale = depthProbe.localScale;
            ApplyAtTime(0f);
        }

        void OnEnable()
        {
            if (depthProbe != null)
                initialProbeScale = depthProbe.localScale;
            ApplyAtTime(0f);
        }

        void Update()
        {
            ApplyAtTime(Mathf.Repeat(Time.timeSinceLevelLoad, durationSeconds));
        }

        void ApplyAtTime(float timeSeconds)
        {
            float t = Mathf.Clamp01(timeSeconds / durationSeconds);
            float eased = t * t * (3f - 2f * t);
            if (depthProbe != null)
            {
                depthProbe.position = QuadraticBezier(pathStart, pathControl, pathEnd, eased);
                depthProbe.rotation = Quaternion.Euler(35f * t, 360f * t, 18f * Mathf.Sin(t * Mathf.PI * 2f));
                depthProbe.localScale = initialProbeScale * (1f + 0.12f * Mathf.Sin(t * Mathf.PI * 4f));
            }

            if (orbitPivot != null)
                orbitPivot.localRotation = Quaternion.Euler(12f, timeSeconds * orbitDegreesPerSecond, 8f);
        }

        static Vector3 QuadraticBezier(Vector3 start, Vector3 control, Vector3 end, float t)
        {
            float inverse = 1f - t;
            return inverse * inverse * start + 2f * inverse * t * control + t * t * end;
        }
    }
}
