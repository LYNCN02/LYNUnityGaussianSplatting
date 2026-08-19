using System;
using UnityEngine;

namespace Lynook.DualScreen
{
    public enum LYNOOKGameViewPreviewLayout
    {
        SeparateDisplays,
        CombinedOnDisplay1
    }

    [Serializable]
    public readonly struct LYNOOKOffAxisFrustum
    {
        public readonly float left;
        public readonly float right;
        public readonly float bottom;
        public readonly float top;
        public readonly float near;
        public readonly float far;

        public LYNOOKOffAxisFrustum(float left, float right, float bottom, float top, float near, float far)
        {
            this.left = left;
            this.right = right;
            this.bottom = bottom;
            this.top = top;
            this.near = near;
            this.far = far;
        }

        public Matrix4x4 Matrix => Matrix4x4.Frustum(left, right, bottom, top, near, far);

        public override string ToString()
        {
            return $"L={left:F7}, R={right:F7}, B={bottom:F7}, T={top:F7}, N={near:F4}, F={far:F1}";
        }
    }

    public readonly struct LYNOOKScreenGeometry
    {
        public readonly Vector3 center;
        public readonly Vector3 right;
        public readonly Vector3 up;
        public readonly Vector3 forward;
        public readonly float widthMeters;
        public readonly float heightMeters;

        public LYNOOKScreenGeometry(
            Vector3 center,
            Vector3 right,
            Vector3 up,
            Vector3 forward,
            float widthMeters,
            float heightMeters)
        {
            this.center = center;
            this.right = right.normalized;
            this.up = up.normalized;
            this.forward = forward.normalized;
            this.widthMeters = widthMeters;
            this.heightMeters = heightMeters;
        }

        public Vector3 BottomLeft => center - right * (widthMeters * 0.5f) - up * (heightMeters * 0.5f);
        public Vector3 BottomRight => center + right * (widthMeters * 0.5f) - up * (heightMeters * 0.5f);
        public Vector3 TopLeft => center - right * (widthMeters * 0.5f) + up * (heightMeters * 0.5f);
        public Vector3 TopRight => center + right * (widthMeters * 0.5f) + up * (heightMeters * 0.5f);
        public Quaternion CameraRotation => Quaternion.LookRotation(forward, up);
    }

    /// <summary>
    /// Calibrates two physical display planes against one viewer eye. The cameras never
    /// leave that eye; rotations follow the display normals and lens offsets are expressed
    /// only by asymmetric Matrix4x4.Frustum projection matrices.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class LYNOOKDualCameraRig : MonoBehaviour
    {
        const float MillimetersToMeters = 0.001f;
        const float MinimumDimensionMm = 1f;
        const float PositionToleranceMeters = 0.000001f;

        public const int MainWidth = 1280;
        public const int MainHeight = 800;
        public const int SideWidth = 720;
        public const int SideHeight = 1280;

        // Legacy values identify the baseline that this component replaces. Neither is
        // used to construct the final projection matrices.
        public const float VerticalFieldOfView = 50f;
        public const float SideYaw = 51.4f;

        [Header("Capture outputs")]
        [SerializeField] Transform captureRig;
        [SerializeField] Camera mainCaptureCamera;
        [SerializeField] Camera sideCaptureCamera;
        [SerializeField] RenderTexture mainCaptureTexture;
        [SerializeField] RenderTexture sideCaptureTexture;

        [Header("Physical visible areas (millimeters)")]
        [Min(MinimumDimensionMm)] [SerializeField] float mainScreenWidthMm = 172.22f;
        [Min(MinimumDimensionMm)] [SerializeField] float mainScreenHeightMm = 107.64f;
        [Min(MinimumDimensionMm)] [SerializeField] float sideScreenWidthMm = 62.1f;
        [Min(MinimumDimensionMm)] [SerializeField] float sideScreenHeightMm = 110.4f;

        [Header("Shared target eye and screen placement (CaptureRig local millimeters)")]
        [Tooltip("The single observation eye used by both cameras. This must come from the real viewing setup.")]
        [SerializeField] Vector3 targetEyeLocalMm = Vector3.zero;
        [Tooltip("Main visible-area center. The temporary Z preserves the previous 50 degree framing; it is not a measured eye distance.")]
        [SerializeField] Vector3 mainScreenCenterLocalMm = new Vector3(0f, 0f, 115.42f);
        [Tooltip("Main display orientation. Local +Z points through the display into the virtual scene.")]
        [SerializeField] Vector3 mainScreenEulerLocalDegrees = Vector3.zero;
        [Tooltip("When enabled, Side Screen Center is recomputed from the main top-right corner, screen angle and seam gap.")]
        [SerializeField] bool deriveSidePositionFromHinge = true;
        [Tooltip("Editable explicit side visible-area center. Disable Derive Side Position From Hinge to use it.")]
        [SerializeField] Vector3 sideScreenCenterLocalMm = new Vector3(105.47f, -1.38f, 91.13f);
        [Tooltip("Fine orientation correction applied after the measured hinge angle.")]
        [SerializeField] Vector3 sideScreenEulerTrimDegrees = Vector3.zero;

        [Header("Hinge calibration")]
        [Tooltip("Measured angle between screen normals. 51.4 is only the previous camera-yaw baseline until hardware measurement replaces it.")]
        [Range(1f, 179f)] [SerializeField] float screenAngleDegrees = 51.4f;
        [Tooltip("Distance from main visible right edge to side visible left edge, measured along the side plane.")]
        [Min(0f)] [SerializeField] float seamGapMm;
        [Tooltip("Enable only after eye, angle, gap and both screen poses have been measured on actual hardware.")]
        [SerializeField] bool calibrationValuesConfirmed;

        [Header("Clipping")]
        [Min(0.001f)] [SerializeField] float nearClipMeters = 0.03f;
        [Min(0.02f)] [SerializeField] float farClipMeters = 1000f;

        [Header("Game View preview")]
        [SerializeField] bool showGameViewPreview = true;
        [SerializeField] LYNOOKGameViewPreviewLayout gameViewPreviewLayout = LYNOOKGameViewPreviewLayout.SeparateDisplays;
        [SerializeField] bool showPreviewLabels = true;
        [SerializeField] Color previewBackground = Color.black;

        [Header("Diagnostics")]
        [SerializeField] bool drawCalibrationGizmos = true;

        [NonSerialized] GameObject mainPreviewCameraObject;
        [NonSerialized] GameObject sidePreviewCameraObject;
        [NonSerialized] Camera mainPreviewCamera;
        [NonSerialized] Camera sidePreviewCamera;
        [NonSerialized] LYNOOKGameViewPreviewLayout activePreviewLayout;
        bool applyingConfiguration;
        bool mainProjectionValid;
        bool sideProjectionValid;
        LYNOOKOffAxisFrustum mainFrustum;
        LYNOOKOffAxisFrustum sideFrustum;

        public Transform CaptureRig => captureRig;
        public Camera MainCaptureCamera => mainCaptureCamera;
        public Camera SideCaptureCamera => sideCaptureCamera;
        public RenderTexture MainCaptureTexture => mainCaptureTexture;
        public RenderTexture SideCaptureTexture => sideCaptureTexture;
        public Vector3 SharedEyeWorldPosition => captureRig != null
            ? captureRig.TransformPoint(targetEyeLocalMm * MillimetersToMeters)
            : transform.TransformPoint(targetEyeLocalMm * MillimetersToMeters);
        public float MainScreenWidthMm => mainScreenWidthMm;
        public float MainScreenHeightMm => mainScreenHeightMm;
        public float SideScreenWidthMm => sideScreenWidthMm;
        public float SideScreenHeightMm => sideScreenHeightMm;
        public float ScreenAngleDegrees => screenAngleDegrees;
        public float SeamGapMm => seamGapMm;
        public bool CalibrationValuesConfirmed => calibrationValuesConfirmed;
        public bool ProjectionsValid => mainProjectionValid && sideProjectionValid;
        public LYNOOKOffAxisFrustum MainFrustum => mainFrustum;
        public LYNOOKOffAxisFrustum SideFrustum => sideFrustum;

        public void SetReferences(
            Transform rig,
            Camera mainCamera,
            Camera sideCamera,
            RenderTexture mainTexture,
            RenderTexture sideTexture)
        {
            captureRig = rig;
            mainCaptureCamera = mainCamera;
            sideCaptureCamera = sideCamera;
            mainCaptureTexture = mainTexture;
            sideCaptureTexture = sideTexture;
            ApplyConfiguration();
        }

        void Awake() => ApplyConfiguration();

        void OnEnable()
        {
            ApplyConfiguration();
            EnsurePreviewCamera();
        }

        void OnDisable() => DestroyPreviewCamera();

        void LateUpdate()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                AbsorbMainCameraEditIntoCaptureRig();
#endif
            ApplyConfiguration();
            if (showGameViewPreview)
                EnsurePreviewCamera();
            else
                DestroyPreviewCamera();
        }

        void OnValidate()
        {
            mainScreenWidthMm = Mathf.Max(MinimumDimensionMm, mainScreenWidthMm);
            mainScreenHeightMm = Mathf.Max(MinimumDimensionMm, mainScreenHeightMm);
            sideScreenWidthMm = Mathf.Max(MinimumDimensionMm, sideScreenWidthMm);
            sideScreenHeightMm = Mathf.Max(MinimumDimensionMm, sideScreenHeightMm);
            seamGapMm = Mathf.Max(0f, seamGapMm);
            nearClipMeters = Mathf.Max(0.001f, nearClipMeters);
            farClipMeters = Mathf.Max(nearClipMeters + 0.01f, farClipMeters);
            ApplyConfiguration();
        }

        [ContextMenu("Apply LYNOOK off-axis calibration")]
        public void ApplyConfiguration()
        {
            if (applyingConfiguration || captureRig == null || mainCaptureCamera == null || sideCaptureCamera == null)
                return;

            applyingConfiguration = true;
            try
            {
                if (mainCaptureCamera.transform.parent != captureRig)
                    mainCaptureCamera.transform.SetParent(captureRig, false);
                if (sideCaptureCamera.transform.parent != captureRig)
                    sideCaptureCamera.transform.SetParent(captureRig, false);

                ConfigureMainCameraCommonSettings();
                sideCaptureCamera.CopyFrom(mainCaptureCamera);
                GetScreenGeometries(out var mainScreen, out var sideScreen);
                Vector3 eye = SharedEyeWorldPosition;
                mainProjectionValid = ConfigureOffAxisCamera(
                    mainCaptureCamera, mainCaptureTexture, eye, mainScreen, out mainFrustum);
                sideProjectionValid = ConfigureOffAxisCamera(
                    sideCaptureCamera, sideCaptureTexture, eye, sideScreen, out sideFrustum);
                RemoveExtraAudioListeners();
            }
            finally
            {
                applyingConfiguration = false;
            }
        }

        public void GetScreenGeometries(out LYNOOKScreenGeometry mainScreen, out LYNOOKScreenGeometry sideScreen)
        {
            Transform reference = captureRig != null ? captureRig : transform;
            Quaternion mainLocalRotation = Quaternion.Euler(mainScreenEulerLocalDegrees);
            Quaternion mainWorldRotation = reference.rotation * mainLocalRotation;
            Vector3 mainCenter = reference.TransformPoint(mainScreenCenterLocalMm * MillimetersToMeters);
            mainScreen = new LYNOOKScreenGeometry(
                mainCenter,
                mainWorldRotation * Vector3.right,
                mainWorldRotation * Vector3.up,
                mainWorldRotation * Vector3.forward,
                mainScreenWidthMm * MillimetersToMeters,
                mainScreenHeightMm * MillimetersToMeters);

            Quaternion sideLocalRotation = mainLocalRotation
                * Quaternion.Euler(0f, screenAngleDegrees, 0f)
                * Quaternion.Euler(sideScreenEulerTrimDegrees);
            Quaternion sideWorldRotation = reference.rotation * sideLocalRotation;
            Vector3 sideRight = sideWorldRotation * Vector3.right;
            Vector3 sideUp = sideWorldRotation * Vector3.up;
            Vector3 sideCenter;
            if (deriveSidePositionFromHinge)
            {
                Vector3 sideTopLeft = mainScreen.TopRight + sideRight * (seamGapMm * MillimetersToMeters);
                sideCenter = sideTopLeft
                    + sideRight * (sideScreenWidthMm * MillimetersToMeters * 0.5f)
                    - sideUp * (sideScreenHeightMm * MillimetersToMeters * 0.5f);
            }
            else
            {
                sideCenter = reference.TransformPoint(sideScreenCenterLocalMm * MillimetersToMeters);
            }

            sideScreen = new LYNOOKScreenGeometry(
                sideCenter,
                sideRight,
                sideUp,
                sideWorldRotation * Vector3.forward,
                sideScreenWidthMm * MillimetersToMeters,
                sideScreenHeightMm * MillimetersToMeters);
        }

        bool ConfigureOffAxisCamera(
            Camera camera,
            RenderTexture target,
            Vector3 eye,
            LYNOOKScreenGeometry screen,
            out LYNOOKOffAxisFrustum frustum)
        {
            frustum = default;
            if (camera == null || target == null)
                return false;

            camera.transform.SetPositionAndRotation(eye, screen.CameraRotation);
            Vector3 localBottomLeft = Quaternion.Inverse(screen.CameraRotation) * (screen.BottomLeft - eye);
            Vector3 localTopRight = Quaternion.Inverse(screen.CameraRotation) * (screen.TopRight - eye);
            float planeDistance = Vector3.Dot(screen.center - eye, screen.forward);
            float near = nearClipMeters;
            float far = farClipMeters;
            if (planeDistance <= near + 0.000001f || localBottomLeft.z <= near || localTopRight.z <= near)
            {
                camera.enabled = false;
                return false;
            }

            float left = localBottomLeft.x * near / localBottomLeft.z;
            float right = localTopRight.x * near / localTopRight.z;
            float bottom = localBottomLeft.y * near / localBottomLeft.z;
            float top = localTopRight.y * near / localTopRight.z;
            if (right <= left || top <= bottom)
            {
                camera.enabled = false;
                return false;
            }

            frustum = new LYNOOKOffAxisFrustum(left, right, bottom, top, near, far);
            camera.orthographic = false;
            camera.usePhysicalProperties = false;
            camera.nearClipPlane = near;
            camera.farClipPlane = far;
            camera.targetTexture = target;
            camera.aspect = (float)target.width / target.height;
            camera.projectionMatrix = frustum.Matrix;
            camera.nonJitteredProjectionMatrix = frustum.Matrix;
            camera.enabled = true;
            return true;
        }

        void ConfigureMainCameraCommonSettings()
        {
            mainCaptureCamera.orthographic = false;
            mainCaptureCamera.usePhysicalProperties = false;
            mainCaptureCamera.nearClipPlane = nearClipMeters;
            mainCaptureCamera.farClipPlane = farClipMeters;
            mainCaptureCamera.targetTexture = mainCaptureTexture;
            mainCaptureCamera.aspect = (float)MainWidth / MainHeight;
            mainCaptureCamera.enabled = true;
        }

#if UNITY_EDITOR
        void AbsorbMainCameraEditIntoCaptureRig()
        {
            if (applyingConfiguration || captureRig == null || mainCaptureCamera == null || sideCaptureCamera == null)
                return;

            Quaternion expectedMainLocalRotation = Quaternion.Euler(mainScreenEulerLocalDegrees);
            Vector3 expectedEyeLocal = targetEyeLocalMm * MillimetersToMeters;
            Transform mainTransform = mainCaptureCamera.transform;
            bool mainWasMoved = Vector3.SqrMagnitude(mainTransform.localPosition - expectedEyeLocal) > 1e-8f
                || Quaternion.Angle(mainTransform.localRotation, expectedMainLocalRotation) > 0.001f;
            if (!mainWasMoved)
                return;

            Vector3 desiredEyeWorld = mainTransform.position;
            Quaternion desiredMainWorldRotation = mainTransform.rotation;
            UnityEditor.Undo.RecordObjects(
                new UnityEngine.Object[] { captureRig, mainTransform, sideCaptureCamera.transform },
                "Move LYNOOK Off-Axis Capture Rig");
            Quaternion desiredRigRotation = desiredMainWorldRotation * Quaternion.Inverse(expectedMainLocalRotation);
            Vector3 desiredRigPosition = desiredEyeWorld - desiredRigRotation * expectedEyeLocal;
            captureRig.SetPositionAndRotation(desiredRigPosition, desiredRigRotation);
            UnityEditor.EditorUtility.SetDirty(captureRig);
        }
#endif

        void RemoveExtraAudioListeners()
        {
            var sideListener = sideCaptureCamera.GetComponent<AudioListener>();
            if (sideListener != null)
                sideListener.enabled = false;
        }

        public bool TryValidate(out string report)
        {
            if (captureRig == null || mainCaptureCamera == null || sideCaptureCamera == null)
            {
                report = "Missing CaptureRig or camera references.";
                return false;
            }

            ApplyConfiguration();
            GetScreenGeometries(out var mainScreen, out var sideScreen);
            Vector3 eye = SharedEyeWorldPosition;
            float sharedEyeError = Vector3.Distance(mainCaptureCamera.transform.position, sideCaptureCamera.transform.position);
            float topAlignmentError = Vector3.Dot(sideScreen.TopLeft - mainScreen.TopRight, mainScreen.up);
            float bottomExtension = Vector3.Dot(mainScreen.BottomRight - sideScreen.BottomLeft, mainScreen.up);
            float expectedBottomExtension = (sideScreenHeightMm - mainScreenHeightMm) * MillimetersToMeters;
            float mainAspectError = Mathf.Abs(mainScreenWidthMm / mainScreenHeightMm - (float)MainWidth / MainHeight);
            float sideAspectError = Mathf.Abs(sideScreenWidthMm / sideScreenHeightMm - (float)SideWidth / SideHeight);

            bool valid = mainProjectionValid && sideProjectionValid;
            valid &= Vector3.Distance(mainCaptureCamera.transform.position, eye) < PositionToleranceMeters;
            valid &= Vector3.Distance(sideCaptureCamera.transform.position, eye) < PositionToleranceMeters;
            valid &= sharedEyeError < PositionToleranceMeters;
            valid &= Mathf.Abs(topAlignmentError) < 0.00001f;
            valid &= Mathf.Abs(bottomExtension - expectedBottomExtension) < 0.00001f;
            valid &= mainAspectError < 0.0001f;
            valid &= sideAspectError < 0.0001f;
            valid &= mainCaptureTexture != null && mainCaptureTexture.width == MainWidth && mainCaptureTexture.height == MainHeight;
            valid &= sideCaptureTexture != null && sideCaptureTexture.width == SideWidth && sideCaptureTexture.height == SideHeight;
            valid &= mainCaptureCamera.targetTexture == mainCaptureTexture;
            valid &= sideCaptureCamera.targetTexture == sideCaptureTexture;
            valid &= Approximately(mainCaptureCamera.projectionMatrix, mainFrustum.Matrix, 0.00001f);
            valid &= Approximately(sideCaptureCamera.projectionMatrix, sideFrustum.Matrix, 0.00001f);
            valid &= Mathf.Approximately(mainCaptureCamera.nearClipPlane, sideCaptureCamera.nearClipPlane);
            valid &= Mathf.Approximately(mainCaptureCamera.farClipPlane, sideCaptureCamera.farClipPlane);
            valid &= mainCaptureCamera.cullingMask == sideCaptureCamera.cullingMask;
            valid &= mainCaptureCamera.clearFlags == sideCaptureCamera.clearFlags;
            valid &= mainCaptureCamera.backgroundColor == sideCaptureCamera.backgroundColor;
            valid &= mainCaptureCamera.allowHDR == sideCaptureCamera.allowHDR;
            valid &= mainCaptureCamera.allowMSAA == sideCaptureCamera.allowMSAA;

            string calibrationState = calibrationValuesConfirmed
                ? "hardware calibration marked confirmed"
                : "PROVISIONAL values: eye/angle/gap still require hardware measurement";
            report = valid
                ? $"LYNOOK off-axis configuration is valid ({calibrationState}). "
                    + $"Shared eye={eye:F4}; angle={screenAngleDegrees:F3} deg; gap={seamGapMm:F3} mm; "
                    + $"side bottom extension={bottomExtension * 1000f:F3} mm. "
                    + $"Main[{mainFrustum}] Side[{sideFrustum}]"
                : $"LYNOOK off-axis validation failed. sharedEyeError={sharedEyeError:E3} m, "
                    + $"topAlignmentError={topAlignmentError * 1000f:F4} mm, "
                    + $"bottomExtension={bottomExtension * 1000f:F4} mm (expected {expectedBottomExtension * 1000f:F4} mm), "
                    + $"mainAspectError={mainAspectError:E3}, sideAspectError={sideAspectError:E3}.";
            return valid;
        }

        static bool Approximately(Matrix4x4 a, Matrix4x4 b, float tolerance)
        {
            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > tolerance)
                    return false;
            }
            return true;
        }

        void EnsurePreviewCamera()
        {
            if (!showGameViewPreview)
                return;
            if (mainPreviewCamera != null && activePreviewLayout == gameViewPreviewLayout)
                return;

            DestroyPreviewCamera();
            activePreviewLayout = gameViewPreviewLayout;
            if (gameViewPreviewLayout == LYNOOKGameViewPreviewLayout.SeparateDisplays)
            {
                mainPreviewCamera = CreatePreviewCamera(
                    "LYNOOK_Main_GameViewPreview", 0, mainCaptureTexture, out mainPreviewCameraObject);
                sidePreviewCamera = CreatePreviewCamera(
                    "LYNOOK_Right_GameViewPreview", 1, sideCaptureTexture, out sidePreviewCameraObject);
            }
            else
            {
                mainPreviewCamera = CreatePreviewCamera(
                    "LYNOOK_Combined_GameViewPreview", 0, null, out mainPreviewCameraObject);
            }
        }

        Camera CreatePreviewCamera(
            string objectName,
            int targetDisplay,
            RenderTexture source,
            out GameObject cameraObject)
        {
            cameraObject = new GameObject(objectName) { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = previewBackground;
            camera.cullingMask = 0;
            camera.depth = 1000f;
            camera.orthographic = true;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.targetTexture = null;
            camera.targetDisplay = targetDisplay;
            if (source != null)
            {
                var blitter = cameraObject.AddComponent<LYNOOKRenderTexturePreviewBlitter>();
                blitter.Source = source;
            }
            return camera;
        }

        void DestroyPreviewCamera()
        {
            DestroyPreviewObject(mainPreviewCameraObject);
            DestroyPreviewObject(sidePreviewCameraObject);
            mainPreviewCameraObject = null;
            sidePreviewCameraObject = null;
            mainPreviewCamera = null;
            sidePreviewCamera = null;
        }

        static void DestroyPreviewObject(GameObject previewObject)
        {
            if (previewObject == null)
                return;
            if (Application.isPlaying)
                Destroy(previewObject);
            else
                DestroyImmediate(previewObject);
        }

        void OnGUI()
        {
            if (!showGameViewPreview
                || gameViewPreviewLayout != LYNOOKGameViewPreviewLayout.CombinedOnDisplay1
                || mainCaptureTexture == null
                || sideCaptureTexture == null)
                return;

            float sidePixelsPerMm = SideWidth / sideScreenWidthMm;
            float physicalMainWidth = mainScreenWidthMm * sidePixelsPerMm;
            float physicalMainHeight = mainScreenHeightMm * sidePixelsPerMm;
            float canvasWidth = physicalMainWidth + SideWidth;
            float canvasHeight = SideHeight;
            float scale = Mathf.Min(Screen.width / canvasWidth, Screen.height / canvasHeight);
            float previewWidth = canvasWidth * scale;
            float previewHeight = canvasHeight * scale;
            float originX = (Screen.width - previewWidth) * 0.5f;
            float originY = (Screen.height - previewHeight) * 0.5f;
            var mainRect = new Rect(originX, originY, physicalMainWidth * scale, physicalMainHeight * scale);
            var sideRect = new Rect(mainRect.xMax, originY, SideWidth * scale, SideHeight * scale);

            GUI.color = previewBackground;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(mainRect, mainCaptureTexture, ScaleMode.StretchToFill, false);
            GUI.DrawTexture(sideRect, sideCaptureTexture, ScaleMode.StretchToFill, false);
            if (!showPreviewLabels)
                return;

            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = Color.white;
            DrawPreviewLabel(mainRect, "MAIN  1280x800 -> physical 1996x1248", style);
            DrawPreviewLabel(sideRect, "RIGHT  720x1280", style);
        }

        static void DrawPreviewLabel(Rect rect, string text, GUIStyle style)
        {
            var labelRect = new Rect(rect.x + 8f, rect.y + 8f, Mathf.Max(0f, rect.width - 16f), 24f);
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(labelRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(labelRect, "  " + text, style);
        }

        void OnDrawGizmosSelected()
        {
            if (!drawCalibrationGizmos || captureRig == null)
                return;
            GetScreenGeometries(out var mainScreen, out var sideScreen);
            DrawScreenGizmo(mainScreen, mainProjectionValid ? Color.cyan : Color.red);
            DrawScreenGizmo(sideScreen, sideProjectionValid ? Color.green : Color.red);
        }

        void DrawScreenGizmo(LYNOOKScreenGeometry screen, Color color)
        {
            Vector3 eye = SharedEyeWorldPosition;
            Vector3[] corners = { screen.BottomLeft, screen.BottomRight, screen.TopRight, screen.TopLeft };
            Gizmos.color = color;
            for (int i = 0; i < corners.Length; i++)
            {
                Gizmos.DrawLine(corners[i], corners[(i + 1) % corners.Length]);
                Gizmos.DrawLine(eye, corners[i]);
            }
            Gizmos.DrawWireSphere(eye, 0.006f);
        }
    }

    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    sealed class LYNOOKRenderTexturePreviewBlitter : MonoBehaviour
    {
        public RenderTexture Source { get; set; }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Source != null)
                Graphics.Blit(Source, destination);
            else
                Graphics.Blit(source, destination);
        }
    }
}
