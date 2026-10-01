using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace Lynook.DualScreen
{
    [DisallowMultipleComponent]
    public sealed class LYNOOKWorldAuthoring : MonoBehaviour
    {
        public string worldId;
        public string roomType = LynookRoomTypes.Bedroom;
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
        // 矩形可走范围（walkPolygon 为空时的回退表示）
        public Vector2 walkCenter;
        public Vector2 walkSize = new Vector2(3, 3);
        // 多边形可走范围（房间局部坐标 XZ，按顺序连接，支持任意简单多边形）。
        // 当顶点数 >= 3 时优先使用多边形，否则回退到 walkCenter/walkSize 矩形。
        public List<Vector2> walkPolygon = new List<Vector2>();
        public float agentRadius = 0.25f;
        public float agentHeight = 1.7f;
        public float pointSpacing = 0.8f;
        public float floorTolerance = 0.12f;
        public int maximumPoints = 12;
        public int recordingSeconds = 10;
        // 可走范围可视化厚度（米），向上挤出形成有体积的板体，便于碰撞和观察时辨识。
        public float walkAreaThickness = 0.05f;
        // 部署目标路径：Unity 部署工程的 Assets 目录（或其子目录）的绝对路径。
        // 点击「发布」时把整个房间工作区复制到该路径下，便于一键迁移到运行工程。
        public string deployTargetPath;

        /// <summary>是否使用多边形可走范围。</summary>
        public bool HasWalkPolygon => walkPolygon != null && walkPolygon.Count >= 3;

        /// <summary>判断房间局部坐标 (x,z) 是否在可走范围内（多边形或矩形）。</summary>
        public bool IsInsideWalkArea(Vector2 localXZ)
        {
            if (HasWalkPolygon) return PointInPolygon(localXZ, walkPolygon);
            Vector2 delta = localXZ - walkCenter;
            return Mathf.Abs(delta.x) <= walkSize.x * 0.5f && Mathf.Abs(delta.y) <= walkSize.y * 0.5f;
        }

        /// <summary>返回可走范围的轴对齐包围盒（房间局部坐标 XZ）。</summary>
        public void WalkBounds(out Vector2 center, out Vector2 size)
        {
            if (HasWalkPolygon)
            {
                float minX = walkPolygon[0].x, maxX = minX, minY = walkPolygon[0].y, maxY = minY;
                for (int i = 1; i < walkPolygon.Count; i++)
                {
                    Vector2 p = walkPolygon[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                }
                center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                size = new Vector2(maxX - minX, maxY - minY);
            }
            else
            {
                center = walkCenter;
                size = walkSize;
            }
        }

        public static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 pi = poly[i], pj = poly[j];
                if (((pi.y > p.y) != (pj.y > p.y)) &&
                    (p.x < (pj.x - pi.x) * (p.y - pi.y) / (pj.y - pi.y) + pi.x))
                    inside = !inside;
            }
            return inside;
        }

        // ── Gizmos ─────────────────────────────────────────────────────
        [System.NonSerialized] Mesh walkPolyMesh;

        void OnDrawGizmos()
        {
            if (coordinateRoot == null) return;
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = coordinateRoot.localToWorldMatrix;
            DrawWalkArea();
            Gizmos.matrix = previous;
            if (spawnPlaced && avatarSpawn != null) DrawMarker(avatarSpawn, Color.green);
            if (activityPoints != null)
                foreach (Transform point in activityPoints) DrawMarker(point, new Color(1f, 0.7f, 0.2f));
        }

        void DrawWalkArea()
        {
            Color fill = new Color(0.25f, 0.8f, 0.85f, 0.35f);
            Color outline = new Color(0.25f, 0.8f, 0.85f, 0.9f);
            float thickness = Mathf.Max(0.001f, walkAreaThickness);
            float topY = floorHeight + thickness;
            if (HasWalkPolygon)
            {
                if (walkPolyMesh == null) walkPolyMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                UpdatePolygonMesh(walkPolyMesh, walkPolygon, floorHeight, thickness);
                Gizmos.color = fill;
                Gizmos.DrawMesh(walkPolyMesh);
                // 顶部轮廓线，便于精确辨识边界
                Gizmos.color = outline;
                for (int i = 0; i < walkPolygon.Count; i++)
                {
                    Vector2 a = walkPolygon[i], b = walkPolygon[(i + 1) % walkPolygon.Count];
                    Gizmos.DrawLine(new Vector3(a.x, topY, a.y), new Vector3(b.x, topY, b.y));
                }
            }
            else
            {
                Vector3 walkCenter3 = new Vector3(walkCenter.x, floorHeight + thickness * 0.5f, walkCenter.y);
                Vector3 walkSize3 = new Vector3(walkSize.x, thickness, walkSize.y);
                Gizmos.color = fill;
                Gizmos.DrawCube(walkCenter3, walkSize3);
                Gizmos.color = outline;
                Gizmos.DrawWireCube(walkCenter3, walkSize3);
            }
        }

        // 把简单多边形三角化并向上挤出厚度，写入 mesh（底面 y=bottomY，顶面 y=bottomY+thickness）。
        static void UpdatePolygonMesh(Mesh mesh, List<Vector2> poly, float bottomY, float thickness)
        {
            int n = poly.Count;
            mesh.Clear();
            if (n < 3) { mesh.RecalculateNormals(); return; }
            float topY = bottomY + thickness;
            int[] topTris = EarClip(poly);
            // 顶点：0..n-1 为底面，n..2n-1 为顶面
            var vertices = new Vector3[n * 2];
            for (int i = 0; i < n; i++)
            {
                vertices[i] = new Vector3(poly[i].x, bottomY, poly[i].y);
                vertices[i + n] = new Vector3(poly[i].x, topY, poly[i].y);
            }
            var triangles = new List<int>(topTris.Length * 2 + n * 6);
            // 底面（法线向下，反转缠绕）
            for (int i = 0; i < topTris.Length; i += 3)
            {
                triangles.Add(topTris[i]);
                triangles.Add(topTris[i + 2]);
                triangles.Add(topTris[i + 1]);
            }
            // 顶面（法线向上）
            for (int i = 0; i < topTris.Length; i += 3)
            {
                triangles.Add(topTris[i] + n);
                triangles.Add(topTris[i + 1] + n);
                triangles.Add(topTris[i + 2] + n);
            }
            // 侧面：每条边一个四边形（两个三角形），法线朝外
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int bi = i, bj = j, ti = i + n, tj = j + n;
                triangles.Add(bi); triangles.Add(ti); triangles.Add(tj);
                triangles.Add(bi); triangles.Add(tj); triangles.Add(bj);
            }
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
        }

        static int[] EarClip(List<Vector2> poly)
        {
            int n = poly.Count;
            if (n < 3) return new int[0];
            // 确保逆时针（面积 > 0），耳尖判定用 Cross > 0
            double area = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                area += a.x * b.y - b.x * a.y;
            }
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            if (area < 0) idx.Reverse();

            var triangles = new List<int>();
            int guard = n * n;
            while (idx.Count > 3 && guard-- > 0)
            {
                bool ear = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ip = idx[(i - 1 + idx.Count) % idx.Count];
                    int ic = idx[i];
                    int inext = idx[(i + 1) % idx.Count];
                    Vector2 a = poly[ip], b = poly[ic], c = poly[inext];
                    if (Cross(b - a, c - a) <= 0f) continue; // 凹顶点，跳过
                    bool contains = false;
                    for (int k = 0; k < idx.Count; k++)
                    {
                        if (k == i || k == (i - 1 + idx.Count) % idx.Count || k == (i + 1) % idx.Count) continue;
                        if (PointInTriangle(poly[idx[k]], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;
                    triangles.Add(ip); triangles.Add(ic); triangles.Add(inext);
                    idx.RemoveAt(i);
                    ear = true;
                    break;
                }
                if (!ear) break;
            }
            if (idx.Count == 3)
            {
                triangles.Add(idx[0]); triangles.Add(idx[1]); triangles.Add(idx[2]);
            }
            return triangles.ToArray();
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
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
