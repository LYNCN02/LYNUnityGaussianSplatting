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
    public static class LYNOOKWorldStudioService
    {
        public const string WorkspaceRoot = "Assets/LYNOOK/Worlds";
        /// <summary>批量录制下载资产的临时目录根（系统临时目录下的 lynook_batch）。</summary>
        public static string BatchTempRoot => Path.Combine(Path.GetTempPath(), "lynook_batch");

        public static LYNOOKWorldAuthoring Current => SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<LYNOOKWorldAuthoring>(true)).SingleOrDefault();

        [Serializable] public sealed class Pose
        {
            public string name;
            public Vector3 position, rotationEuler, scale;
            public static Pose From(Transform value) => new Pose { name = value.name, position = value.localPosition, rotationEuler = value.localEulerAngles, scale = value.localScale };
        }

        // ---- 场景队列 / 批量录制 ----

        /// <summary>
        /// Marble SPZ 语义元数据（数据库 metadata.splatSemantics，源自官方
        /// assets.splats.semantics_metadata）：把原始帧高斯换算成米制、地面对齐坐标。
        /// 顺序固定：① 中心与尺寸 × metricScaleFactor → ② 中心 y −= groundPlaneOffset
        /// → ③ OpenGL 系引擎绕 X 轴 180°（Unity 不做）。
        /// </summary>
        [Serializable] public sealed class SplatSemantics
        {
            public float metricScaleFactor;
            public float groundPlaneOffset;
        }

        /// <summary>数据库 scenes 表中一条待转换（待录制）场景。</summary>
        [Serializable] public sealed class SceneQueueItem
        {
            public int id;
            public string name, prompt, model, status, convertStatus, convertTaskId, roomType;
            public string operationId, providerSceneId, marbleUrl, thumbnailUrl;
            public string colliderMeshUrl, splatUrl, panoUrl, hdrPanoUrl;
            public string worldJsonUrl, previewVideoUrl;
            public string error, convertError;
            public string createdAt, convertQueuedAt;
            public string ownerEmail, ownerUsername, ownerName;
            // spzUrls 是 JSON 对象，JsonUtility 不支持字典；保留原始字符串供后续解析。
            public string spzUrls;
            // 旧场景未回填时可能为 null，导入时按 identity（scale=1、offset=0）处理。
            public SplatSemantics splatSemantics;
        }

        [Serializable] internal sealed class SceneQueueResult
        {
            public SceneQueueItem[] rows;
        }

        [Serializable] public sealed class SceneDownloadResult
        {
            public string splat, collider;
            public long splatBytes, colliderBytes;
        }

        [Serializable] public sealed class SceneUploadResult
        {
            public string worldJsonUrl, previewVideoUrl, rightVideoUrl, collisionUrl, previewUrl;
        }

        [Serializable] public sealed class SceneUpdateResult
        {
            public bool updated;
            public int sceneId;
            public string convertStatus;
        }

        [Serializable] internal sealed class Draft
        {
            public int schemaVersion = 1;
            public string worldId, roomType, displayName, gaussian, collision, gaussianSha256, collisionSha256;
            public bool alignmentConfirmed, spawnPlaced;
            public Pose coordinates, gaussianTransform, collisionTransform, cameraRig, spawn;
            public Pose[] points;
            public float floorHeight, agentRadius, agentHeight, pointSpacing, floorTolerance;
            public Vector2 walkCenter, walkSize;
            public List<Vector2> walkPolygon;
            public float walkAreaThickness;
            public int maximumPoints, recordingSeconds;
            public string deployTargetPath;
        }
    }
}
#endif
