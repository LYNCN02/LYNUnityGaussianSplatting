using GaussianSplatting.Runtime;
using UnityEngine;

namespace Lynook.DualScreen
{
    [DisallowMultipleComponent]
    public sealed class LYNOOKWorldAuthoring : MonoBehaviour
    {
        public string worldId;
        public string displayName;
        public string workspacePath;
        public string gaussianSourcePath;
        public string collisionSourcePath;
        public string gaussianSha256;
        public string collisionSha256;
        public Transform coordinateRoot;
        public GaussianSplatRenderer gaussian;
        public GameObject collisionObject;
        public LYNOOKDualCameraRig cameraRig;
        public Transform avatarSpawn;
        public Transform activityPoints;
        public bool spawnPlaced;
        public bool alignmentConfirmed;
        public bool showCollision;
        public float floorHeight;
        public Vector2 walkCenter;
        public Vector2 walkSize = new Vector2(3, 3);
        public float agentRadius = 0.25f;
        public float agentHeight = 1.7f;
        public float pointSpacing = 0.8f;
        public float floorTolerance = 0.12f;
        public int maximumPoints = 12;
        public int recordingSeconds = 10;

        void OnDrawGizmos()
        {
            if (coordinateRoot == null) return;
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = coordinateRoot.localToWorldMatrix;
            Gizmos.color = new Color(0.25f, 0.8f, 0.85f, 0.7f);
            Gizmos.DrawWireCube(new Vector3(walkCenter.x, floorHeight, walkCenter.y),
                new Vector3(walkSize.x, 0.01f, walkSize.y));
            Gizmos.matrix = previous;
            if (spawnPlaced && avatarSpawn != null) DrawMarker(avatarSpawn, Color.green);
            if (activityPoints != null)
                foreach (Transform point in activityPoints) DrawMarker(point, new Color(1f, 0.7f, 0.2f));
        }

        void DrawMarker(Transform marker, Color color)
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = coordinateRoot.localToWorldMatrix;
            Vector3 foot = coordinateRoot.InverseTransformPoint(marker.position);
            Gizmos.color = color;
            Gizmos.DrawWireCube(foot + Vector3.up * agentHeight * 0.5f,
                new Vector3(agentRadius * 2, agentHeight, agentRadius * 2));
            Vector3 forward = coordinateRoot.InverseTransformDirection(marker.forward);
            Gizmos.DrawRay(foot + Vector3.up * 0.05f, forward * 0.5f);
            Gizmos.matrix = previous;
        }
    }
}
