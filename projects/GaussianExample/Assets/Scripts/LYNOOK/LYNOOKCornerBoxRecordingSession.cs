using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
#endif

namespace Lynook.DualScreen
{
    /// <summary>
    /// Records the two panel feeds plus sweet-spot, elevated, left, and right
    /// physical-corner observer views from one RecorderController and one fixed frame interval.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LYNOOKCornerBoxRecordingSession : MonoBehaviour
    {
        const string FrontBaseName = "front_corner45";
        const string SideBaseName = "side_corner45";
        const string ObserverBaseName = "corner45_observer_preview";
        const string ObserverTopBaseName = "corner45_observer_top";
        const string ObserverLeftBaseName = "corner45_observer_left";
        const string ObserverRightBaseName = "corner45_observer_right";

        [SerializeField] LYNOOKDualCameraRig cameraRig;
        [SerializeField] RenderTexture observerTexture;
        [SerializeField] RenderTexture observerTopTexture;
        [SerializeField] RenderTexture observerLeftTexture;
        [SerializeField] RenderTexture observerRightTexture;
        [SerializeField, Min(1)] int frameRate = 30;
        [SerializeField, Min(1)] int frameCount = 300;
        [SerializeField] string outputFolder = "Recordings/LYNOOK/CornerBox45";
        [SerializeField] string ffmpegExecutable = "/opt/homebrew/bin/ffmpeg";

#if UNITY_EDITOR
        RecorderController recorderController;
        bool recordingStarted;
        bool recordingFinished;
        bool outputsFinalized;
        LYNOOKRecordingWorldExporter worldExporter;
#endif

        public void SetReferences(
            LYNOOKDualCameraRig rig,
            RenderTexture observerPreview,
            RenderTexture observerTopPreview,
            RenderTexture observerLeftPreview,
            RenderTexture observerRightPreview)
        {
            cameraRig = rig;
            observerTexture = observerPreview;
            observerTopTexture = observerTopPreview;
            observerLeftTexture = observerLeftPreview;
            observerRightTexture = observerRightPreview;
        }

#if UNITY_EDITOR
        bool recordingRequested;
        string requestedOutputFolder;

        public void BeginRecording(string folder)
        {
            if (!Application.isPlaying || recordingRequested)
                throw new System.InvalidOperationException("Recording must be requested once in Play Mode from Tools > LYNOOK > Record.");
            requestedOutputFolder = folder;
            recordingStarted = false;
            recordingFinished = false;
            outputsFinalized = false;
            worldExporter = null;
            recordingRequested = true;
            StartCoroutine(RecordAfterInitialization());
        }

        IEnumerator RecordAfterInitialization()
        {
            yield return null;
            try
            {
                StartSynchronizedRecording();
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.ExitPlaymode();
            }
        }
#endif

        void Update()
        {
#if UNITY_EDITOR
            if (!recordingStarted || recordingFinished || recorderController == null)
                return;
            if (recorderController.IsRecording())
                return;

            recordingFinished = true;
            recorderController.StopRecording();
            Debug.Log($"LYNOOK CornerBox45 recording finished: {frameCount} frames at {frameRate} fps.");
            FinalizeOutputsAndExit();
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            if (recorderController != null && recorderController.IsRecording())
                recorderController.StopRecording();

            if (recordingStarted && !outputsFinalized)
                FinalizeMovOutputs();
            if (recordingStarted)
                worldExporter?.TryWrite();
            recordingRequested = false;
#endif
        }

#if UNITY_EDITOR
        void StartSynchronizedRecording()
        {
            if (cameraRig == null)
                throw new MissingReferenceException("CornerBox45 recording requires a LYNOOKDualCameraRig.");
            if (observerTexture == null)
                throw new MissingReferenceException("CornerBox45 recording requires an observer RenderTexture.");
            if (observerTopTexture == null || observerLeftTexture == null || observerRightTexture == null)
                throw new MissingReferenceException("CornerBox45 multi-angle recording requires top, left, and right observer RenderTextures.");

            cameraRig.ApplyConfiguration();
            if (!cameraRig.TryValidate(out string validationReport))
                throw new System.InvalidOperationException(validationReport);

            string absoluteOutputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", requestedOutputFolder ?? outputFolder));
            Directory.CreateDirectory(absoluteOutputFolder);

            var settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            settings.name = "LYNOOK CornerBox45 Six-Feed Recorder";
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = frameRate;
            settings.CapFrameRate = true;
            settings.ExitPlayMode = false;
            settings.SetRecordModeToFrameInterval(0, frameCount - 1);
            settings.AddRecorderSettings(CreateMovieRecorder(
                "Front Panel Recorder",
                cameraRig.MainCaptureTexture,
                Path.Combine(absoluteOutputFolder, FrontBaseName)));
            settings.AddRecorderSettings(CreateMovieRecorder(
                "Side Panel Recorder",
                cameraRig.SideCaptureTexture,
                Path.Combine(absoluteOutputFolder, SideBaseName)));
            settings.AddRecorderSettings(CreateMovieRecorder(
                "45 Degree Observer Recorder",
                observerTexture,
                Path.Combine(absoluteOutputFolder, ObserverBaseName)));
            settings.AddRecorderSettings(CreateMovieRecorder(
                "Elevated Observer Recorder",
                observerTopTexture,
                Path.Combine(absoluteOutputFolder, ObserverTopBaseName)));
            settings.AddRecorderSettings(CreateMovieRecorder(
                "Left Observer Recorder",
                observerLeftTexture,
                Path.Combine(absoluteOutputFolder, ObserverLeftBaseName)));
            settings.AddRecorderSettings(CreateMovieRecorder(
                "Right Observer Recorder",
                observerRightTexture,
                Path.Combine(absoluteOutputFolder, ObserverRightBaseName)));

            RecorderOptions.VerboseMode = true;
            recorderController = new RecorderController(settings);
            recorderController.PrepareRecording();
            worldExporter = LYNOOKRecordingWorldExporter.Capture(absoluteOutputFolder,
                cameraRig.MainCaptureCamera, cameraRig.SideCaptureCamera, FrontBaseName, SideBaseName);
            if (!recorderController.StartRecording())
                throw new System.InvalidOperationException("Unity Recorder failed to start the CornerBox45 multi-angle recording.");

            recordingStarted = true;
            Debug.Log(
                $"LYNOOK CornerBox45 recording started: six synchronized H.264 feeds, "
                + $"{frameCount} frames at {frameRate} fps -> {absoluteOutputFolder}");
        }

        void FinalizeOutputsAndExit()
        {
            FinalizeMovOutputs();
            worldExporter?.TryWrite();
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
                else if (EditorApplication.isPlaying)
                    EditorApplication.ExitPlaymode();
            };
        }

        void FinalizeMovOutputs()
        {
            if (outputsFinalized)
                return;

            string executable = ResolveFfmpegExecutable();
            if (string.IsNullOrEmpty(executable))
            {
                Debug.LogError("FFmpeg was not found. CornerBox45 MP4 files are valid, but MOV remux was skipped.");
                return;
            }

            string absoluteOutputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", requestedOutputFolder ?? outputFolder));
            bool frontOk = RemuxToMov(executable, absoluteOutputFolder, FrontBaseName);
            bool sideOk = RemuxToMov(executable, absoluteOutputFolder, SideBaseName);
            bool observerOk = RemuxToMov(executable, absoluteOutputFolder, ObserverBaseName);
            bool observerTopOk = RemuxToMov(executable, absoluteOutputFolder, ObserverTopBaseName);
            bool observerLeftOk = RemuxToMov(executable, absoluteOutputFolder, ObserverLeftBaseName);
            bool observerRightOk = RemuxToMov(executable, absoluteOutputFolder, ObserverRightBaseName);
            outputsFinalized = frontOk
                && sideOk
                && observerOk
                && observerTopOk
                && observerLeftOk
                && observerRightOk;
            if (outputsFinalized)
            {
                Debug.Log(
                    "LYNOOK CornerBox45 MOV outputs ready: front + side + sweet-spot + top + left + right.");
            }
        }

        static bool RemuxToMov(string executable, string folder, string baseName)
        {
            string input = Path.Combine(folder, baseName + ".mp4");
            string output = Path.Combine(folder, baseName + ".mov");
            if (!File.Exists(input))
                return false;

            string temporary = output + ".tmp.mov";
            var errorLog = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-y -i {Quote(input)} -map 0:v:0 -c:v copy -an -movflags +faststart {Quote(temporary)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            try
            {
                using var process = new Process { StartInfo = startInfo };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        errorLog.AppendLine(args.Data);
                };
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(temporary))
                {
                    Debug.LogError($"FFmpeg remux failed for {baseName}:\n{errorLog}");
                    return false;
                }

                if (File.Exists(output))
                    File.Delete(output);
                File.Move(temporary, output);
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"FFmpeg remux failed for {baseName}: {exception.Message}");
                return false;
            }
        }

        string ResolveFfmpegExecutable()
        {
            if (!string.IsNullOrWhiteSpace(ffmpegExecutable) && File.Exists(ffmpegExecutable))
                return ffmpegExecutable;

            string[] commonLocations = { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" };
            foreach (string location in commonLocations)
            {
                if (File.Exists(location))
                    return location;
            }
            return null;
        }

        static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static MovieRecorderSettings CreateMovieRecorder(string recorderName, RenderTexture source, string outputPath)
        {
            var recorder = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            recorder.name = recorderName;
            recorder.Enabled = true;
            recorder.CaptureAudio = false;
            recorder.CaptureAlpha = false;
            recorder.FrameRatePlayback = FrameRatePlayback.Constant;
            recorder.ImageInputSettings = new RenderTextureInputSettings
            {
                RenderTexture = source,
                FlipFinalOutput = false
            };
            recorder.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
                EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
                GopSize = 30,
                NumConsecutiveBFrames = 2
            };
            recorder.OutputFile = outputPath;
            return recorder;
        }
#endif
    }
}
