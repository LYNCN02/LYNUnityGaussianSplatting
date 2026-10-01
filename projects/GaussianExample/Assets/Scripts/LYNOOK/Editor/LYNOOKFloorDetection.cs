#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKFloorDetection
    {
        struct FloorTri
        {
            public Vector2 a, b, c;
            public float worldY;
            public float area;
        }

        /// <summary>
        /// 扫描 GLB 碰撞网格，找出面积最大的近水平地面，自动设置
        /// <see cref="LYNOOKWorldAuthoring.floorHeight"/> 并把
        /// <see cref="LYNOOKWorldAuthoring.walkPolygon"/> 拟合为地面真实轮廓（任意简单多边形）。
        /// 同时保留 walkCenter/walkSize 作为多边形边界盒回退。
        /// </summary>
        public static void AutoDetectFloor(LYNOOKWorldAuthoring world)
        {
            if (world.coordinateRoot == null || world.collisionObject == null)
                throw new InvalidOperationException("缺少房间坐标或 GLB。");

            Vector3 up = world.coordinateRoot.up;
            float cosThreshold = Mathf.Cos(12f * Mathf.Deg2Rad); // 法线与上方向夹角 ≤12° 视为地面
            float heightTolerance = Mathf.Max(world.floorTolerance * 2f, 0.2f);

            // 收集所有近水平三角面
            var floorTris = new List<FloorTri>();
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts = mesh.vertices;
                int[] tris = mesh.triangles;
                Transform tr = filter.transform;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 wa = tr.TransformPoint(verts[tris[i]]);
                    Vector3 wb = tr.TransformPoint(verts[tris[i + 1]]);
                    Vector3 wc = tr.TransformPoint(verts[tris[i + 2]]);
                    Vector3 n = Vector3.Cross(wb - wa, wc - wa);
                    float area2 = n.magnitude;
                    if (area2 < 1e-8f) continue;
                    if (Vector3.Dot(n / area2, up) < cosThreshold) continue;
                    Vector3 la = world.coordinateRoot.InverseTransformPoint(wa);
                    Vector3 lb = world.coordinateRoot.InverseTransformPoint(wb);
                    Vector3 lc = world.coordinateRoot.InverseTransformPoint(wc);
                    floorTris.Add(new FloorTri
                    {
                        a = new Vector2(la.x, la.z),
                        b = new Vector2(lb.x, lb.z),
                        c = new Vector2(lc.x, lc.z),
                        worldY = (wa.y + wb.y + wc.y) / 3f,
                        area = area2 * 0.5f
                    });
                }
            }

            if (floorTris.Count == 0)
                throw new InvalidOperationException("GLB 中没有检测到接近水平的地面三角面。");

            // 按高度做面积加权直方图，选出占主导的地面高度层
            float minY = floorTris[0].worldY, maxY = minY;
            foreach (var t in floorTris) { if (t.worldY < minY) minY = t.worldY; if (t.worldY > maxY) maxY = t.worldY; }
            int binCount = Mathf.Max(1, Mathf.CeilToInt((maxY - minY) / heightTolerance) + 1);
            float[] areaByBin = new float[binCount];
            float[] sumYByBin = new float[binCount];
            float[] sumAreaByBin = new float[binCount];
            foreach (var t in floorTris)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt((t.worldY - minY) / heightTolerance), 0, binCount - 1);
                areaByBin[bin] += t.area;
                sumYByBin[bin] += t.worldY * t.area;
                sumAreaByBin[bin] += t.area;
            }
            int bestBin = 0;
            for (int i = 1; i < binCount; i++) if (areaByBin[i] > areaByBin[bestBin]) bestBin = i;
            float floorWorldY = sumYByBin[bestBin] / sumAreaByBin[bestBin];

            // 只保留主导高度层的地面三角面
            var mainFloor = floorTris.Where(t => Mathf.Abs(t.worldY - floorWorldY) <= heightTolerance).ToList();
            if (mainFloor.Count == 0) throw new InvalidOperationException("地面范围计算失败。");

            // 面积加权平均得到房间局部坐标下的地面高度
            float sumLocalY = 0f, sumArea = 0f;
            foreach (var t in mainFloor)
            {
                float ly = world.coordinateRoot.InverseTransformPoint(new Vector3(0, t.worldY, 0)).y;
                sumLocalY += ly * t.area;
                sumArea += t.area;
            }
            float floorLocalY = sumLocalY / sumArea;

            // 提取地面轮廓多边形
            var polygon = ExtractFloorPolygon(mainFloor, world.agentRadius);
            if (polygon.Count < 3) throw new InvalidOperationException("地面轮廓提取失败。");

            world.floorHeight = floorLocalY;
            world.walkPolygon = polygon;
            // 同步更新矩形边界盒作为回退表示
            world.WalkBounds(out var center, out var size);
            world.walkCenter = center;
            world.walkSize = size;
        }

        /// <summary>
        /// 从用户点击点出发提取地面：用点击 Y 作为地面高度（跳过直方图），
        /// 在房间局部高度容差内栅格化，以点击 X/Z 选取连通域及闭合轮廓。
        /// </summary>
        public static void AutoDetectFloorFromPoint(LYNOOKWorldAuthoring world, Vector3 hitWorldPoint)
        {
            if (world.coordinateRoot == null || world.collisionObject == null)
                throw new InvalidOperationException("缺少房间坐标或 GLB。");

            Vector3 up = world.coordinateRoot.up;
            // 放宽法线阈值到 25°，吸收颗粒侧面
            float cosThreshold = Mathf.Cos(25f * Mathf.Deg2Rad);
            // Use the configured tolerance; do not merge furniture half a metre above the click.
            float heightTolerance = Mathf.Max(world.floorTolerance, 0.02f);

            Vector3 seedLocal = world.coordinateRoot.InverseTransformPoint(hitWorldPoint);
            float floorLocalY = seedLocal.y;

            // 收集点击高度附近的近水平三角面
            var mainFloor = new List<FloorTri>();
            foreach (var filter in world.collisionObject.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Vector3[] verts = mesh.vertices;
                int[] tris = mesh.triangles;
                Transform tr = filter.transform;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 wa = tr.TransformPoint(verts[tris[i]]);
                    Vector3 wb = tr.TransformPoint(verts[tris[i + 1]]);
                    Vector3 wc = tr.TransformPoint(verts[tris[i + 2]]);
                    Vector3 n = Vector3.Cross(wb - wa, wc - wa);
                    float area2 = n.magnitude;
                    if (area2 < 1e-8f) continue;
                    if (Mathf.Abs(Vector3.Dot(n / area2, up)) < cosThreshold) continue;
                    Vector3 la = world.coordinateRoot.InverseTransformPoint(wa);
                    Vector3 lb = world.coordinateRoot.InverseTransformPoint(wb);
                    Vector3 lc = world.coordinateRoot.InverseTransformPoint(wc);
                    if (Mathf.Max(Mathf.Abs(la.y - floorLocalY), Mathf.Abs(lb.y - floorLocalY), Mathf.Abs(lc.y - floorLocalY)) > heightTolerance) continue;
                    mainFloor.Add(new FloorTri
                    {
                        a = new Vector2(la.x, la.z),
                        b = new Vector2(lb.x, lb.z),
                        c = new Vector2(lc.x, lc.z),
                        worldY = (wa.y + wb.y + wc.y) / 3f,
                        area = area2 * 0.5f
                    });
                }
            }

            if (mainFloor.Count == 0)
                throw new InvalidOperationException("点击位置附近没有检测到地面三角面。");

            // The click's X/Z selects the connected component, never the largest island.
            // This is the selected floor footprint; agent clearance is a separate operation.
            var polygon = ExtractFloorPolygon(mainFloor, 0, new Vector2(seedLocal.x, seedLocal.z));
            if (polygon.Count < 3)
                throw new InvalidOperationException("点击点所在的地面区域无法形成闭合轮廓；原范围保持不变。");

            world.floorHeight = floorLocalY;
            world.walkPolygon = polygon;
            world.WalkBounds(out var center2, out var size2);
            world.walkCenter = center2;
            world.walkSize = size2;
        }

        /// <summary>
        /// 从地面三角面集合提取 2D 轮廓多边形（房间局部 XZ）：
        /// 栅格化 → 最大连通域 → marching squares 等高线 → Douglas-Peucker 简化。
        /// </summary>
        static List<Vector2> ExtractFloorPolygon(List<FloorTri> tris, float margin, Vector2? seed = null)
        {
            // 1. 计算 XZ 包围盒
            float minX = tris[0].a.x, maxX = minX, minZ = tris[0].a.y, maxZ = minZ;
            void Enc(Vector2 p)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }
            foreach (var t in tris) { Enc(t.a); Enc(t.b); Enc(t.c); }

            float cell = 0.08f; // 栅格分辨率，平衡精度与性能
            // Empty border closes contours that touch the source mesh bounds.
            minX -= cell * 2; minZ -= cell * 2;
            maxX += cell * 2; maxZ += cell * 2;
            int cols = Mathf.CeilToInt((maxX - minX) / cell) + 1;
            int rows = Mathf.CeilToInt((maxZ - minZ) / cell) + 1;
            if ((long)cols * rows > 4000000)
                throw new InvalidOperationException("地面范围过大，请检查 GLB 尺度。");

            // 2. 栅格化：每个网格中心若落在任一地面三角面内则占用
            var occ = new bool[cols, rows];
            foreach (var t in tris)
            {
                float tx0 = Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x));
                float tx1 = Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x));
                float tz0 = Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y));
                float tz1 = Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y));
                int cx0 = Mathf.Clamp(Mathf.FloorToInt((tx0 - minX) / cell), 0, cols - 1);
                int cx1 = Mathf.Clamp(Mathf.FloorToInt((tx1 - minX) / cell), 0, cols - 1);
                int cz0 = Mathf.Clamp(Mathf.FloorToInt((tz0 - minZ) / cell), 0, rows - 1);
                int cz1 = Mathf.Clamp(Mathf.FloorToInt((tz1 - minZ) / cell), 0, rows - 1);
                for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    if (occ[cx, cz]) continue;
                    Vector2 p = new Vector2(minX + (cx + 0.5f) * cell, minZ + (cz + 0.5f) * cell);
                    if (PointInTriangle(p, t.a, t.b, t.c)) occ[cx, cz] = true;
                }
            }

            // 3. 找最大连通域（4 邻接 flood fill），并剔除孤立小面积
            int[] labels = new int[cols * rows];
            int labelCount = 0;
            var regionSize = new List<int>();
            for (int cx = 0; cx < cols; cx++)
            for (int cz = 0; cz < rows; cz++)
            {
                if (!occ[cx, cz] || labels[cz * cols + cx] != 0) continue;
                labelCount++;
                int size = 0;
                var stack = new Stack<(int x, int z)>();
                stack.Push((cx, cz));
                labels[cz * cols + cx] = labelCount;
                while (stack.Count > 0)
                {
                    var (x, z) = stack.Pop();
                    size++;
                    if (x > 0 && occ[x - 1, z] && labels[z * cols + x - 1] == 0) { labels[z * cols + x - 1] = labelCount; stack.Push((x - 1, z)); }
                    if (x < cols - 1 && occ[x + 1, z] && labels[z * cols + x + 1] == 0) { labels[z * cols + x + 1] = labelCount; stack.Push((x + 1, z)); }
                    if (z > 0 && occ[x, z - 1] && labels[(z - 1) * cols + x] == 0) { labels[(z - 1) * cols + x] = labelCount; stack.Push((x, z - 1)); }
                    if (z < rows - 1 && occ[x, z + 1] && labels[(z + 1) * cols + x] == 0) { labels[(z + 1) * cols + x] = labelCount; stack.Push((x, z + 1)); }
                }
                regionSize.Add(size);
            }
            if (labelCount == 0) return new List<Vector2>();
            int bestLabel = 1, bestSize = regionSize[0];
            for (int i = 1; i < regionSize.Count; i++)
                if (regionSize[i] > bestSize) { bestSize = regionSize[i]; bestLabel = i + 1; }

            if (seed.HasValue)
            {
                int sx = Mathf.FloorToInt((seed.Value.x - minX) / cell);
                int sz = Mathf.FloorToInt((seed.Value.y - minZ) / cell);
                if (sx < 0 || sx >= cols || sz < 0 || sz >= rows || labels[sz * cols + sx] == 0)
                    throw new InvalidOperationException("点击点不在可提取地面栅格内，请稍向地面内部点击。");
                bestLabel = labels[sz * cols + sx];
            }

            // 4. marching squares 提取最大连通域的外轮廓
            var segments = new List<(Vector2 a, Vector2 b)>();
            for (int cx = 0; cx < cols - 1; cx++)
            for (int cz = 0; cz < rows - 1; cz++)
            {
                int c00 = (labels[cz * cols + cx] == bestLabel) ? 1 : 0;
                int c10 = (labels[cz * cols + cx + 1] == bestLabel) ? 1 : 0;
                int c01 = (labels[(cz + 1) * cols + cx] == bestLabel) ? 1 : 0;
                int c11 = (labels[(cz + 1) * cols + cx + 1] == bestLabel) ? 1 : 0;
                int code = c00 | (c10 << 1) | (c01 << 2) | (c11 << 3);
                Vector2 bl = new Vector2(minX + (cx + 0.5f) * cell, minZ + (cz + 0.5f) * cell);
                Vector2 br = bl + new Vector2(cell, 0);
                Vector2 tl = bl + new Vector2(0, cell);
                Vector2 tr = bl + new Vector2(cell, cell);
                Vector2 mb = (bl + br) * 0.5f, ml = (bl + tl) * 0.5f, mr = (br + tr) * 0.5f, mt = (tl + tr) * 0.5f;
                switch (code)
                {
                    case 0: case 15: break;
                    case 1: segments.Add((mb, ml)); break;
                    case 2: segments.Add((mr, mb)); break;
                    case 3: segments.Add((mr, ml)); break;
                    case 4: segments.Add((ml, mt)); break;
                    case 5: segments.Add((mb, mt)); break; // saddle: pick one diagonal
                    case 6: segments.Add((ml, mt)); segments.Add((mr, mb)); break;
                    case 7: segments.Add((mt, mr)); break;
                    case 8: segments.Add((mt, mr)); break;
                    case 9: segments.Add((mb, ml)); segments.Add((mt, mr)); break;
                    case 10: segments.Add((mb, mt)); break; // saddle
                    case 11: segments.Add((mt, ml)); break;
                    case 12: segments.Add((mr, ml)); break;
                    case 13: segments.Add((mr, mb)); break;
                    case 14: segments.Add((mb, ml)); break;
                }
            }

            // 5. 把线段串成闭合轮廓
            var contour = ChainSegments(segments, seed);
            if (contour.Count < 3) return new List<Vector2>();

            // 6. Douglas-Peucker 简化
            float simplifyTol = cell * 1.5f;
            var simplified = DouglasPeucker(contour, simplifyTol);
            if (seed.HasValue && !LYNOOKWorldAuthoring.PointInPolygon(seed.Value, simplified))
                simplified = contour;

            // 7. 内缩 margin，避免角色贴墙（把多边形整体向质心收缩一小步的近似：
            //    这里直接对每个顶点沿角平分线内移；若退化则回退原轮廓）
            var inset = InsetPolygon(simplified, margin);
            return inset.Count >= 3 ? inset : simplified;
        }

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // 把无序列表线段按端点匹配串成一个闭合折线
        static List<Vector2> ChainSegments(List<(Vector2 a, Vector2 b)> segs, Vector2? seed = null)
        {
            var remaining = new LinkedList<(Vector2 a, Vector2 b)>(segs);
            var best = new List<Vector2>();
            float bestArea = 0;
            const float eps2 = 1e-8f;
            while (remaining.Count > 0)
            {
                var first = remaining.First.Value;
                remaining.RemoveFirst();
                var loop = new List<Vector2> { first.a };
                Vector2 head = first.b;
                while ((head - first.a).sqrMagnitude > eps2)
                {
                    loop.Add(head);
                    var node = remaining.First;
                    while (node != null && (node.Value.a - head).sqrMagnitude > eps2 &&
                           (node.Value.b - head).sqrMagnitude > eps2) node = node.Next;
                    if (node == null) break;
                    head = (node.Value.a - head).sqrMagnitude <= eps2 ? node.Value.b : node.Value.a;
                    remaining.Remove(node);
                }
                if ((head - first.a).sqrMagnitude > eps2 || loop.Count < 3) continue;
                if (seed.HasValue && !LYNOOKWorldAuthoring.PointInPolygon(seed.Value, loop)) continue;
                float area = 0;
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2 p = loop[i], q = loop[(i + 1) % loop.Count];
                    area += p.x * q.y - q.x * p.y;
                }
                if (Mathf.Abs(area) > bestArea) { best = loop; bestArea = Mathf.Abs(area); }
            }
            return best;
        }

        static List<Vector2> DouglasPeucker(List<Vector2> points, float epsilon)
        {
            if (points.Count < 3) return new List<Vector2>(points);
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var stack = new Stack<(int lo, int hi)>();
            stack.Push((0, points.Count - 1));
            while (stack.Count > 0)
            {
                var (lo, hi) = stack.Pop();
                if (hi - lo < 2) continue;
                float maxDist = 0; int index = -1;
                for (int i = lo + 1; i < hi; i++)
                {
                    float d = PerpDistance(points[i], points[lo], points[hi]);
                    if (d > maxDist) { maxDist = d; index = i; }
                }
                if (index >= 0 && maxDist > epsilon)
                {
                    keep[index] = true;
                    stack.Push((lo, index));
                    stack.Push((index, hi));
                }
            }
            var result = new List<Vector2>();
            for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
            return result;
        }

        static float PerpDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return (p - a).magnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }

        // 把多边形每个顶点沿内角平分线向内移动 distance，生成内缩多边形。
        // 若某顶点内缩后导致边自交/退化，保留原顶点。
        static List<Vector2> InsetPolygon(List<Vector2> poly, float distance)
        {
            int n = poly.Count;
            if (n < 3 || distance <= 0f) return new List<Vector2>(poly);
            var result = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = poly[(i - 1 + n) % n];
                Vector2 cur = poly[i];
                Vector2 next = poly[(i + 1) % n];
                Vector2 e1 = (cur - prev).normalized;
                Vector2 e2 = (next - cur).normalized;
                Vector2 bisect = (e1 - e2).normalized; // 指向多边形内部（逆时针时）
                // 根据面积符号判断朝向，修正内缩方向
                double area = 0;
                for (int k = 0; k < n; k++) area += poly[k].x * poly[(k + 1) % n].y - poly[(k + 1) % n].x * poly[k].y;
                if (area < 0) bisect = -bisect;
                Vector2 moved = cur + bisect * distance;
                result.Add(moved);
            }
            // 简单自交检测：若内缩后边数级别的自交则退回原多边形
            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = result[i], a2 = result[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (j == (i + 1) % n || i == (j + 1) % n) continue;
                    Vector2 b1 = result[j], b2 = result[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return new List<Vector2>(poly);
                }
            }
            return result;
        }

        static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
            float d1 = Cross(p4 - p3, p1 - p3);
            float d2 = Cross(p4 - p3, p2 - p3);
            float d3 = Cross(p2 - p1, p3 - p1);
            float d4 = Cross(p2 - p1, p4 - p1);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

    }
}
#endif
