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
    public static class LYNOOKWorldInteraction
    {
        public static Bounds CollisionBounds(LYNOOKWorldAuthoring world)
        {
            var renderers = world.collisionObject.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("GLB 没有可显示的网格。");
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        public static void Frame(LYNOOKWorldAuthoring world)
        {
            var view = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            view.Frame(LYNOOKWorldInteraction.CollisionBounds(world), false);
            view.Show();
            view.Focus();
            SceneView.RepaintAll();
        }

        public static void SetCollisionVisible(LYNOOKWorldAuthoring world, bool visible)
        {
            world.showCollision = visible;
            foreach (var renderer in world.collisionObject.GetComponentsInChildren<Renderer>(true)) renderer.enabled = visible;
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            SceneView.RepaintAll();
        }

        public static bool Raycast(LYNOOKWorldAuthoring world, Ray ray, out RaycastHit nearest, float distance = 10000)
        {
            nearest = default;
            if (world == null || world.collisionObject == null) return false;
            bool found = false;
            Physics.SyncTransforms();
            // Imported room meshes can face outward. Query both sides when picking
            // from inside, without changing the project's global physics setting.
            bool previousBackfaces = Physics.queriesHitBackfaces;
            try
            {
                Physics.queriesHitBackfaces = true;
                foreach (var collider in world.collisionObject.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled || !collider.Raycast(ray, out var hit, distance)) continue;
                    distance = hit.distance;
                    if (Vector3.Dot(hit.normal, ray.direction) > 0) hit.normal = -hit.normal;
                    nearest = hit;
                    found = true;
                }
            }
            finally { Physics.queriesHitBackfaces = previousBackfaces; }
            return found;
        }

        // Do not look through walls or accept a wall as a fallback floor.
        public static bool RaycastPreferFloor(LYNOOKWorldAuthoring world, Ray ray, out RaycastHit best, float distance = 10000)
        {
            return LYNOOKWorldInteraction.Raycast(world, ray, out best, distance) &&
                Vector3.Dot(best.normal, world.coordinateRoot.up) >= Mathf.Cos(25f * Mathf.Deg2Rad);
        }

        public static bool ValidStandingPoint(LYNOOKWorldAuthoring world, Vector3 position, out string reason)
        {
            if (world.coordinateRoot == null || world.collisionObject == null) { reason = "缺少房间坐标或 GLB。"; return false; }
            Vector3 local = world.coordinateRoot.InverseTransformPoint(position);
            // 多边形模式下轮廓已内缩 agentRadius，直接判断点在范围内即可；
            // 矩形模式下仍需把边缘 agentRadius 排除。
            bool inside = world.HasWalkPolygon
                ? world.IsInsideWalkArea(new Vector2(local.x, local.z))
                : (Mathf.Abs(local.x - world.walkCenter.x) <= world.walkSize.x * 0.5f - world.agentRadius &&
                   Mathf.Abs(local.z - world.walkCenter.y) <= world.walkSize.y * 0.5f - world.agentRadius);
            if (!inside)
            { reason = "位置在可走范围外，或离边缘太近。"; return false; }
            if (Mathf.Abs(local.y - world.floorHeight) > world.floorTolerance)
            { reason = "位置不在已确认的地面高度附近。"; return false; }
            Vector3 up = world.coordinateRoot.up;
            float scale = world.coordinateRoot.lossyScale.x;
            float probe = (world.floorTolerance + 0.05f) * scale;
            if (!LYNOOKWorldInteraction.Raycast(world, new Ray(position + up * probe, -up), out var hit, probe * 2) ||
                Vector3.Dot(hit.normal, up) < 0.9f || Vector3.Distance(hit.point, position) > world.floorTolerance * scale)
            { reason = "GLB 中没有找到平坦的支撑地面。"; return false; }
            LYNOOKWorldInteraction.Capsule(world, position, out var bottom, out var top, out float radius);
            if (Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore)
                .Any(c => c.transform.IsChildOf(world.collisionObject.transform)))
            { reason = "角色站立空间与 GLB 障碍物重叠。"; return false; }
            reason = "可站立";
            return true;
        }

        static void Capsule(LYNOOKWorldAuthoring world, Vector3 foot, out Vector3 bottom, out Vector3 top, out float radius)
        {
            float scale = world.coordinateRoot.lossyScale.x;
            radius = world.agentRadius * scale;
            bottom = foot + world.coordinateRoot.up * (radius + 0.025f * scale);
            top = foot + world.coordinateRoot.up * (world.agentHeight * scale - radius);
        }

        public static bool DirectPathClear(LYNOOKWorldAuthoring world, Vector3 from, Vector3 to)
        {
            LYNOOKWorldInteraction.Capsule(world, from, out var bottom, out var top, out float radius);
            Vector3 delta = to - from;
            if (delta.magnitude > 0.001f && Physics.CapsuleCastAll(bottom, top, radius, delta.normalized,
                    delta.magnitude, ~0, QueryTriggerInteraction.Ignore)
                .Any(h => h.collider.transform.IsChildOf(world.collisionObject.transform))) return false;
            int steps = Mathf.CeilToInt(delta.magnitude / Mathf.Max(0.05f, radius));
            for (int i = 0; i <= steps; i++)
                if (!LYNOOKWorldInteraction.ValidStandingPoint(world, Vector3.Lerp(from, to, steps == 0 ? 0 : (float)i / steps), out _)) return false;
            return true;
        }

        public static void PlaceSpawn(LYNOOKWorldAuthoring world, RaycastHit hit)
        {
            if (!LYNOOKWorldInteraction.ValidStandingPoint(world, hit.point, out string reason)) throw new InvalidOperationException(reason);
            Undo.RecordObjects(new Object[] { world, world.avatarSpawn }, "Set avatar spawn");
            world.avatarSpawn.position = hit.point;
            world.spawnPlaced = true;
            LYNOOKWorldInteraction.Changed(world);
        }

        public static int GeneratePoints(LYNOOKWorldAuthoring world)
        {
            LYNOOKWorldPersistence.ValidateSettings(world);
            if (!world.spawnPlaced) throw new InvalidOperationException("请先点选出生地。");
            if (!LYNOOKWorldInteraction.ValidStandingPoint(world, world.avatarSpawn.position, out string reason)) throw new InvalidOperationException("出生地：" + reason);
            float scale = world.coordinateRoot.lossyScale.x;
            float minDist = world.pointSpacing * scale;
            world.WalkBounds(out var center, out var size);
            float halfX = size.x * 0.5f, halfZ = size.y * 0.5f;
            float minX = center.x - halfX, maxX = center.x + halfX;
            float minZ = center.y - halfZ, maxZ = center.y + halfZ;

            // 1. 在多边形边界盒内随机撒点，过滤出可站立且从出生地可达的候选点。
            //    尝试次数按面积与间距估算，保证大区域也能采到足够候选。
            int attempts = Mathf.Clamp(Mathf.CeilToInt(size.x * size.y / Mathf.Max(0.04f, world.pointSpacing * world.pointSpacing)) * 4, 200, 4000);
            var candidates = new List<Vector3>(attempts);
            var rng = new System.Random(Guid.NewGuid().GetHashCode());
            for (int i = 0; i < attempts; i++)
            {
                Vector2 localXZ = new Vector2(
                    (float)(minX + rng.NextDouble() * (maxX - minX)),
                    (float)(minZ + rng.NextDouble() * (maxZ - minZ)));
                if (!world.IsInsideWalkArea(localXZ)) continue;
                Vector3 local = new Vector3(localXZ.x, world.floorHeight + world.floorTolerance + 0.05f, localXZ.y);
                if (!LYNOOKWorldInteraction.Raycast(world, new Ray(world.coordinateRoot.TransformPoint(local), -world.coordinateRoot.up), out var hit,
                        (world.floorTolerance * 2 + 0.1f) * scale)) continue;
                if (Vector3.Distance(hit.point, world.avatarSpawn.position) < minDist) continue;
                if (!LYNOOKWorldInteraction.ValidStandingPoint(world, hit.point, out _)) continue;
                if (!LYNOOKWorldInteraction.DirectPathClear(world, world.avatarSpawn.position, hit.point)) continue;
                candidates.Add(hit.point);
            }

            // 2. 打乱候选点顺序，按最小间距贪心选择，保证位置随机且分布均匀。
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            var accepted = new List<Vector3>();
            foreach (var p in candidates)
            {
                bool tooClose = false;
                for (int k = 0; k < accepted.Count; k++)
                {
                    if (Vector3.Distance(p, accepted[k]) < minDist) { tooClose = true; break; }
                }
                if (tooClose) continue;
                accepted.Add(p);
                if (accepted.Count >= world.maximumPoints) break;
            }
            if (accepted.Count == 0) throw new InvalidOperationException("没有找到安全点位。请检查地面高度、活动范围和 GLB 对齐；原有点位已保留。");

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            for (int i = world.activityPoints.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(world.activityPoints.GetChild(i).gameObject);
            for (int i = 0; i < accepted.Count; i++)
            {
                Transform point = LYNOOKWorldSceneBuilder.Child(world.activityPoints, "stand_" + (i + 1).ToString("D2"));
                point.position = accepted[i];
                point.rotation = world.avatarSpawn.rotation;
                var meta = point.gameObject.AddComponent<LYNOOKActivityPoint>();
                meta.type = LynookActivityTypes.Stand;
                Undo.RegisterCreatedObjectUndo(point.gameObject, "Generate activity points");
            }
            Undo.CollapseUndoOperations(group);
            LYNOOKWorldInteraction.Changed(world);
            return accepted.Count;
        }

        public static void Changed(LYNOOKWorldAuthoring world)
        {
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            SceneView.RepaintAll();
        }

        /// <summary>自动把出生点放在可走范围中心。</summary>
        public static void AutoPlaceSpawn(LYNOOKWorldAuthoring world)
        {
            if (world.coordinateRoot == null) throw new InvalidOperationException("缺少房间坐标。");
            world.WalkBounds(out var center, out _);
            Vector3 localSpawn = new Vector3(center.x, world.floorHeight, center.y);
            Vector3 worldSpawn = world.coordinateRoot.TransformPoint(localSpawn);
            if (!LYNOOKWorldInteraction.ValidStandingPoint(world, worldSpawn, out string reason))
                throw new InvalidOperationException("自动出生点无效：" + reason);
            Undo.RecordObjects(new Object[] { world, world.avatarSpawn }, "Auto place avatar spawn");
            world.avatarSpawn.position = worldSpawn;
            world.spawnPlaced = true;
            LYNOOKWorldInteraction.Changed(world);
        }

    }
}
#endif
