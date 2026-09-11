using UnityEngine;

namespace Lynook.DualScreen
{
    /// <summary>
    /// Drives the corner-box hero from behind the display planes to a controlled
    /// pop-out position, then returns it to the virtual volume during one recording.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LYNOOKCornerBoxMotion : MonoBehaviour
    {
        [SerializeField] Transform hero;
        [SerializeField] Transform orbitRoot;
        [SerializeField] Vector3 insidePosition;
        [SerializeField] Vector3 cornerPosition;
        [SerializeField] Vector3 popOutPosition;
        [SerializeField, Min(0.1f)] float durationSeconds = 10f;

        Vector3 heroInitialScale = Vector3.one;
        float startTime;

        public void SetReferences(
            Transform heroTransform,
            Transform orbitTransform,
            Vector3 inside,
            Vector3 corner,
            Vector3 popOut,
            float duration)
        {
            hero = heroTransform;
            orbitRoot = orbitTransform;
            insidePosition = inside;
            cornerPosition = corner;
            popOutPosition = popOut;
            durationSeconds = Mathf.Max(0.1f, duration);
            if (hero != null)
                heroInitialScale = hero.localScale;
            ApplyAtNormalizedTime(0f);
        }

        void OnEnable()
        {
            startTime = Time.timeSinceLevelLoad;
            if (hero != null)
                heroInitialScale = hero.localScale;
            ApplyAtNormalizedTime(0f);
        }

        void Update()
        {
            float elapsed = Mathf.Repeat(Time.timeSinceLevelLoad - startTime, durationSeconds);
            ApplyAtNormalizedTime(elapsed / durationSeconds);
        }

        void ApplyAtNormalizedTime(float normalizedTime)
        {
            float t = Mathf.Clamp01(normalizedTime);
            float travel;
            if (t <= 0.62f)
                travel = Smooth01(t / 0.62f);
            else
                travel = 1f - Smooth01((t - 0.62f) / 0.38f);

            if (hero != null)
            {
                hero.position = QuadraticBezier(insidePosition, cornerPosition, popOutPosition, travel);
                hero.rotation = Quaternion.Euler(18f + 55f * travel, 360f * t, 12f * Mathf.Sin(t * Mathf.PI * 2f));
                hero.localScale = heroInitialScale * (1f + 0.08f * Mathf.Sin(t * Mathf.PI * 4f));
            }

            if (orbitRoot != null)
                orbitRoot.localRotation = Quaternion.Euler(25f, t * 720f, 12f);
        }

        static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        static Vector3 QuadraticBezier(Vector3 start, Vector3 control, Vector3 end, float t)
        {
            float inverse = 1f - t;
            return inverse * inverse * start + 2f * inverse * t * control + t * t * end;
        }
    }
}
