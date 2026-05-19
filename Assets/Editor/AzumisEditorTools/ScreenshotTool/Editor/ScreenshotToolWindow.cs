// ================================================================
// 功能名称: 截图工具
// 
// 文件名称: ScreenshotToolWindow.cs
// 版本: 1.2
// 
// 描述:
// Editor 截图工具窗口，支持：
// 1) Game 视图截图（ScreenCapture）；
// 2) 指定相机离屏截图（Camera.Render + ReadPixels）；
// 3) Play 模式视频录制与 mp4 编码输出。
// 
// 使用方法:
// 1) 菜单 Tools/Azumi/截图工具 打开窗口；
// 2) 在“Game视图截图”或“相机截图”区域配置参数后执行截图；
// 3) 在 Play 模式下可使用 F9 或面板按钮录制视频并输出 mp4 文件。
// 
// 修改历史:
// 2026-04-29 - 1.0 增加 CameraView 预设遍历多角度截图功能
// 2026-05-19 - 1.1 去除对 CameraView 脚本类型的直接依赖，改为通用组件预设读取
// 2026-05-19 - 1.2 移除与 CameraView 配套的多角度预设截图功能，仅保留通用截图与录屏
// ================================================================

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

public class ScreenshotToolWindow : EditorWindow
{
    private const string PrefKeyPrefix = "Azumi_ScreenshotTool_";
    private const string DefaultFolderName = "Screenshots";
    private const int DefaultVideoFps = 30;
    private const float DefaultVideoDurationSeconds = 5f;
    private static readonly int[] MsaaSampleOptions = { 1, 2, 4, 8 };
    private static readonly string[] MsaaSampleOptionLabels = { "1x", "2x", "4x", "8x" };
    private static readonly string[] FfmpegPresetOptions = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };

    private string m_OutputFolderName = DefaultFolderName;
    private string m_FilePrefix = "Screenshot";
    private int m_Width = 1920;
    private int m_Height = 1080;
    private int m_SuperSize = 1;
    private Camera m_TargetCamera;
    private bool m_RevealInExplorer = true;

    private int m_VideoFps = DefaultVideoFps;
    private bool m_VideoFixedDurationEnabled = true;
    private float m_VideoDurationSeconds = DefaultVideoDurationSeconds;
    private string m_FfmpegPath = string.Empty;
    private bool m_KeepVideoFrames;
    private bool m_UseRecordingMsaa = true;
    private int m_RecordingMsaaSamples = 4;
    private bool m_OverrideUrpQuality;
    private float m_RecordingUrpRenderScale = 1.25f;
    private int m_RecordingUrpMsaaSamples = 4;
    private bool m_UseHighQualityEncoding = true;
    private int m_VideoCrf = 16;
    private string m_FfmpegPreset = "slow";
    private int m_MaxPendingReadbackFrames = 2;

    private bool m_IsRecording;
    private string m_RecordingFrameDirectory = string.Empty;
    private string m_RecordingOutputPath = string.Empty;
    private string m_RecordingRawVideoPath = string.Empty;
    private int m_RecordingFrameIndex;
    private int m_WrittenRawFrameCount;
    private int m_RecordingWidth;
    private int m_RecordingHeight;
    private int m_PreparedRecordingWidth;
    private int m_PreparedRecordingHeight;
    private int m_PreparedRecordingMsaaSamples;
    private double m_RecordingStartTime;
    private double m_RecordingStopTime;
    private double m_NextFrameTime;
    private Camera m_RecordingCamera;
    private RenderTexture m_RecordingRenderTexture;
    private RenderTexture m_RecordingReadbackTexture;
    private bool m_RecordingReadbackPrewarmed;
    private ConcurrentQueue<RawVideoFrame> m_RawFrameQueue;
    private Thread m_RawWriterThread;
    private volatile bool m_RawWriterShouldStop;
    private volatile bool m_RawWriterFailed;
    private string m_RawWriterError = string.Empty;
    private int m_PendingReadbackCount;
    private int m_DroppedRecordingFrameCount;
    private bool m_IsEncodingVideo;
    private Process m_EncodingProcess;
    private string m_EncodingOutputPath = string.Empty;
    private UniversalRenderPipelineAsset m_OverriddenUrpAsset;
    private float m_OriginalUrpRenderScale;
    private int m_OriginalUrpMsaaSamples;
    private bool m_HasUrpQualityOverride;
    private Camera m_OverriddenCamera;
    private bool m_OriginalCameraAllowMsaa;
    private bool m_HasCameraQualityOverride;

    private struct RawVideoFrame
    {
        public int Index;
        public byte[] Data;
        public int Length;
    }

    [MenuItem("Tools/Azumi/截图工具")]
    public static void ShowWindow()
    {
        ScreenshotToolWindow window = GetWindow<ScreenshotToolWindow>("截图工具");
        window.minSize = new Vector2(420f, 260f);
        window.Show();
    }

    [Shortcut("Azumi Screenshot Tool/Toggle Video Recording", KeyCode.F9)]
    private static void ToggleVideoRecordingShortcut()
    {
        ScreenshotToolWindow window = GetWindow<ScreenshotToolWindow>("截图工具");
        window.minSize = new Vector2(420f, 260f);
        window.ToggleRecording();
    }

    private void OnEnable()
    {
        LoadPrefs();
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private void OnDisable()
    {
        if (m_IsRecording)
        {
            StopRecording(true);
        }

        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        RestoreCameraQualityOverrides();
        RestoreRecordingQualityOverrides();
        ReleasePreparedRecordingTexture();
        SavePrefs();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("保存设置", EditorStyles.boldLabel);
        m_OutputFolderName = EditorGUILayout.TextField("输出目录(项目根目录下)", m_OutputFolderName);
        m_FilePrefix = EditorGUILayout.TextField("文件名前缀", m_FilePrefix);
        m_RevealInExplorer = EditorGUILayout.Toggle("截图后打开目录", m_RevealInExplorer);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Game视图截图", EditorStyles.boldLabel);
        m_SuperSize = EditorGUILayout.IntSlider("放大倍数", m_SuperSize, 1, 4);
        if (GUILayout.Button("截取 Game 视图", GUILayout.Height(26f)))
        {
            CaptureGameView();
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("相机截图", EditorStyles.boldLabel);
        m_TargetCamera = (Camera)EditorGUILayout.ObjectField("目标相机", m_TargetCamera, typeof(Camera), true);
        m_Width = EditorGUILayout.IntField("宽度", m_Width);
        m_Height = EditorGUILayout.IntField("高度", m_Height);

        m_Width = Mathf.Max(1, m_Width);
        m_Height = Mathf.Max(1, m_Height);

        if (GUILayout.Button("截取目标相机", GUILayout.Height(26f)))
        {
            CaptureCamera();
        }

        DrawVideoRecordingSettings();
    }

    private void CaptureGameView()
    {
        string filePath = CreateScreenshotPath();
        ScreenCapture.CaptureScreenshot(filePath, m_SuperSize);
        EditorApplication.delayCall += () =>
        {
            AssetDatabase.Refresh();
            if (m_RevealInExplorer && File.Exists(filePath))
            {
                EditorUtility.RevealInFinder(filePath);
            }
        };
        Debug.Log($"[ScreenshotTool] 已保存 Game 视图截图: {filePath}");
    }

    private void CaptureCamera()
    {
        Camera camera = m_TargetCamera != null ? m_TargetCamera : Camera.main;
        if (camera == null)
        {
            EditorUtility.DisplayDialog("截图工具", "未找到可用相机，请先指定目标相机。", "确定");
            return;
        }

        string filePath = CreateScreenshotPath();
        if (!TryCaptureCameraToPng(camera, m_Width, m_Height, filePath, out string error))
        {
            EditorUtility.DisplayDialog("截图工具", $"截图失败：{error}", "确定");
            return;
        }

        AssetDatabase.Refresh();
        if (m_RevealInExplorer)
        {
            EditorUtility.RevealInFinder(filePath);
        }
        Debug.Log($"[ScreenshotTool] 已保存相机截图: {filePath}");
    }

    private string MakeSafeFileNamePart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Unnamed";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool invalid = false;
            for (int j = 0; j < invalidChars.Length; j++)
            {
                if (c == invalidChars[j])
                {
                    invalid = true;
                    break;
                }
            }

            sb.Append(invalid ? '_' : c);
        }

        return sb.ToString();
    }

    private bool TryCaptureCameraToPng(Camera camera, int width, int height, string filePath, out string error)
    {
        // 使用离屏 RT 强制渲染，并用 ReadPixels 读回，确保在 Editor（非 Play）也能稳定输出 PNG。
        error = string.Empty;
        if (camera == null)
        {
            error = "Camera 为空。";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "截图分辨率无效。";
            return false;
        }

        try
        {
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = rt;
                RenderTexture.active = rt;
                camera.Render();
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply(false, false);
                byte[] pngData = tex.EncodeToPNG();
                File.WriteAllBytes(filePath, pngData);
                return true;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                DestroyImmediate(rt);
                DestroyImmediate(tex);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void DrawVideoRecordingSettings()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Play 模式视频录制", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            m_FfmpegPath = EditorGUILayout.TextField("ffmpeg.exe 路径", m_FfmpegPath);
            if (GUILayout.Button("搜索", GUILayout.Width(52f)))
            {
                SearchAndApplyFfmpegPath(true);
            }

            if (GUILayout.Button("浏览", GUILayout.Width(52f)))
            {
                string selectedPath = EditorUtility.OpenFilePanel("选择 ffmpeg.exe", string.Empty, "exe");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    m_FfmpegPath = selectedPath;
                }
            }
        }

        m_VideoFps = EditorGUILayout.IntField("帧率", m_VideoFps);
        m_VideoFps = Mathf.Clamp(m_VideoFps, 1, 240);

        m_VideoFixedDurationEnabled = EditorGUILayout.Toggle("固定时长", m_VideoFixedDurationEnabled);
        if (m_VideoFixedDurationEnabled)
        {
            m_VideoDurationSeconds = EditorGUILayout.FloatField("录制秒数", m_VideoDurationSeconds);
            m_VideoDurationSeconds = Mathf.Max(0.1f, m_VideoDurationSeconds);
        }

        m_KeepVideoFrames = EditorGUILayout.Toggle("保留帧数据", m_KeepVideoFrames);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("录制质量", EditorStyles.boldLabel);
        m_UseRecordingMsaa = EditorGUILayout.Toggle("录制RT MSAA", m_UseRecordingMsaa);
        using (new EditorGUI.DisabledScope(!m_UseRecordingMsaa))
        {
            m_RecordingMsaaSamples = DrawMsaaPopup("录制RT MSAA质量", m_RecordingMsaaSamples);
        }

        m_OverrideUrpQuality = EditorGUILayout.Toggle("录制时覆盖URP质量", m_OverrideUrpQuality);
        using (new EditorGUI.DisabledScope(!m_OverrideUrpQuality))
        {
            m_RecordingUrpRenderScale = EditorGUILayout.Slider("URP Render Scale", m_RecordingUrpRenderScale, 0.5f, 2f);
            m_RecordingUrpMsaaSamples = DrawMsaaPopup("URP MSAA质量", m_RecordingUrpMsaaSamples);
        }

        m_UseHighQualityEncoding = EditorGUILayout.Toggle("高质量MP4编码", m_UseHighQualityEncoding);
        using (new EditorGUI.DisabledScope(!m_UseHighQualityEncoding))
        {
            m_VideoCrf = EditorGUILayout.IntSlider("编码CRF(越低越高)", m_VideoCrf, 12, 28);
            int presetIndex = Mathf.Max(0, Array.IndexOf(FfmpegPresetOptions, m_FfmpegPreset));
            presetIndex = EditorGUILayout.Popup("编码Preset", presetIndex, FfmpegPresetOptions);
            m_FfmpegPreset = FfmpegPresetOptions[presetIndex];
        }

        m_MaxPendingReadbackFrames = EditorGUILayout.IntSlider("最大待读回帧", m_MaxPendingReadbackFrames, 1, 8);

        string status = m_IsRecording
            ? $"录制中: {m_RecordingFrameIndex} 帧 / {EditorApplication.timeSinceStartup - m_RecordingStartTime:0.0}s / 待读回 {m_PendingReadbackCount} / 跳过 {m_DroppedRecordingFrameCount}"
            : m_IsEncodingVideo
                ? $"视频合成中: {m_EncodingOutputPath}"
            : EditorApplication.isPlaying && m_RecordingRenderTexture != null
                ? $"未录制 / {(m_RecordingReadbackPrewarmed ? "已预热" : "预热中")}"
                : "未录制";
        EditorGUILayout.LabelField("状态", status);

        string buttonText = m_IsRecording ? "停止录制 (F9)" : "开始录制 (F9)";
        if (GUILayout.Button(buttonText, GUILayout.Height(26f)))
        {
            ToggleRecording();
        }
    }

    private void ToggleRecording()
    {
        if (m_IsRecording)
        {
            StopRecording(true);
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("截图工具", "视频录制只能在 Play 模式下开始。", "确定");
            return;
        }

        if (m_IsEncodingVideo)
        {
            EditorUtility.DisplayDialog("截图工具", "上一个视频仍在合成中，请等待完成后再开始新的录制。", "确定");
            return;
        }

        Camera camera = m_TargetCamera != null ? m_TargetCamera : Camera.main;
        if (camera == null)
        {
            EditorUtility.DisplayDialog("截图工具", "未找到可用相机，请先指定目标相机。", "确定");
            return;
        }

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            EditorUtility.DisplayDialog("截图工具", "当前设备不支持 AsyncGPUReadback，无法使用低卡顿视频录制。", "确定");
            return;
        }

        m_RecordingWidth = Mathf.Max(1, m_Width * m_SuperSize);
        m_RecordingHeight = Mathf.Max(1, m_Height * m_SuperSize);
        ApplyRecordingQualityOverrides();
        ApplyCameraQualityOverrides(camera);
        EnsureRecordingTexture();
        m_RecordingCamera = camera;
        m_RecordingFrameIndex = 0;
        m_WrittenRawFrameCount = 0;
        m_DroppedRecordingFrameCount = 0;
        m_RecordingStartTime = EditorApplication.timeSinceStartup;
        m_RecordingStopTime = m_RecordingStartTime;
        m_NextFrameTime = m_RecordingStartTime;
        CreateVideoPaths(out m_RecordingFrameDirectory, out m_RecordingOutputPath);
        m_RecordingRawVideoPath = Path.Combine(m_RecordingFrameDirectory, "frames.rgba");

        StartRawFrameWriter();
        m_IsRecording = true;

        Debug.Log($"[ScreenshotTool] 开始录制视频帧流: {m_RecordingRawVideoPath}");
        Repaint();
    }

    private void StopRecording(bool encodeVideo)
    {
        if (!m_IsRecording)
        {
            return;
        }

        m_IsRecording = false;
        m_RecordingStopTime = EditorApplication.timeSinceStartup;
        AsyncGPUReadback.WaitAllRequests();
        StopRawFrameWriter();
        m_RecordingCamera = null;
        RestoreCameraQualityOverrides();
        RestoreRecordingQualityOverrides();

        if (encodeVideo)
        {
            EncodeRecordingToMp4();
        }

        AssetDatabase.Refresh();
        Repaint();
    }

    private void OnEditorUpdate()
    {
        if (!m_IsRecording)
        {
            if (EditorApplication.isPlaying)
            {
                PrepareRecordingTextureForCurrentSettings();
            }

            return;
        }

        if (!EditorApplication.isPlaying)
        {
            StopRecording(true);
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        if (m_VideoFixedDurationEnabled && now - m_RecordingStartTime >= m_VideoDurationSeconds)
        {
            StopRecording(true);
            return;
        }

        if (now < m_NextFrameTime)
        {
            return;
        }

        CaptureRecordingFrame();

        double frameInterval = 1.0 / Mathf.Max(1, m_VideoFps);
        m_NextFrameTime += frameInterval;
        if (m_NextFrameTime < now)
        {
            m_NextFrameTime = now + frameInterval;
        }

        Repaint();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (m_IsRecording && state == PlayModeStateChange.ExitingPlayMode)
        {
            StopRecording(true);
        }

        if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
        {
            RestoreRecordingQualityOverrides();
            RestoreCameraQualityOverrides();
        }
    }

    private void CaptureRecordingFrame()
    {
        if (m_RecordingCamera == null || m_RecordingRenderTexture == null || m_RawFrameQueue == null)
        {
            StopRecording(true);
            return;
        }

        if (m_PendingReadbackCount >= m_MaxPendingReadbackFrames)
        {
            m_DroppedRecordingFrameCount++;
            return;
        }

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = m_RecordingCamera.targetTexture;
        try
        {
            m_RecordingCamera.targetTexture = m_RecordingRenderTexture;
            RenderTexture.active = m_RecordingRenderTexture;
            m_RecordingCamera.Render();
            Graphics.Blit(m_RecordingRenderTexture, m_RecordingReadbackTexture);

            m_RecordingFrameIndex++;
            int frameIndex = m_RecordingFrameIndex;
            Interlocked.Increment(ref m_PendingReadbackCount);
            AsyncGPUReadback.Request(m_RecordingReadbackTexture, 0, TextureFormat.RGBA32, request =>
            {
                try
                {
                    if (request.hasError)
                    {
                        m_RawWriterFailed = true;
                        m_RawWriterError = $"AsyncGPUReadback failed at frame {frameIndex}.";
                        return;
                    }

                    Unity.Collections.NativeArray<byte> readbackData = request.GetData<byte>();
                    byte[] frameData = ArrayPool<byte>.Shared.Rent(readbackData.Length);
                    readbackData.CopyTo(frameData);
                    m_RawFrameQueue?.Enqueue(new RawVideoFrame
                    {
                        Index = frameIndex,
                        Data = frameData,
                        Length = readbackData.Length
                    });
                }
                finally
                {
                    Interlocked.Decrement(ref m_PendingReadbackCount);
                }
            });
        }
        finally
        {
            m_RecordingCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
        }
    }

    private void EncodeRecordingToMp4()
    {
        if (m_WrittenRawFrameCount <= 0)
        {
            Debug.LogWarning("[ScreenshotTool] 没有录到任何视频帧，已跳过 mp4 编码。");
            return;
        }

        if (m_RawWriterFailed)
        {
            EditorUtility.DisplayDialog("截图工具", "录制帧写入失败，已跳过 mp4 编码。详情请查看 Console。", "确定");
            Debug.LogError($"[ScreenshotTool] 录制帧写入失败，帧数据保留在: {m_RecordingFrameDirectory}\n{m_RawWriterError}");
            return;
        }

        if (string.IsNullOrWhiteSpace(m_FfmpegPath) || !File.Exists(m_FfmpegPath))
        {
            if (!SearchAndApplyFfmpegPath(false))
            {
                EditorUtility.DisplayDialog("截图工具", "未找到 ffmpeg.exe，已保留帧数据，未生成 mp4。请安装 ffmpeg，或在面板中指定 ffmpeg.exe 路径。", "确定");
                Debug.LogWarning($"[ScreenshotTool] 未找到 ffmpeg.exe，帧数据保留在: {m_RecordingFrameDirectory}");
                return;
            }
        }

        double recordedSeconds = Math.Max(0.001d, m_RecordingStopTime - m_RecordingStartTime);
        double encodeFrameRate = Math.Max(0.001d, m_WrittenRawFrameCount / recordedSeconds);
        string encodeFrameRateText = encodeFrameRate.ToString("0.###", CultureInfo.InvariantCulture);
        string videoSize = $"{m_RecordingWidth}x{m_RecordingHeight}";
        string qualityArguments = m_UseHighQualityEncoding
            ? $"-crf {m_VideoCrf} -preset {m_FfmpegPreset} "
            : string.Empty;
        string arguments = $"-y -f rawvideo -pixel_format rgba -video_size {videoSize} -framerate {encodeFrameRateText} -i {Quote(m_RecordingRawVideoPath)} -vf vflip -r {m_VideoFps} -c:v libx264 {qualityArguments}-pix_fmt yuv420p -movflags +faststart {Quote(m_RecordingOutputPath)}";

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = m_FfmpegPath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        try
        {
            Process process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            StringBuilder stderrBuilder = new StringBuilder();
            object stderrLock = new object();

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    return;
                }

                lock (stderrLock)
                {
                    stderrBuilder.AppendLine(args.Data);
                }
            };

            string outputPath = m_RecordingOutputPath;
            string frameDirectory = m_RecordingFrameDirectory;
            bool keepFrames = m_KeepVideoFrames;
            bool revealInExplorer = m_RevealInExplorer;
            double loggedRecordedSeconds = recordedSeconds;
            double loggedEncodeFrameRate = encodeFrameRate;
            int loggedFrameCount = m_WrittenRawFrameCount;

            process.Exited += (_, _) =>
            {
                int exitCode = process.ExitCode;
                process.WaitForExit();
                string stderr;
                lock (stderrLock)
                {
                    stderr = stderrBuilder.ToString();
                }

                EditorApplication.delayCall += () =>
                {
                    CompleteVideoEncoding(process, exitCode, stderr, outputPath, frameDirectory, keepFrames, revealInExplorer, loggedRecordedSeconds, loggedEncodeFrameRate, loggedFrameCount);
                };
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("ffmpeg process was not started.");
            }

            m_EncodingProcess = process;
            m_IsEncodingVideo = true;
            m_EncodingOutputPath = outputPath;
            process.BeginErrorReadLine();
            Debug.Log($"[ScreenshotTool] 已开始后台合成视频: {outputPath}");
            Repaint();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[ScreenshotTool] ffmpeg 编码异常，帧数据保留在: {m_RecordingFrameDirectory}\n{exception}");
            EditorUtility.DisplayDialog("截图工具", "ffmpeg 编码异常，已保留帧数据。详情请查看 Console。", "确定");
            return;
        }
    }

    private void CompleteVideoEncoding(
        Process process,
        int exitCode,
        string stderr,
        string outputPath,
        string frameDirectory,
        bool keepFrames,
        bool revealInExplorer,
        double recordedSeconds,
        double encodeFrameRate,
        int frameCount)
    {
        if (m_EncodingProcess == process)
        {
            m_EncodingProcess = null;
            m_IsEncodingVideo = false;
            m_EncodingOutputPath = string.Empty;
        }

        process.Dispose();

        if (exitCode != 0 || !File.Exists(outputPath))
        {
            Debug.LogError($"[ScreenshotTool] ffmpeg 编码失败，帧数据保留在: {frameDirectory}\n{stderr}");
            EditorUtility.DisplayDialog("截图工具", "ffmpeg 编码失败，已保留帧数据。详情请查看 Console。", "确定");
            Repaint();
            return;
        }

        if (!keepFrames && Directory.Exists(frameDirectory))
        {
            Directory.Delete(frameDirectory, true);
        }

        AssetDatabase.Refresh();
        if (revealInExplorer)
        {
            EditorUtility.RevealInFinder(outputPath);
        }

        Debug.Log($"[ScreenshotTool] 已保存视频: {outputPath}，录制时长 {recordedSeconds:0.###} 秒，实际帧率 {encodeFrameRate:0.###} fps，写入帧数 {frameCount}。");
        Repaint();
    }

    private void StartRawFrameWriter()
    {
        m_RawFrameQueue = new ConcurrentQueue<RawVideoFrame>();
        m_RawWriterShouldStop = false;
        m_RawWriterFailed = false;
        m_RawWriterError = string.Empty;
        m_PendingReadbackCount = 0;

        m_RawWriterThread = new Thread(WriteRawFrames)
        {
            IsBackground = true,
            Name = "ScreenshotTool Raw Frame Writer"
        };
        m_RawWriterThread.Start();
    }

    private void StopRawFrameWriter()
    {
        m_RawWriterShouldStop = true;
        if (m_RawWriterThread != null)
        {
            m_RawWriterThread.Join();
            m_RawWriterThread = null;
        }

        m_RawFrameQueue = null;
    }

    private void WriteRawFrames()
    {
        Dictionary<int, RawVideoFrame> pendingFrames = new Dictionary<int, RawVideoFrame>();
        int nextFrameIndex = 1;

        try
        {
            using (FileStream stream = new FileStream(m_RecordingRawVideoPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024))
            {
                while (!m_RawWriterShouldStop || (m_RawFrameQueue != null && !m_RawFrameQueue.IsEmpty) || pendingFrames.ContainsKey(nextFrameIndex))
                {
                    while (m_RawFrameQueue != null && m_RawFrameQueue.TryDequeue(out RawVideoFrame frame))
                    {
                        pendingFrames[frame.Index] = frame;
                    }

                    bool wroteFrame = false;
                    while (pendingFrames.TryGetValue(nextFrameIndex, out RawVideoFrame pendingFrame))
                    {
                        stream.Write(pendingFrame.Data, 0, pendingFrame.Length);
                        ArrayPool<byte>.Shared.Return(pendingFrame.Data);
                        pendingFrames.Remove(nextFrameIndex);
                        nextFrameIndex++;
                        m_WrittenRawFrameCount = nextFrameIndex - 1;
                        wroteFrame = true;
                    }

                    if (!wroteFrame)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            m_RawWriterFailed = true;
            m_RawWriterError = exception.ToString();
        }
        finally
        {
            foreach (RawVideoFrame frame in pendingFrames.Values)
            {
                ArrayPool<byte>.Shared.Return(frame.Data);
            }
        }
    }

    private void PrepareRecordingTextureForCurrentSettings()
    {
        m_RecordingWidth = Mathf.Max(1, m_Width * m_SuperSize);
        m_RecordingHeight = Mathf.Max(1, m_Height * m_SuperSize);
        EnsureRecordingTexture();
    }

    private void EnsureRecordingTexture()
    {
        int msaaSamples = m_UseRecordingMsaa ? NormalizeMsaaSampleCount(m_RecordingMsaaSamples, 4) : 1;
        if (m_RecordingRenderTexture != null &&
            m_PreparedRecordingWidth == m_RecordingWidth &&
            m_PreparedRecordingHeight == m_RecordingHeight &&
            m_PreparedRecordingMsaaSamples == msaaSamples)
        {
            return;
        }

        ReleasePreparedRecordingTexture();

        m_RecordingRenderTexture = new RenderTexture(m_RecordingWidth, m_RecordingHeight, 24, RenderTextureFormat.ARGB32);
        m_RecordingRenderTexture.antiAliasing = msaaSamples;
        m_RecordingRenderTexture.Create();
        m_RecordingReadbackTexture = new RenderTexture(m_RecordingWidth, m_RecordingHeight, 0, RenderTextureFormat.ARGB32);
        m_RecordingReadbackTexture.antiAliasing = 1;
        m_RecordingReadbackTexture.Create();
        m_PreparedRecordingWidth = m_RecordingWidth;
        m_PreparedRecordingHeight = m_RecordingHeight;
        m_PreparedRecordingMsaaSamples = msaaSamples;
        m_RecordingReadbackPrewarmed = false;

        if (SystemInfo.supportsAsyncGPUReadback)
        {
            AsyncGPUReadback.Request(m_RecordingReadbackTexture, 0, TextureFormat.RGBA32, _ =>
            {
                m_RecordingReadbackPrewarmed = true;
            });
        }
    }

    private void ReleasePreparedRecordingTexture()
    {
        if (m_RecordingRenderTexture != null)
        {
            AsyncGPUReadback.WaitAllRequests();
            DestroyImmediate(m_RecordingRenderTexture);
            m_RecordingRenderTexture = null;
        }

        if (m_RecordingReadbackTexture != null)
        {
            DestroyImmediate(m_RecordingReadbackTexture);
            m_RecordingReadbackTexture = null;
        }

        m_PreparedRecordingWidth = 0;
        m_PreparedRecordingHeight = 0;
        m_PreparedRecordingMsaaSamples = 0;
        m_RecordingReadbackPrewarmed = false;
        m_RecordingCamera = null;
    }

    private int DrawMsaaPopup(string label, int currentValue)
    {
        int index = Array.IndexOf(MsaaSampleOptions, currentValue);
        if (index < 0)
        {
            index = 0;
        }

        index = EditorGUILayout.Popup(label, index, MsaaSampleOptionLabels);
        return MsaaSampleOptions[index];
    }

    private int NormalizeMsaaSampleCount(int value, int fallback)
    {
        return Array.IndexOf(MsaaSampleOptions, value) >= 0 ? value : fallback;
    }

    private void ApplyRecordingQualityOverrides()
    {
        if (!m_OverrideUrpQuality || m_HasUrpQualityOverride)
        {
            return;
        }

        UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset == null)
        {
            Debug.LogWarning("[ScreenshotTool] 当前没有可用的 URP Asset，已跳过 URP 录制质量覆盖。");
            return;
        }

        m_OverriddenUrpAsset = urpAsset;
        m_OriginalUrpRenderScale = urpAsset.renderScale;
        m_OriginalUrpMsaaSamples = urpAsset.msaaSampleCount;
        m_HasUrpQualityOverride = true;

        urpAsset.renderScale = m_RecordingUrpRenderScale;
        urpAsset.msaaSampleCount = NormalizeMsaaSampleCount(m_RecordingUrpMsaaSamples, 4);
    }

    private void RestoreRecordingQualityOverrides()
    {
        if (!m_HasUrpQualityOverride || m_OverriddenUrpAsset == null)
        {
            m_HasUrpQualityOverride = false;
            m_OverriddenUrpAsset = null;
            return;
        }

        m_OverriddenUrpAsset.renderScale = m_OriginalUrpRenderScale;
        m_OverriddenUrpAsset.msaaSampleCount = m_OriginalUrpMsaaSamples;
        m_HasUrpQualityOverride = false;
        m_OverriddenUrpAsset = null;
    }

    private void ApplyCameraQualityOverrides(Camera camera)
    {
        if (camera == null || m_HasCameraQualityOverride)
        {
            return;
        }

        bool needsCameraMsaa = (m_UseRecordingMsaa && NormalizeMsaaSampleCount(m_RecordingMsaaSamples, 4) > 1) ||
            (m_OverrideUrpQuality && NormalizeMsaaSampleCount(m_RecordingUrpMsaaSamples, 4) > 1);
        if (!needsCameraMsaa)
        {
            return;
        }

        m_OverriddenCamera = camera;
        m_OriginalCameraAllowMsaa = camera.allowMSAA;
        m_HasCameraQualityOverride = true;
        camera.allowMSAA = true;
    }

    private void RestoreCameraQualityOverrides()
    {
        if (!m_HasCameraQualityOverride || m_OverriddenCamera == null)
        {
            m_HasCameraQualityOverride = false;
            m_OverriddenCamera = null;
            return;
        }

        m_OverriddenCamera.allowMSAA = m_OriginalCameraAllowMsaa;
        m_HasCameraQualityOverride = false;
        m_OverriddenCamera = null;
    }

    private string CreateScreenshotPath()
    {
        string outputDirectory = GetOutputDirectory();
        string safePrefix = GetSafeFilePrefix();

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{safePrefix}_{timestamp}.png";
        return Path.Combine(outputDirectory, fileName);
    }

    private void CreateVideoPaths(out string frameDirectory, out string videoPath)
    {
        string outputDirectory = GetOutputDirectory();
        string safePrefix = GetSafeFilePrefix();
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        frameDirectory = Path.Combine(outputDirectory, $"{safePrefix}_{timestamp}_frames");
        Directory.CreateDirectory(frameDirectory);

        videoPath = Path.Combine(outputDirectory, $"{safePrefix}_{timestamp}.mp4");
    }

    private string GetOutputDirectory()
    {
        string projectRootPath = Directory.GetParent(Application.dataPath)!.FullName;
        string safeFolderName = string.IsNullOrWhiteSpace(m_OutputFolderName) ? DefaultFolderName : m_OutputFolderName.Trim();
        string outputDirectory = Path.Combine(projectRootPath, safeFolderName);
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        return outputDirectory;
    }

    private string GetSafeFilePrefix()
    {
        return string.IsNullOrWhiteSpace(m_FilePrefix) ? "Screenshot" : m_FilePrefix.Trim();
    }

    private string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private bool SearchAndApplyFfmpegPath(bool showResultDialog)
    {
        if (TryResolveFfmpegPath(out string ffmpegPath, out string source))
        {
            m_FfmpegPath = ffmpegPath;
            SavePrefs();
            Debug.Log($"[ScreenshotTool] 已找到 ffmpeg.exe ({source}): {ffmpegPath}");
            if (showResultDialog)
            {
                EditorUtility.DisplayDialog("截图工具", $"已找到 ffmpeg.exe:\n{ffmpegPath}", "确定");
            }

            return true;
        }

        if (showResultDialog)
        {
            EditorUtility.DisplayDialog("截图工具", "未找到 ffmpeg.exe。请安装 ffmpeg，或手动指定 ffmpeg.exe 路径。", "确定");
        }

        return false;
    }

    private bool TryResolveFfmpegPath(out string ffmpegPath, out string source)
    {
        if (IsValidFfmpegPath(m_FfmpegPath))
        {
            ffmpegPath = m_FfmpegPath;
            source = "当前设置";
            return true;
        }

        if (TryFindFfmpegInProject(out ffmpegPath))
        {
            source = "项目目录";
            return true;
        }

        if (TryFindFfmpegInPath(out ffmpegPath))
        {
            source = "环境变量 PATH";
            return true;
        }

        if (TryFindFfmpegInCommonLocations(out ffmpegPath))
        {
            source = "常见安装位置";
            return true;
        }

        ffmpegPath = string.Empty;
        source = string.Empty;
        return false;
    }

    private bool IsValidFfmpegPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path) &&
            string.Equals(Path.GetFileName(path), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryFindFfmpegInProject(out string ffmpegPath)
    {
        string projectRootPath = Directory.GetParent(Application.dataPath)!.FullName;
        return TryFindFileByName(projectRootPath, "ffmpeg.exe", 6, out ffmpegPath);
    }

    private bool TryFindFfmpegInPath(out string ffmpegPath)
    {
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] pathEntries = pathValue.Split(Path.PathSeparator);
        foreach (string pathEntry in pathEntries)
        {
            if (string.IsNullOrWhiteSpace(pathEntry))
            {
                continue;
            }

            string candidate = Path.Combine(pathEntry.Trim(), "ffmpeg.exe");
            if (IsValidFfmpegPath(candidate))
            {
                ffmpegPath = candidate;
                return true;
            }
        }

        ffmpegPath = string.Empty;
        return false;
    }

    private bool TryFindFfmpegInCommonLocations(out string ffmpegPath)
    {
        List<string> roots = new List<string>();
        AddSearchRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddSearchRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddSearchRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddSearchRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddSearchRoot(roots, Environment.GetEnvironmentVariable("ChocolateyInstall"));
        AddSearchRoot(roots, Environment.GetEnvironmentVariable("SCOOP"));
        AddSearchRoot(roots, @"C:\ffmpeg");
        AddSearchRoot(roots, @"C:\tools");

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            AddSearchRoot(roots, Path.Combine(drive.RootDirectory.FullName, "ffmpeg"));
            AddSearchRoot(roots, Path.Combine(drive.RootDirectory.FullName, "tools"));
        }

        foreach (string root in roots)
        {
            if (TryFindFileByName(root, "ffmpeg.exe", 5, out ffmpegPath))
            {
                return true;
            }
        }

        ffmpegPath = string.Empty;
        return false;
    }

    private void AddSearchRoot(List<string> roots, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        string fullPath = Path.GetFullPath(root);
        foreach (string existingRoot in roots)
        {
            if (string.Equals(existingRoot, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        roots.Add(fullPath);
    }

    private bool TryFindFileByName(string root, string fileName, int maxDepth, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        Stack<SearchDirectory> pendingDirectories = new Stack<SearchDirectory>();
        pendingDirectories.Push(new SearchDirectory(root, 0));

        while (pendingDirectories.Count > 0)
        {
            SearchDirectory current = pendingDirectories.Pop();
            string candidate = Path.Combine(current.Path, fileName);
            if (IsValidFfmpegPath(candidate))
            {
                filePath = candidate;
                return true;
            }

            if (current.Depth >= maxDepth)
            {
                continue;
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(current.Path);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string childDirectory in childDirectories)
            {
                if (ShouldSkipFfmpegSearchDirectory(childDirectory))
                {
                    continue;
                }

                pendingDirectories.Push(new SearchDirectory(childDirectory, current.Depth + 1));
            }
        }

        return false;
    }

    private bool ShouldSkipFfmpegSearchDirectory(string directoryPath)
    {
        string directoryName = Path.GetFileName(directoryPath);
        return string.Equals(directoryName, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Library", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Temp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Build", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Builds", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directoryName, "Logs", StringComparison.OrdinalIgnoreCase);
    }

    private struct SearchDirectory
    {
        public readonly string Path;
        public readonly int Depth;

        public SearchDirectory(string path, int depth)
        {
            Path = path;
            Depth = depth;
        }
    }

    private void LoadPrefs()
    {
        m_OutputFolderName = EditorPrefs.GetString(PrefKeyPrefix + "OutputFolderName", DefaultFolderName);
        m_FilePrefix = EditorPrefs.GetString(PrefKeyPrefix + "FilePrefix", "Screenshot");
        m_Width = EditorPrefs.GetInt(PrefKeyPrefix + "Width", 1920);
        m_Height = EditorPrefs.GetInt(PrefKeyPrefix + "Height", 1080);
        m_SuperSize = EditorPrefs.GetInt(PrefKeyPrefix + "SuperSize", 1);
        m_RevealInExplorer = EditorPrefs.GetBool(PrefKeyPrefix + "RevealInExplorer", true);
        m_VideoFps = EditorPrefs.GetInt(PrefKeyPrefix + "VideoFps", DefaultVideoFps);
        m_VideoFixedDurationEnabled = EditorPrefs.GetBool(PrefKeyPrefix + "VideoFixedDurationEnabled", true);
        m_VideoDurationSeconds = EditorPrefs.GetFloat(PrefKeyPrefix + "VideoDurationSeconds", DefaultVideoDurationSeconds);
        m_FfmpegPath = EditorPrefs.GetString(PrefKeyPrefix + "FfmpegPath", string.Empty);
        m_KeepVideoFrames = EditorPrefs.GetBool(PrefKeyPrefix + "KeepVideoFrames", false);
        m_UseRecordingMsaa = EditorPrefs.GetBool(PrefKeyPrefix + "UseRecordingMsaa", true);
        m_RecordingMsaaSamples = NormalizeMsaaSampleCount(EditorPrefs.GetInt(PrefKeyPrefix + "RecordingMsaaSamples", 4), 4);
        m_OverrideUrpQuality = EditorPrefs.GetBool(PrefKeyPrefix + "OverrideUrpQuality", false);
        m_RecordingUrpRenderScale = Mathf.Clamp(EditorPrefs.GetFloat(PrefKeyPrefix + "RecordingUrpRenderScale", 1.25f), 0.5f, 2f);
        m_RecordingUrpMsaaSamples = NormalizeMsaaSampleCount(EditorPrefs.GetInt(PrefKeyPrefix + "RecordingUrpMsaaSamples", 4), 4);
        m_UseHighQualityEncoding = EditorPrefs.GetBool(PrefKeyPrefix + "UseHighQualityEncoding", true);
        m_VideoCrf = Mathf.Clamp(EditorPrefs.GetInt(PrefKeyPrefix + "VideoCrf", 16), 12, 28);
        m_FfmpegPreset = EditorPrefs.GetString(PrefKeyPrefix + "FfmpegPreset", "slow");
        m_MaxPendingReadbackFrames = Mathf.Clamp(EditorPrefs.GetInt(PrefKeyPrefix + "MaxPendingReadbackFrames", 2), 1, 8);
        if (Array.IndexOf(FfmpegPresetOptions, m_FfmpegPreset) < 0)
        {
            m_FfmpegPreset = "slow";
        }
    }

    private void SavePrefs()
    {
        EditorPrefs.SetString(PrefKeyPrefix + "OutputFolderName", m_OutputFolderName);
        EditorPrefs.SetString(PrefKeyPrefix + "FilePrefix", m_FilePrefix);
        EditorPrefs.SetInt(PrefKeyPrefix + "Width", m_Width);
        EditorPrefs.SetInt(PrefKeyPrefix + "Height", m_Height);
        EditorPrefs.SetInt(PrefKeyPrefix + "SuperSize", m_SuperSize);
        EditorPrefs.SetBool(PrefKeyPrefix + "RevealInExplorer", m_RevealInExplorer);
        EditorPrefs.SetInt(PrefKeyPrefix + "VideoFps", m_VideoFps);
        EditorPrefs.SetBool(PrefKeyPrefix + "VideoFixedDurationEnabled", m_VideoFixedDurationEnabled);
        EditorPrefs.SetFloat(PrefKeyPrefix + "VideoDurationSeconds", m_VideoDurationSeconds);
        EditorPrefs.SetString(PrefKeyPrefix + "FfmpegPath", m_FfmpegPath);
        EditorPrefs.SetBool(PrefKeyPrefix + "KeepVideoFrames", m_KeepVideoFrames);
        EditorPrefs.SetBool(PrefKeyPrefix + "UseRecordingMsaa", m_UseRecordingMsaa);
        EditorPrefs.SetInt(PrefKeyPrefix + "RecordingMsaaSamples", m_RecordingMsaaSamples);
        EditorPrefs.SetBool(PrefKeyPrefix + "OverrideUrpQuality", m_OverrideUrpQuality);
        EditorPrefs.SetFloat(PrefKeyPrefix + "RecordingUrpRenderScale", m_RecordingUrpRenderScale);
        EditorPrefs.SetInt(PrefKeyPrefix + "RecordingUrpMsaaSamples", m_RecordingUrpMsaaSamples);
        EditorPrefs.SetBool(PrefKeyPrefix + "UseHighQualityEncoding", m_UseHighQualityEncoding);
        EditorPrefs.SetInt(PrefKeyPrefix + "VideoCrf", m_VideoCrf);
        EditorPrefs.SetString(PrefKeyPrefix + "FfmpegPreset", m_FfmpegPreset);
        EditorPrefs.SetInt(PrefKeyPrefix + "MaxPendingReadbackFrames", m_MaxPendingReadbackFrames);
    }
}
