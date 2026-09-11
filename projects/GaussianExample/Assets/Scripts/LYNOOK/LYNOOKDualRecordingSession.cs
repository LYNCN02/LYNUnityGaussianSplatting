using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Playables;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
#endif

namespace Lynook.DualScreen
{
    public enum LYNOOKMovieOutputFormat
    {
        H264Mp4AndFfmpegMov,
        H264Mp4,
        ProRes422LtMov
    }

    [DisallowMultipleComponent]
    public sealed class LYNOOKDualRecordingSession : MonoBehaviour
    {
        const string DefaultMainBaseName = "main_offaxis";
        const string DefaultSideBaseName = "right_offaxis";
        const string DefaultPhysicalPreviewName = "offaxis_physical_preview.mov";
        const int PhysicalPreviewMainWidth = 1996;
        const int PhysicalPreviewMainHeight = 1248;
        const int PhysicalPreviewSideWidth = 720;
        const int PhysicalPreviewSideHeight = 1280;

        [SerializeField] LYNOOKDualCameraRig cameraRig;
        [SerializeField] PlayableDirector sharedTimeline;
        [SerializeField, Min(1)] int frameRate = 30;
        [SerializeField, Min(1)] int frameCount = 300;
        [SerializeField] string outputFolder = "Recordings/LYNOOK";
        [SerializeField] string mainBaseName = DefaultMainBaseName;
        [SerializeField] string sideBaseName = DefaultSideBaseName;
        [SerializeField] string physicalPreviewName = DefaultPhysicalPreviewName;
        [SerializeField] LYNOOKMovieOutputFormat outputFormat = LYNOOKMovieOutputFormat.H264Mp4AndFfmpegMov;
        [SerializeField] string ffmpegExecutable = "/opt/homebrew/bin/ffmpeg";

#if UNITY_EDITOR
        RecorderController recorderController;
        bool recordingStarted;
        bool recordingFinished;
        bool movFinalized;
        bool previewFinalized;
#endif

        public void SetReferences(LYNOOKDualCameraRig rig, PlayableDirector director)
        {
            cameraRig = rig;
            sharedTimeline = director;
        }

        public void SetOutputNames(string mainOutputBaseName, string sideOutputBaseName, string previewFileName)
        {
            mainBaseName = RequireSimpleFileName(mainOutputBaseName, nameof(mainOutputBaseName));
            sideBaseName = RequireSimpleFileName(sideOutputBaseName, nameof(sideOutputBaseName));
            physicalPreviewName = RequireSimpleFileName(previewFileName, nameof(previewFileName));
        }

        public int FrameRate => frameRate;
        public int FrameCount => frameCount;

#if UNITY_EDITOR
        bool recordingRequested;
        string requestedOutputFolder;
        Camera perspectiveMain;
        Camera perspectiveSide;
        RenderTexture perspectiveMainTexture;
        RenderTexture perspectiveSideTexture;
        RenderTexture previousMainTexture;
        RenderTexture previousSideTexture;

        public void SetPerspectiveCameras(Camera main, Camera side)
        {
            perspectiveMain = main;
            perspectiveSide = side;
        }

        public void BeginRecording(string folder)
        {
            if (!Application.isPlaying || recordingRequested)
                throw new System.InvalidOperationException("Recording must be requested once in Play Mode from Tools > LYNOOK > Record.");
            requestedOutputFolder = folder;
            recordingStarted = false;
            recordingFinished = false;
            movFinalized = false;
            previewFinalized = false;
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
            Debug.Log($"LYNOOK dual recording finished: {frameCount} frames at {frameRate} fps.");
            FinalizeOutputsAndExit();
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            if (recorderController != null && recorderController.IsRecording())
                recorderController.StopRecording();

            // A user may stop Play Mode before the configured frame interval ends. The
            // recorder still finalizes valid partial MP4 files, so remux those immediately
            // instead of requiring the full 300-frame session to finish.
            if (recordingStarted
                && outputFormat == LYNOOKMovieOutputFormat.H264Mp4AndFfmpegMov
                && !movFinalized)
            {
                recordingFinished = true;
                Debug.Log("LYNOOK Play Mode stopped early; finalizing the off-axis pair and physical preview.");
                if (RemuxPairToMovSynchronously())
                    CreatePhysicalPreviewSynchronously();
            }
            ReleasePerspectiveTextures();
            recordingRequested = false;
#endif
        }

#if UNITY_EDITOR
        void ReleasePerspectiveTextures()
        {
            if (perspectiveMainTexture != null)
            {
                if (perspectiveMain != null)
                    perspectiveMain.targetTexture = previousMainTexture;
                perspectiveMainTexture.Release();
                Destroy(perspectiveMainTexture);
                perspectiveMainTexture = null;
            }
            if (perspectiveSideTexture != null)
            {
                if (perspectiveSide != null)
                    perspectiveSide.targetTexture = previousSideTexture;
                perspectiveSideTexture.Release();
                Destroy(perspectiveSideTexture);
                perspectiveSideTexture = null;
            }
        }

        static RenderTexture CreatePerspectiveTexture(int width, int height, string textureName)
        {
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = textureName,
                antiAliasing = 1,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            if (!texture.Create())
                throw new System.InvalidOperationException("Unable to create recording texture: " + textureName);
            return texture;
        }

        void StartSynchronizedRecording()
        {
            RenderTexture mainTexture;
            RenderTexture sideTexture;
            if (cameraRig != null)
            {
                cameraRig.ApplyConfiguration();
                if (!cameraRig.TryValidate(out var validationReport))
                    throw new System.InvalidOperationException(validationReport);
                mainTexture = cameraRig.MainCaptureTexture;
                sideTexture = cameraRig.SideCaptureTexture;
            }
            else
            {
                if (perspectiveMain == null || perspectiveSide == null
                    || !perspectiveMain.isActiveAndEnabled || !perspectiveSide.isActiveAndEnabled)
                    throw new MissingReferenceException("Perspective recording requires two active cameras.");
                previousMainTexture = perspectiveMain.targetTexture;
                previousSideTexture = perspectiveSide.targetTexture;
                perspectiveMainTexture = CreatePerspectiveTexture(LYNOOKDualCameraRig.MainWidth,
                    LYNOOKDualCameraRig.MainHeight, "PerspectiveMainCapture");
                perspectiveSideTexture = CreatePerspectiveTexture(LYNOOKDualCameraRig.SideWidth,
                    LYNOOKDualCameraRig.SideHeight, "PerspectiveSideCapture");
                perspectiveMain.targetTexture = perspectiveMainTexture;
                perspectiveSide.targetTexture = perspectiveSideTexture;
                mainTexture = perspectiveMainTexture;
                sideTexture = perspectiveSideTexture;
            }

            if (sharedTimeline != null)
            {
                sharedTimeline.Stop();
                sharedTimeline.time = 0.0;
                sharedTimeline.Evaluate();
            }

            string absoluteOutputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", requestedOutputFolder ?? outputFolder));
            Directory.CreateDirectory(absoluteOutputFolder);

            var controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            controllerSettings.name = "LYNOOK Dual Recorder Session";
            controllerSettings.FrameRatePlayback = FrameRatePlayback.Constant;
            controllerSettings.FrameRate = frameRate;
            controllerSettings.CapFrameRate = true;
            controllerSettings.ExitPlayMode = false;
            controllerSettings.SetRecordModeToFrameInterval(0, frameCount - 1);

            controllerSettings.AddRecorderSettings(CreateMovieRecorder(
                "Main Recorder",
                mainTexture,
                Path.Combine(absoluteOutputFolder, MainOutputBaseName),
                outputFormat));
            controllerSettings.AddRecorderSettings(CreateMovieRecorder(
                "Right Recorder",
                sideTexture,
                Path.Combine(absoluteOutputFolder, SideOutputBaseName),
                outputFormat));

            RecorderOptions.VerboseMode = true;
            recorderController = new RecorderController(controllerSettings);
            recorderController.PrepareRecording();

            // One RecorderController owns both RecorderSettings, so Prepare and Record are
            // issued once for the pair rather than sequentially per camera.
            if (!recorderController.StartRecording())
                throw new System.InvalidOperationException("Unity Recorder failed to start the synchronized LYNOOK session.");

            if (sharedTimeline != null)
                sharedTimeline.Play();

            recordingStarted = true;
            string outputDescription = outputFormat switch
            {
                LYNOOKMovieOutputFormat.H264Mp4AndFfmpegMov => ".mp4 + automatic H.264 .mov remux",
                LYNOOKMovieOutputFormat.ProRes422LtMov => "native ProRes .mov",
                _ => ".mp4"
            };
            Debug.Log($"LYNOOK dual recording started: {frameCount} frames at {frameRate} fps, output {outputDescription} -> {absoluteOutputFolder}");
        }

        void FinalizeOutputsAndExit()
        {
            if (outputFormat == LYNOOKMovieOutputFormat.H264Mp4AndFfmpegMov)
            {
                if (RemuxPairToMovSynchronously())
                    CreatePhysicalPreviewSynchronously();
            }
            else
            {
                CreatePhysicalPreviewSynchronously();
            }

            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
                else if (EditorApplication.isPlaying)
                    EditorApplication.ExitPlaymode();
            };
        }

        bool RemuxPairToMovSynchronously()
        {
            string executable = ResolveFfmpegExecutable();
            if (string.IsNullOrEmpty(executable))
            {
                Debug.LogError(
                    "FFmpeg was not found. MP4 files are valid, but MOV remux was skipped. " +
                    "Install FFmpeg or update the Ffmpeg Executable field.");
                return false;
            }

            string absoluteOutputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", requestedOutputFolder ?? outputFolder));
            var mainJob = StartRemux(
                executable,
                Path.Combine(absoluteOutputFolder, MainOutputBaseName + ".mp4"),
                Path.Combine(absoluteOutputFolder, MainOutputBaseName + ".mov"));
            var rightJob = StartRemux(
                executable,
                Path.Combine(absoluteOutputFolder, SideOutputBaseName + ".mp4"),
                Path.Combine(absoluteOutputFolder, SideOutputBaseName + ".mov"));

            if (mainJob == null || rightJob == null)
            {
                FinishIncompleteJob(mainJob);
                FinishIncompleteJob(rightJob);
                return false;
            }

            mainJob.Process.WaitForExit();
            rightJob.Process.WaitForExit();
            bool mainSucceeded = FinalizeRemux(mainJob);
            bool rightSucceeded = FinalizeRemux(rightJob);
            if (mainSucceeded && rightSucceeded)
            {
                movFinalized = true;
                Debug.Log(
                    $"LYNOOK FFmpeg remux finished: {MainOutputBaseName}.mov + {SideOutputBaseName}.mov "
                    + "(H.264 stream copy, no re-encode).");
            }
            return mainSucceeded && rightSucceeded;
        }

        bool CreatePhysicalPreviewSynchronously()
        {
            if (previewFinalized)
                return true;

            string executable = ResolveFfmpegExecutable();
            if (string.IsNullOrEmpty(executable))
            {
                Debug.LogError("FFmpeg was not found; the physical-size stitched preview was skipped.");
                return false;
            }

            string absoluteOutputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", requestedOutputFolder ?? outputFolder));
            string extension = outputFormat == LYNOOKMovieOutputFormat.H264Mp4 ? ".mp4" : ".mov";
            string mainInput = Path.Combine(absoluteOutputFolder, MainOutputBaseName + extension);
            string sideInput = Path.Combine(absoluteOutputFolder, SideOutputBaseName + extension);
            string finalOutput = Path.Combine(absoluteOutputFolder, PhysicalPreviewFileName);
            if (!File.Exists(mainInput) || !File.Exists(sideInput))
            {
                Debug.LogError($"Cannot create physical preview because an input is missing: {mainInput} / {sideInput}");
                return false;
            }

            string temporaryOutput = finalOutput + ".tmp.mov";
            var errorLog = new StringBuilder();
            string filter = $"[0:v]scale={PhysicalPreviewMainWidth}:{PhysicalPreviewMainHeight}:flags=lanczos,"
                + $"pad={PhysicalPreviewMainWidth}:{PhysicalPreviewSideHeight}:0:0:black[main];"
                + $"[1:v]scale={PhysicalPreviewSideWidth}:{PhysicalPreviewSideHeight}:flags=lanczos[side];"
                + "[main][side]hstack=inputs=2[v]";
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-y -i {Quote(mainInput)} -i {Quote(sideInput)} -filter_complex {Quote(filter)} "
                    + $"-map {Quote("[v]")} -an -r {frameRate} -c:v libx264 -preset medium -crf 18 "
                    + $"-pix_fmt yuv420p -movflags +faststart {Quote(temporaryOutput)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            try
            {
                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        errorLog.AppendLine(args.Data);
                };
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(temporaryOutput))
                {
                    Debug.LogError($"FFmpeg physical preview failed:\n{errorLog}");
                    return false;
                }

                if (File.Exists(finalOutput))
                    File.Delete(finalOutput);
                File.Move(temporaryOutput, finalOutput);
                previewFinalized = true;
                Debug.Log(
                    $"LYNOOK physical preview created: {finalOutput} "
                    + $"({PhysicalPreviewMainWidth + PhysicalPreviewSideWidth}x{PhysicalPreviewSideHeight}; "
                    + "main 1996x1248 top-aligned with 32 px black below; side 720x1280).");
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"Failed to create the physical preview: {exception.Message}");
                return false;
            }
        }

        static void FinishIncompleteJob(RemuxJob job)
        {
            if (job == null)
                return;

            job.Process.WaitForExit();
            FinalizeRemux(job);
        }

        sealed class RemuxJob : System.IDisposable
        {
            public readonly Process Process;
            public readonly StringBuilder ErrorLog;
            public readonly string TemporaryOutput;
            public readonly string FinalOutput;

            public RemuxJob(Process process, StringBuilder errorLog, string temporaryOutput, string finalOutput)
            {
                Process = process;
                ErrorLog = errorLog;
                TemporaryOutput = temporaryOutput;
                FinalOutput = finalOutput;
            }

            public void Dispose()
            {
                Process?.Dispose();
            }
        }

        RemuxJob StartRemux(string executable, string input, string output)
        {
            if (!File.Exists(input))
            {
                Debug.LogError($"Cannot create MOV because the MP4 input is missing: {input}");
                return null;
            }

            string temporaryOutput = output + ".tmp.mov";
            var errorLog = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-y -i {Quote(input)} -map 0:v:0 -c:v copy -an -movflags +faststart {Quote(temporaryOutput)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            try
            {
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        errorLog.AppendLine(args.Data);
                };
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                return new RemuxJob(process, errorLog, temporaryOutput, output);
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"Failed to start FFmpeg: {exception.Message}");
                return null;
            }
        }

        static bool FinalizeRemux(RemuxJob job)
        {
            try
            {
                job.Process.WaitForExit();
                if (job.Process.ExitCode != 0 || !File.Exists(job.TemporaryOutput))
                {
                    Debug.LogError($"FFmpeg remux failed for {job.FinalOutput}:\n{job.ErrorLog}");
                    return false;
                }

                if (File.Exists(job.FinalOutput))
                    File.Delete(job.FinalOutput);
                File.Move(job.TemporaryOutput, job.FinalOutput);
                return true;
            }
            finally
            {
                job.Dispose();
            }
        }

        string ResolveFfmpegExecutable()
        {
            if (!string.IsNullOrWhiteSpace(ffmpegExecutable) && File.Exists(ffmpegExecutable))
                return ffmpegExecutable;

            string[] commonLocations =
            {
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg"
            };
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

        string MainOutputBaseName => NormalizeOptionalName(mainBaseName, DefaultMainBaseName);
        string SideOutputBaseName => NormalizeOptionalName(sideBaseName, DefaultSideBaseName);
        string PhysicalPreviewFileName => NormalizeOptionalName(physicalPreviewName, DefaultPhysicalPreviewName);

        static string NormalizeOptionalName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        static string RequireSimpleFileName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new System.ArgumentException("A non-empty file name is required.", parameterName);

            string trimmed = value.Trim();
            if (trimmed != Path.GetFileName(trimmed))
                throw new System.ArgumentException("Use a file name without folders.", parameterName);
            return trimmed;
        }

        static MovieRecorderSettings CreateMovieRecorder(
            string recorderName,
            RenderTexture source,
            string outputPath,
            LYNOOKMovieOutputFormat format)
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

            if (format == LYNOOKMovieOutputFormat.ProRes422LtMov)
            {
                recorder.EncoderSettings = new ProResEncoderSettings
                {
                    Format = ProResEncoderSettings.OutputFormat.ProRes422LT
                };
            }
            else
            {
                recorder.EncoderSettings = new CoreEncoderSettings
                {
                    Codec = CoreEncoderSettings.OutputCodec.MP4,
                    EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
                    EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
                    GopSize = 30,
                    NumConsecutiveBFrames = 2
                };
            }

            recorder.OutputFile = outputPath;
            return recorder;
        }
#endif
    }
}
