using UnityEngine;
using UnityEngine.Playables;

namespace Lynook.DualScreen
{
    [DisallowMultipleComponent]
    public sealed class LYNOOKSeamMotion : MonoBehaviour
    {
        [SerializeField] PlayableDirector timeSource;
        [SerializeField] Transform movingTarget;
        [SerializeField] float duration = 10f;
        [SerializeField] Vector3 startPosition;
        [SerializeField] Vector3 seamPosition;
        [SerializeField] Vector3 endPosition;

        public void SetReferences(PlayableDirector director, Transform target)
        {
            timeSource = director;
            movingTarget = target;
            ApplyAtTime(0.0);
        }

        public void SetPath(Vector3 start, Vector3 seam, Vector3 end)
        {
            startPosition = start;
            seamPosition = seam;
            endPosition = end;
            ApplyAtTime(0.0);
        }

        void LateUpdate()
        {
            if (timeSource == null || movingTarget == null)
                return;

            ApplyAtTime(timeSource.time);
        }

        public void ApplyAtTime(double seconds)
        {
            if (movingTarget == null)
                return;

            float normalized = Mathf.Clamp01((float)(seconds / duration));
            Vector3 basePosition = normalized <= 0.5f
                ? Vector3.Lerp(startPosition, seamPosition, normalized * 2f)
                : Vector3.Lerp(seamPosition, endPosition, (normalized - 0.5f) * 2f);
            float lift = Mathf.Sin(normalized * Mathf.PI) * 0.45f;
            movingTarget.localPosition = basePosition + Vector3.up * lift;
        }
    }
}
