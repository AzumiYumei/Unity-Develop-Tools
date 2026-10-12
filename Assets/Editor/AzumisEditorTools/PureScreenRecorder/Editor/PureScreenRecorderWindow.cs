using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace EPT.EditorTools
{
    public class PureScreenRecorderWindow : EditorWindow
    {
        private const string Pref = "EPT_PureScreenRecorder_";
        private const int MinFrameRate = 1;
        private const int MaxPendingReadbacks = 4;

        private ScreenRecordingSettings m_Settings = new ScreenRecordingSettings();

        [SerializeField]
        private Camera m_TargetCamera;

        private ScreenRecorderState m_State = ScreenRecorderState.Idle;
        private RenderTexture m_CaptureTexture;
        private RawVideoPipeWriter m_Writer;
        private double m_CountdownEndTime;
        private int m_TotalFrames;
        private int m_ScheduledFrames;
        private int m_ReadbackCompleted;
        private int m_CapturedFrames;
        private int m_LastReportedFrame;
        private int m_PreviousCaptureFramerate;
        private double m_NextStatusRepaintTime;
        private string m_OutputPath = "";
        private string m_Error = "";
        private string m_Info = "";
        private bool m_RevealWhenDone = true;
        private bool m_UseFixedGameTime = true;
        private bool m_FlipVertical = true;

        [MenuItem("Tools/Recording/Pure Screen Recorder")]
        public static void Open()
        {
            var window = GetWindow<PureScreenRecorderWindow>("Pure Recorder");
            window.minSize = new Vector2(460, 390);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPrefs();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (IsBusy())
            {
                CancelRecording("Recording cancelled because the window was closed.");
            }
            SavePrefs();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Pure Screen Recorder", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Renders the selected Camera into an offscreen RenderTexture. Editor windows are never captured.", MessageType.Info);

            using (new EditorGUI.DisabledScope(IsBusy()))
            {
                DrawSettings();
            }

            EditorGUILayout.Space(8);
            DrawControls();
            EditorGUILayout.Space(8);
            DrawStatus();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Recording", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_TargetCamera = (Camera)EditorGUILayout.ObjectField("Camera", m_TargetCamera, typeof(Camera), true);
                if (GUILayout.Button("Main", GUILayout.Width(56)))
                {
                    m_TargetCamera = Camera.main;
                }
            }

            m_Settings.width = Even(EditorGUILayout.IntField("Width", m_Settings.width));
            m_Settings.height = Even(EditorGUILayout.IntField("Height", m_Settings.height));
            m_Settings.frameRate = Mathf.Max(MinFrameRate, EditorGUILayout.IntField("Frame Rate", m_Settings.frameRate));
            m_Settings.durationSeconds = Mathf.Max(0.001f, EditorGUILayout.FloatField("Duration Seconds", m_Settings.durationSeconds));
            m_Settings.countdownSeconds = Mathf.Max(0, EditorGUILayout.FloatField("Countdown Seconds", m_Settings.countdownSeconds));
            m_Settings.crf = EditorGUILayout.IntSlider("CRF", m_Settings.crf, 0, 51);
            m_UseFixedGameTime = EditorGUILayout.Toggle("Fixed Game Time", m_UseFixedGameTime);
            m_FlipVertical = EditorGUILayout.Toggle("Flip Vertical", m_FlipVertical);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Settings.outputDirectory = EditorGUILayout.TextField("Folder", m_Settings.outputDirectory);
                if (GUILayout.Button("Browse", GUILayout.Width(72)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select output folder", OutputBrowseDir(), "");
                    if (!string.IsNullOrEmpty(selected)) m_Settings.outputDirectory = selected;
                }
            }

            m_Settings.outputFileName = EditorGUILayout.TextField("File Name", m_Settings.outputFileName);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Settings.ffmpegPath = EditorGUILayout.TextField("ffmpeg", m_Settings.ffmpegPath);
                if (GUILayout.Button("Browse", GUILayout.Width(72)))
                {
                    string selected = EditorUtility.OpenFilePanel("Select ffmpeg.exe", FfmpegBrowseDir(), "exe");
                    if (!string.IsNullOrEmpty(selected)) m_Settings.ffmpegPath = selected;
                }
            }
            m_RevealWhenDone = EditorGUILayout.Toggle("Reveal When Done", m_RevealWhenDone);
        }

        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(IsBusy()))
                {
                    if (GUILayout.Button("Start Recording", GUILayout.Height(28))) StartRecording();
                }
                using (new EditorGUI.DisabledScope(!IsBusy()))
                {
                    if (GUILayout.Button("Stop", GUILayout.Height(28), GUILayout.Width(96))) CancelRecording("Recording stopped by user.");
                }
            }
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("State", m_State.ToString());

            Camera camera = ResolveTargetCamera(false);
            EditorGUILayout.LabelField("Resolved Camera", camera != null ? camera.name : "None");

            if (m_State == ScreenRecorderState.CountingDown)
            {
                double remaining = Math.Max(0, m_CountdownEndTime - EditorApplication.timeSinceStartup);
                EditorGUILayout.LabelField("Countdown", remaining.ToString("F1", CultureInfo.InvariantCulture) + "s");
            }

            if (m_TotalFrames > 0)
            {
                float progress = Mathf.Clamp01(m_CapturedFrames / (float)m_TotalFrames);
                EditorGUI.ProgressBar(GUILayoutUtility.GetRect(1f, 18f), progress, string.Format(CultureInfo.InvariantCulture, "{0}/{1} frames", m_CapturedFrames, m_TotalFrames));
            }

            if (!string.IsNullOrEmpty(m_OutputPath))
            {
                EditorGUILayout.SelectableLabel(m_OutputPath, EditorStyles.textField, GUILayout.Height(20));
            }

            if (!string.IsNullOrEmpty(m_Info)) EditorGUILayout.HelpBox(m_Info, MessageType.Info);
            if (!string.IsNullOrEmpty(m_Error)) EditorGUILayout.HelpBox(m_Error, MessageType.Error);
        }

        private void StartRecording()
        {
            string error;
            if (!ValidateSettings(out error))
            {
                m_Error = error;
                m_Info = "";
                return;
            }

            SavePrefs();
            m_Error = "";
            m_Info = "";
            m_TotalFrames = FrameCount(m_Settings);
            m_ScheduledFrames = 0;
            m_ReadbackCompleted = 0;
            m_CapturedFrames = 0;
            m_LastReportedFrame = 0;
            m_OutputPath = BuildOutputPath(m_Settings);

            try
            {
                PrepareOutputFile(m_OutputPath);
                PrepareCaptureTexture();
                m_Writer = new RawVideoPipeWriter(m_Settings, m_OutputPath, m_TotalFrames, m_FlipVertical);
                m_Writer.Start();
            }
            catch (Exception e)
            {
                Fail(e.Message);
                return;
            }

            if (m_Settings.countdownSeconds > 0)
            {
                m_State = ScreenRecorderState.CountingDown;
                m_CountdownEndTime = EditorApplication.timeSinceStartup + m_Settings.countdownSeconds;
            }
            else
            {
                BeginFrameCapture();
            }

            Repaint();
        }

        private void OnEditorUpdate()
        {
            if (!IsBusy()) return;

            if (!Application.isPlaying)
            {
                CancelRecording("Enter Play Mode before recording.");
                return;
            }

            EditorApplication.QueuePlayerLoopUpdate();

            if (m_Writer != null && !string.IsNullOrEmpty(m_Writer.ErrorMessage))
            {
                Fail(m_Writer.ErrorMessage);
                return;
            }

            if (m_State == ScreenRecorderState.CountingDown)
            {
                if (EditorApplication.timeSinceStartup >= m_CountdownEndTime)
                {
                    BeginFrameCapture();
                }
                Repaint();
                return;
            }

            if (m_State == ScreenRecorderState.Recording)
            {
                bool readbackRoom = m_ScheduledFrames - m_ReadbackCompleted < MaxPendingReadbacks;
                if (m_ScheduledFrames < m_TotalFrames && readbackRoom)
                {
                    CaptureFrame(m_ScheduledFrames);
                    ++m_ScheduledFrames;
                }

                if (m_ScheduledFrames >= m_TotalFrames)
                {
                    RestoreCaptureFramerate();
                    m_State = ScreenRecorderState.Encoding;
                }
            }

            if (m_State == ScreenRecorderState.Encoding)
            {
                if (m_ReadbackCompleted >= m_ScheduledFrames && m_Writer != null && !m_Writer.IsCompleting)
                {
                    m_Writer.Complete();
                }

                if (m_Writer != null && m_Writer.IsFinished)
                {
                    FinishRecording();
                }
            }

            if (m_CapturedFrames != m_LastReportedFrame || m_State == ScreenRecorderState.Encoding)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now >= m_NextStatusRepaintTime || m_CapturedFrames >= m_TotalFrames)
                {
                    m_LastReportedFrame = m_CapturedFrames;
                    m_NextStatusRepaintTime = now + 0.1;
                    Repaint();
                }
            }
        }

        private void BeginFrameCapture()
        {
            if (m_UseFixedGameTime)
            {
                m_PreviousCaptureFramerate = Time.captureFramerate;
                Time.captureFramerate = m_Settings.frameRate;
            }
            m_State = ScreenRecorderState.Recording;
        }

        private void CaptureFrame(int frameIndex)
        {
            Camera camera = ResolveTargetCamera(true);
            if (camera == null)
            {
                Fail("No recording Camera was found.");
                return;
            }

            if (m_CaptureTexture == null)
            {
                Fail("Capture RenderTexture is invalid.");
                return;
            }

            RenderTexture oldTarget = camera.targetTexture;
            RenderTexture oldActive = RenderTexture.active;
            float oldAspect = camera.aspect;
            NativeArray<byte> buffer = default;
            bool requestStarted = false;
            try
            {
                camera.targetTexture = m_CaptureTexture;
                camera.aspect = m_Settings.width / (float)m_Settings.height;
                camera.Render();
                buffer = new NativeArray<byte>(m_Settings.width * m_Settings.height * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                AsyncGPUReadback.RequestIntoNativeArray(ref buffer, m_CaptureTexture, 0, TextureFormat.RGB24, request => OnReadback(frameIndex, request, buffer));
                requestStarted = true;
            }
            catch (Exception e)
            {
                if (!requestStarted && buffer.IsCreated) buffer.Dispose();
                Fail("Frame capture failed: " + e.Message);
            }
            finally
            {
                camera.targetTexture = oldTarget;
                camera.aspect = oldAspect;
                RenderTexture.active = oldActive;
            }
        }

        private void OnReadback(int frameIndex, AsyncGPUReadbackRequest request, NativeArray<byte> frame)
        {
            ++m_ReadbackCompleted;
            if (m_State == ScreenRecorderState.Cancelled || m_State == ScreenRecorderState.Failed)
            {
                if (frame.IsCreated) frame.Dispose();
                return;
            }

            if (request.hasError)
            {
                if (frame.IsCreated) frame.Dispose();
                Fail("AsyncGPUReadback failed.");
                return;
            }

            try
            {
                if (m_Writer != null) m_Writer.Enqueue(frameIndex, frame);
                else if (frame.IsCreated) frame.Dispose();
                m_CapturedFrames = Mathf.Max(m_CapturedFrames, frameIndex + 1);
            }
            catch (Exception e)
            {
                if (frame.IsCreated) frame.Dispose();
                Fail("Readback enqueue failed: " + e.Message);
            }
        }

        private void FinishRecording()
        {
            bool success = m_Writer != null && m_Writer.Succeeded && File.Exists(m_OutputPath);
            string error = success ? "" : FailureMessage();
            DisposeWriter(false);
            ReleaseCaptureTexture();
            AssetDatabase.Refresh();

            if (success)
            {
                m_State = ScreenRecorderState.Completed;
                m_Info = string.Format(CultureInfo.InvariantCulture, "Completed. {0}x{1}, {2}fps, {3} frames, {4:F3}s.", m_Settings.width, m_Settings.height, m_Settings.frameRate, m_TotalFrames, m_TotalFrames / (float)m_Settings.frameRate);
                m_Error = "";
                if (m_RevealWhenDone) EditorUtility.RevealInFinder(m_OutputPath);
            }
            else
            {
                m_State = ScreenRecorderState.Failed;
                m_Error = error;
                m_Info = "";
            }
            Repaint();
        }

        private void CancelRecording(string message)
        {
            RestoreCaptureFramerate();
            DisposeWriter(true);
            ReleaseCaptureTexture();
            m_State = ScreenRecorderState.Cancelled;
            m_Info = "";
            m_Error = message;
            Repaint();
        }

        private void Fail(string message)
        {
            RestoreCaptureFramerate();
            DisposeWriter(true);
            ReleaseCaptureTexture();
            m_State = ScreenRecorderState.Failed;
            m_Info = "";
            m_Error = message;
            Debug.LogError("[PureScreenRecorder] " + message);
            Repaint();
        }

        private bool ValidateSettings(out string error)
        {
            error = "";
            if (!Application.isPlaying)
            {
                error = "Enter Play Mode before recording.";
                return false;
            }
            if (ResolveTargetCamera(true) == null)
            {
                error = "Select a Camera, or ensure Camera.main exists.";
                return false;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                error = "AsyncGPUReadback is not supported on this device.";
                return false;
            }
            if (m_Settings.width <= 0 || m_Settings.height <= 0)
            {
                error = "Width and height must be greater than 0.";
                return false;
            }
            if ((m_Settings.width & 1) != 0 || (m_Settings.height & 1) != 0)
            {
                error = "Width and height must be even for H.264 yuv420p mp4 output.";
                return false;
            }
            if (m_Settings.frameRate < MinFrameRate)
            {
                error = "Frame rate must be greater than 0.";
                return false;
            }
            if (m_Settings.durationSeconds <= 0)
            {
                error = "Duration must be greater than 0.";
                return false;
            }
            double exactFrames = m_Settings.durationSeconds * m_Settings.frameRate;
            if (Math.Abs(exactFrames - Math.Round(exactFrames)) > 0.0001)
            {
                error = "durationSeconds * frameRate must be an integer frame count for exact mp4 duration.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(m_Settings.ffmpegPath))
            {
                error = "Set ffmpeg path, or enter ffmpeg if it is available in PATH.";
                return false;
            }
            bool commandOnly = m_Settings.ffmpegPath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0;
            if (!commandOnly && !File.Exists(m_Settings.ffmpegPath))
            {
                error = "The specified ffmpeg executable does not exist.";
                return false;
            }
            return true;
        }

        private Camera ResolveTargetCamera(bool assignIfFound)
        {
            if (m_TargetCamera != null) return m_TargetCamera;

            Camera camera = Camera.main;
            if (camera == null)
            {
                Camera[] cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; ++i)
                {
                    if (cameras[i] != null && cameras[i].enabled)
                    {
                        camera = cameras[i];
                        break;
                    }
                }
            }

            if (assignIfFound && camera != null)
            {
                m_TargetCamera = camera;
            }
            return camera;
        }

        private void PrepareCaptureTexture()
        {
            ReleaseCaptureTexture();
            m_CaptureTexture = new RenderTexture(m_Settings.width, m_Settings.height, 24, RenderTextureFormat.ARGB32)
            {
                name = "PureScreenRecorder_Offscreen_RT",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            m_CaptureTexture.Create();
        }

        private void ReleaseCaptureTexture()
        {
            if (m_CaptureTexture == null) return;
            m_CaptureTexture.Release();
            DestroyImmediate(m_CaptureTexture);
            m_CaptureTexture = null;
        }

        private void RestoreCaptureFramerate()
        {
            if (!m_UseFixedGameTime) return;
            if (Time.captureFramerate == m_Settings.frameRate)
            {
                Time.captureFramerate = m_PreviousCaptureFramerate;
            }
        }

        private void DisposeWriter(bool cancel)
        {
            if (m_Writer == null) return;
            if (cancel) m_Writer.Cancel();
            m_Writer.Dispose();
            m_Writer = null;
        }

        private bool IsBusy()
        {
            return m_State == ScreenRecorderState.CountingDown || m_State == ScreenRecorderState.Recording || m_State == ScreenRecorderState.Encoding;
        }

        private static int Even(int value)
        {
            value = Mathf.Max(2, value);
            return (value & 1) == 0 ? value : value + 1;
        }

        private static int FrameCount(ScreenRecordingSettings settings)
        {
            return Mathf.Max(1, Mathf.RoundToInt(settings.durationSeconds * settings.frameRate));
        }

        private string BuildOutputPath(ScreenRecordingSettings settings)
        {
            string directory = settings.outputDirectory;
            if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(Application.dataPath, "../Recordings");

            string fileName = settings.outputFileName;
            if (string.IsNullOrWhiteSpace(fileName)) fileName = string.Format(CultureInfo.InvariantCulture, "record_{0:yyyyMMdd_HHmmss}.mp4", DateTime.Now);
            if (!fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) fileName += ".mp4";
            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        private void PrepareOutputFile(string outputPath)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }

        private string OutputBrowseDir()
        {
            return string.IsNullOrWhiteSpace(m_Settings.outputDirectory) ? Path.Combine(Application.dataPath, "../Recordings") : m_Settings.outputDirectory;
        }

        private string FfmpegBrowseDir()
        {
            if (!string.IsNullOrWhiteSpace(m_Settings.ffmpegPath) && File.Exists(m_Settings.ffmpegPath)) return Path.GetDirectoryName(m_Settings.ffmpegPath);
            return Application.dataPath;
        }

        private string FailureMessage()
        {
            if (m_Writer == null) return "Video writer is missing.";
            if (!string.IsNullOrEmpty(m_Writer.ErrorMessage)) return m_Writer.ErrorMessage;
            return string.Format(CultureInfo.InvariantCulture, "ffmpeg failed. ExitCode:{0} Error:{1}", m_Writer.ExitCode, m_Writer.StandardError);
        }

        private void LoadPrefs()
        {
            m_Settings.width = EditorPrefs.GetInt(Pref + "Width", 1920);
            m_Settings.height = EditorPrefs.GetInt(Pref + "Height", 1080);
            m_Settings.frameRate = EditorPrefs.GetInt(Pref + "FrameRate", 30);
            m_Settings.durationSeconds = EditorPrefs.GetFloat(Pref + "Duration", 10f);
            m_Settings.countdownSeconds = EditorPrefs.GetFloat(Pref + "Countdown", 0f);
            m_Settings.outputDirectory = EditorPrefs.GetString(Pref + "OutputDirectory", Path.Combine(Application.dataPath, "../Recordings"));
            m_Settings.outputFileName = EditorPrefs.GetString(Pref + "OutputFileName", "");
            m_Settings.ffmpegPath = EditorPrefs.GetString(Pref + "Ffmpeg", "ffmpeg");
            m_Settings.crf = EditorPrefs.GetInt(Pref + "Crf", 18);
            m_RevealWhenDone = EditorPrefs.GetBool(Pref + "Reveal", true);
            m_UseFixedGameTime = EditorPrefs.GetBool(Pref + "FixedGameTime", true);
            m_FlipVertical = EditorPrefs.GetBool(Pref + "FlipVertical", true);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetInt(Pref + "Width", m_Settings.width);
            EditorPrefs.SetInt(Pref + "Height", m_Settings.height);
            EditorPrefs.SetInt(Pref + "FrameRate", m_Settings.frameRate);
            EditorPrefs.SetFloat(Pref + "Duration", m_Settings.durationSeconds);
            EditorPrefs.SetFloat(Pref + "Countdown", m_Settings.countdownSeconds);
            EditorPrefs.SetString(Pref + "OutputDirectory", m_Settings.outputDirectory ?? "");
            EditorPrefs.SetString(Pref + "OutputFileName", m_Settings.outputFileName ?? "");
            EditorPrefs.SetString(Pref + "Ffmpeg", m_Settings.ffmpegPath ?? "ffmpeg");
            EditorPrefs.SetInt(Pref + "Crf", m_Settings.crf);
            EditorPrefs.SetBool(Pref + "Reveal", m_RevealWhenDone);
            EditorPrefs.SetBool(Pref + "FixedGameTime", m_UseFixedGameTime);
            EditorPrefs.SetBool(Pref + "FlipVertical", m_FlipVertical);
        }

        private class RawVideoPipeWriter : IDisposable
        {
            public bool IsFinished { get { return m_IsFinished; } }
            public bool IsCompleting { get { return m_CompleteAdding; } }
            public bool Succeeded { get { return m_IsFinished && string.IsNullOrEmpty(m_ErrorMessage) && m_Process != null && m_Process.HasExited && m_Process.ExitCode == 0 && m_WrittenFrames == m_TotalFrames; } }
            public int ExitCode { get { return m_Process != null && m_Process.HasExited ? m_Process.ExitCode : -1; } }
            public string ErrorMessage { get { return m_ErrorMessage; } }
            public string StandardError { get { return m_StandardError; } }

            private readonly ScreenRecordingSettings m_Settings;
            private readonly string m_OutputPath;
            private readonly int m_TotalFrames;
            private readonly bool m_FlipVertical;
            private readonly SortedDictionary<int, NativeArray<byte>> m_PendingFrames = new SortedDictionary<int, NativeArray<byte>>();
            private readonly object m_Lock = new object();
            private readonly AutoResetEvent m_FrameEvent = new AutoResetEvent(false);

            private Process m_Process;
            private Task m_WriteTask;
            private Task m_ErrorReadTask;
            private volatile bool m_CompleteAdding;
            private volatile bool m_Cancelled;
            private volatile bool m_IsFinished;
            private int m_NextFrameIndex;
            private int m_WrittenFrames;
            private string m_ErrorMessage = "";
            private string m_StandardError = "";

            public RawVideoPipeWriter(ScreenRecordingSettings settings, string outputPath, int totalFrames, bool flipVertical)
            {
                m_Settings = settings;
                m_OutputPath = outputPath;
                m_TotalFrames = totalFrames;
                m_FlipVertical = flipVertical;
            }

            public void Start()
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = m_Settings.ffmpegPath,
                    Arguments = BuildArguments(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                m_Process = Process.Start(startInfo);
                if (m_Process == null) throw new InvalidOperationException("Failed to start ffmpeg process.");

                m_ErrorReadTask = Task.Run(() =>
                {
                    try { m_StandardError = m_Process.StandardError.ReadToEnd(); }
                    catch (Exception e) { m_StandardError = e.Message; }
                });
                m_WriteTask = Task.Run(WriteLoop);
            }

            public void Enqueue(int frameIndex, NativeArray<byte> frame)
            {
                if (m_Cancelled || m_CompleteAdding) return;
                lock (m_Lock) m_PendingFrames[frameIndex] = frame;
                m_FrameEvent.Set();
            }

            public void Complete()
            {
                m_CompleteAdding = true;
                m_FrameEvent.Set();
            }

            public void Cancel()
            {
                m_Cancelled = true;
                m_CompleteAdding = true;
                m_FrameEvent.Set();
                DisposePendingFrames();
                try
                {
                    if (m_Process != null && !m_Process.HasExited) m_Process.Kill();
                }
                catch { }
            }

            public void Dispose()
            {
                if (m_Cancelled && m_Process != null && !m_Process.HasExited) Cancel();
                if (m_WriteTask != null)
                {
                    try { m_WriteTask.Wait(500); } catch { }
                    m_WriteTask = null;
                }
                if (m_ErrorReadTask != null)
                {
                    try { m_ErrorReadTask.Wait(500); } catch { }
                    m_ErrorReadTask = null;
                }
                if (m_Process != null)
                {
                    m_Process.Dispose();
                    m_Process = null;
                }
                m_FrameEvent.Dispose();
            }

            private void WriteLoop()
            {
                byte[] bytes = null;
                try
                {
                    while (!m_Cancelled)
                    {
                        NativeArray<byte> frame = default;
                        bool hasFrame = false;
                        lock (m_Lock)
                        {
                            if (m_PendingFrames.TryGetValue(m_NextFrameIndex, out frame))
                            {
                                m_PendingFrames.Remove(m_NextFrameIndex);
                                ++m_NextFrameIndex;
                                hasFrame = true;
                            }
                            else if (m_CompleteAdding) break;
                        }

                        if (!hasFrame)
                        {
                            m_FrameEvent.WaitOne(50);
                            continue;
                        }

                        try
                        {
                            if (frame.IsCreated)
                            {
                                if (bytes == null || bytes.Length < frame.Length) bytes = new byte[frame.Length];
                                frame.CopyTo(bytes);
                                m_Process.StandardInput.BaseStream.Write(bytes, 0, frame.Length);
                                ++m_WrittenFrames;
                            }
                        }
                        finally
                        {
                            if (frame.IsCreated) frame.Dispose();
                        }
                    }

                    try
                    {
                        m_Process.StandardInput.BaseStream.Flush();
                        m_Process.StandardInput.Close();
                    }
                    catch { }

                    if (!m_Cancelled) m_Process.WaitForExit();
                }
                catch (Exception e)
                {
                    m_ErrorMessage = e.Message;
                    try
                    {
                        if (m_Process != null && !m_Process.HasExited) m_Process.Kill();
                    }
                    catch { }
                }
                finally
                {
                    DisposePendingFrames();
                    m_IsFinished = true;
                }
            }

            private void DisposePendingFrames()
            {
                lock (m_Lock)
                {
                    foreach (var pair in m_PendingFrames)
                    {
                        NativeArray<byte> frame = pair.Value;
                        if (frame.IsCreated) frame.Dispose();
                    }
                    m_PendingFrames.Clear();
                }
            }

            private string BuildArguments()
            {
                string flip = m_FlipVertical ? " -vf vflip" : "";
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgb24 -s {0}x{1} -framerate {2} -i pipe:0 -frames:v {3}{4} -an -c:v libx264 -preset veryfast -crf {5} -pix_fmt yuv420p -r {2} -video_track_timescale {2} -movflags +faststart {6}",
                    m_Settings.width,
                    m_Settings.height,
                    m_Settings.frameRate,
                    m_TotalFrames,
                    flip,
                    m_Settings.crf,
                    Quote(m_OutputPath));
            }

            private static string Quote(string value)
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }
        }
    }
}
