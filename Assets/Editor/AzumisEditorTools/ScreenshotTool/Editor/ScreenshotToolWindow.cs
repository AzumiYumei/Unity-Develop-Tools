// ================================================================
// 截图与录屏工具
//
// 文件名称: ScreenshotToolWindow.cs
// 版本: 1.8
//
// 描述: 提供 Game 视图截图、指定相机截图，以及基于 ffmpeg 的
// 指定相机录屏能力。录屏会将目标 Camera 渲染到独立的 RenderTexture，
// 再通过 ffmpeg 的 stdin 直接编码为 mp4，不依赖 GameView 窗口内容。
//
// 修改历史:
// 2026-06-26 - 补充脚本头部信息
// 2026-06-26 - 新增 ffmpeg 录屏、延迟录制、定时录制与停止录制功能
// 2026-06-26 - 修复 ffmpeg 浏览按钮目录打开异常，并新增录屏输出分辨率设置
// 2026-06-26 - 将录屏源改为指定 Camera 直接输出，移除对 GameView 的依赖
// 2026-07-03 - 新增录制期相机轨迹采样、轨迹预设保存，以及轨迹回放录屏功能
// 2026-07-03 - 将轨迹回放录制改为离线逐帧采集，解决实时录制掉帧导致的画面卡顿
// 2026-07-03 - 将轨迹采样从视频抓帧中解耦，并在保存前重采样为稳定时间步
// 2026-07-22 - 为实时录制增加帧缓冲复用与背压控制，降低 GC 和队列堆积导致的卡顿
// 2026-07-30 - 在工具窗口中显示当前录制时长，便于观察录制进度
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

public class ScreenshotToolWindow : EditorWindow
{
    private const string PrefKeyPrefix = "Azumi_ScreenshotTool_";
    private const string DefaultFolderName = "Screenshots";
    private const int RecordFrameRate = 60;
    private const int RecordQualityCrf = 23;
    private const string DefaultFilePrefix = "Screenshot";
    private const int MinTrajectoryKeyframeCount = 2;
    private const int TrajectorySampleRate = 120;
    private const double RecordFrameInterval = 1d / RecordFrameRate;
    private const int MaxPendingGpuReadbackCount = 2;
    private const int MaxQueuedRecordFrameCount = 2;

    private string m_OutputFolderName = DefaultFolderName;
    private string m_FilePrefix = DefaultFilePrefix;
    private int m_Width = 1920;
    private int m_Height = 1080;
    private int m_SuperSize = 1;
    private Camera m_TargetCamera;
    private bool m_RevealInExplorer = true;
    private string m_FfmpegExePath = string.Empty;
    private float m_RecordDurationSeconds = 10f;
    private float m_RecordDelaySeconds = 0f;
    private int m_RecordWidth = 1920;
    private int m_RecordHeight = 1080;
    private bool m_RecordCameraTrajectory = true;
    private Process m_RecordProcess;
    private bool m_IsRecordScheduled;
    private bool m_IsStoppingRecording;
    private bool m_IsRecordInputClosing;
    private double m_RecordStartTime;
    private double m_RecordEndTime;
    private double m_RecordNextFrameTime;
    private string m_PendingRecordOutputPath = string.Empty;
    private string m_RecordingOutputPath = string.Empty;
    private string m_LastRecordLogLine = string.Empty;
    private string m_RecordPipelineError = string.Empty;
    private Camera m_RecordingCamera;
    private RenderTexture m_RecordRenderTexture;
    private Thread m_RecordWriterThread;
    private readonly AutoResetEvent m_RecordFrameEvent = new AutoResetEvent(false);
    private readonly Queue<NativeArray<byte>> m_RecordFrameQueue = new Queue<NativeArray<byte>>();
    private readonly object m_RecordFrameQueueLock = new object();
    private volatile bool m_RecordWriterRunning;
    private volatile bool m_RecordWriterExited = true;
    private volatile bool m_IsWritingRecordFrame;
    private int m_PendingReadbackCount;
    private int m_RecordFrameByteSize;
    private int m_LastRenderedFrameCount;
    private float m_ActiveRecordDurationSeconds;
    private double m_RecordCaptureStartTime;
    private double m_TrajectoryNextSampleTime;
    private bool m_IsOfflinePlaybackRecording;
    private int m_OfflinePlaybackFrameIndex;
    private int m_OfflinePlaybackFrameCount;
    private CameraTrajectoryPreset m_SelectedTrajectoryPreset;
    private CameraTrajectoryPreset m_PendingTrajectoryPreset;
    private CameraTrajectoryPreset m_ActiveTrajectoryPreset;
    private readonly List<CameraTrajectoryKeyframe> m_RawRecordedTrajectoryKeyframes = new List<CameraTrajectoryKeyframe>();
    private readonly List<CameraTrajectoryKeyframe> m_LastRecordedTrajectoryKeyframes = new List<CameraTrajectoryKeyframe>();
    private string m_LastRecordedTrajectoryCameraName = string.Empty;
    private float m_LastRecordedTrajectoryDuration;
    private bool m_IsTrajectoryPlaybackActive;
    private Texture2D m_OfflinePlaybackReadTexture;
    private Vector3 m_PlaybackRestorePosition;
    private Quaternion m_PlaybackRestoreRotation = Quaternion.identity;
    private float m_PlaybackRestoreFieldOfView = 60f;
    private float m_PlaybackRestoreOrthographicSize = 5f;
    private bool m_PlaybackRestoreOrthographic;

    // ----------------------------------------------------------------
    // 机位平滑运动系统（与轨迹回放并存，UI 互斥切换）
    // ----------------------------------------------------------------
    private enum CameraMovementMode { Trajectory, Rig }
    private CameraMovementMode m_MovementMode = CameraMovementMode.Trajectory;
    private const int DefaultRigWaypointCount = 5;
    private List<Transform> m_RigWaypoints = new List<Transform>(new Transform[DefaultRigWaypointCount]);
    private Transform m_RigLookAtTarget;
    private float m_RigSmoothTime = 2f;
    private float m_RigMaxSpeed = float.PositiveInfinity;
    private float m_RigArrivalThreshold = 0.01f;
    private bool m_IsRigPreviewing;
    private double m_LastRigUpdateTime;
    private readonly CameraRigDriver m_RigDriver = new CameraRigDriver();

    [MenuItem("Tools/Azumi/截图工具")]
    public static void ShowWindow()
    {
        ScreenshotToolWindow window = GetWindow<ScreenshotToolWindow>("截图工具");
        window.minSize = new Vector2(460f, 600f);
        window.Show();
    }

    private void OnEnable()
    {
        LoadPrefs();
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        SavePrefs();
        CancelScheduledRecording();
        DisposeRecordingProcess(true);
    }

    private void OnGUI()
    {
        DrawSaveSettingsSection();

        EditorGUILayout.Space(8f);
        DrawGameScreenshotSection();

        EditorGUILayout.Space(8f);
        DrawCameraScreenshotSection();

        EditorGUILayout.Space(8f);
        DrawRecordSection();
    }

    private void DrawSaveSettingsSection()
    {
        EditorGUILayout.LabelField("保存设置", EditorStyles.boldLabel);
        m_OutputFolderName = EditorGUILayout.TextField("输出目录(项目根目录下)", m_OutputFolderName);
        m_FilePrefix = EditorGUILayout.TextField("文件名前缀", m_FilePrefix);
        m_RevealInExplorer = EditorGUILayout.Toggle("完成后打开目录", m_RevealInExplorer);
    }

    private void DrawGameScreenshotSection()
    {
        EditorGUILayout.LabelField("Game视图截图", EditorStyles.boldLabel);
        m_SuperSize = EditorGUILayout.IntSlider("放大倍数", m_SuperSize, 1, 4);
        if (GUILayout.Button("截取 Game 视图", GUILayout.Height(26f)))
        {
            CaptureGameView();
        }
    }

    private void DrawCameraScreenshotSection()
    {
        EditorGUILayout.LabelField("相机截图", EditorStyles.boldLabel);
        m_TargetCamera = (Camera)EditorGUILayout.ObjectField("目标相机", m_TargetCamera, typeof(Camera), true);
        m_Width = Mathf.Max(1, EditorGUILayout.IntField("宽度", m_Width));
        m_Height = Mathf.Max(1, EditorGUILayout.IntField("高度", m_Height));

        if (GUILayout.Button("截取目标相机", GUILayout.Height(26f)))
        {
            CaptureCamera();
        }
    }

    private void DrawRecordSection()
    {
        EditorGUILayout.LabelField("相机录屏", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "录屏将直接采集上方“目标相机”的渲染结果；若未指定，则回退到 Main Camera。录制尺寸为独立输出分辨率，不再依赖 GameView 窗口，也不会再因为窗口比例导致画面变形。",
            MessageType.Info);

        DrawFfmpegPathField();

        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled);
        m_RecordDurationSeconds = Mathf.Max(1f, EditorGUILayout.FloatField("录制时长(秒)", m_RecordDurationSeconds));
        m_RecordDelaySeconds = Mathf.Max(0f, EditorGUILayout.FloatField("延迟录制(秒)", m_RecordDelaySeconds));
        m_RecordWidth = EnsureEvenPositiveSize(EditorGUILayout.IntField("录制宽度", m_RecordWidth));
        m_RecordHeight = EnsureEvenPositiveSize(EditorGUILayout.IntField("录制高度", m_RecordHeight));
        EditorGUILayout.IntField("录制帧率", RecordFrameRate);
        m_RecordCameraTrajectory = EditorGUILayout.Toggle("录制时记录轨迹", m_RecordCameraTrajectory);
        EditorGUI.EndDisabledGroup();

        DrawMovementModeSection();
        DrawTrajectoryPresetSection();
        DrawRigConfigSection();
        DrawRecordStatus();
        DrawRecordButtons();
        DrawLastRecordedTrajectorySection();
    }

    private void DrawFfmpegPathField()
    {
        EditorGUILayout.BeginHorizontal();
        m_FfmpegExePath = EditorGUILayout.TextField("ffmpeg.exe 路径", m_FfmpegExePath);
        if (GUILayout.Button("浏览", GUILayout.Width(68f)))
        {
            string selectedPath = EditorUtility.OpenFilePanel("选择 ffmpeg.exe", GetFfmpegBrowseDirectory(), "exe");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                m_FfmpegExePath = selectedPath;
                GUI.FocusControl(null);
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawRecordStatus()
    {
        Camera previewCamera = ResolveTargetCamera(false);
        string cameraName = previewCamera != null ? previewCamera.name : "未指定";
        string scheduledModeText = m_MovementMode == CameraMovementMode.Rig
            ? "机位平滑录制"
            : (m_PendingTrajectoryPreset != null ? $"轨迹回放录制: {m_PendingTrajectoryPreset.name}" : "直接相机录制");
        string activeModeText = m_MovementMode == CameraMovementMode.Rig
            ? "机位平滑录制"
            : (m_ActiveTrajectoryPreset != null ? $"轨迹回放录制: {m_ActiveTrajectoryPreset.name}" : "直接相机录制");


        if (m_IsRecordScheduled)
        {
            float remainSeconds = Mathf.Max(0f, (float)(m_RecordStartTime - EditorApplication.timeSinceStartup));
            EditorGUILayout.HelpBox(
                $"录屏已排队，将在 {remainSeconds:F1} 秒后开始。模式: {scheduledModeText}，录制相机: {cameraName}，输出文件: {Path.GetFileName(m_PendingRecordOutputPath)}",
                MessageType.Warning);
            return;
        }

        if (IsRecordingActive())
        {
            float elapsedSeconds = GetDisplayedRecordElapsedSeconds();
            EditorGUILayout.HelpBox(
                $"正在录制相机: {cameraName}，模式: {activeModeText}，当前录制时长: {elapsedSeconds:F1} / {m_ActiveRecordDurationSeconds:F1} 秒，输出文件: {Path.GetFileName(m_RecordingOutputPath)}",
                MessageType.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(m_RecordingOutputPath) && File.Exists(m_RecordingOutputPath))
        {
            EditorGUILayout.HelpBox(
                $"最近一次录制文件: {Path.GetFileName(m_RecordingOutputPath)}",
                MessageType.None);
        }
    }

    private void DrawTrajectoryPresetSection()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("轨迹预设", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "开始录制时默认会按当前录制帧率采样目标相机轨迹。录制结束后可以将最近一次轨迹保存为预设，并支持一键按预设回放运镜同时再次录制输出视频。轨迹回放录制会使用离线逐帧采集，录制过程可能比实时慢，但成片更稳定。",
            MessageType.None);

        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled);
        m_SelectedTrajectoryPreset = (CameraTrajectoryPreset)EditorGUILayout.ObjectField("回放预设", m_SelectedTrajectoryPreset, typeof(CameraTrajectoryPreset), false);
        EditorGUI.EndDisabledGroup();
    }

    private void DrawMovementModeSection()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("运动模式", EditorStyles.boldLabel);
        CameraMovementMode previousMode = m_MovementMode;
        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled || m_IsRigPreviewing);
        m_MovementMode = (CameraMovementMode)EditorGUILayout.EnumPopup("当前模式", m_MovementMode);
        EditorGUI.EndDisabledGroup();
        if (previousMode != m_MovementMode && m_IsRigPreviewing)
        {
            StopRigPreview();
        }
    }

    private void DrawRigConfigSection()
    {
        if (m_MovementMode != CameraMovementMode.Rig)
        {
            return;
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("机位配置", EditorStyles.boldLabel);

        // 机位列表
        for (int i = 0; i < m_RigWaypoints.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"机位 {i + 1}", GUILayout.Width(60f));
            Transform newWaypoint = (Transform)EditorGUILayout.ObjectField(m_RigWaypoints[i], typeof(Transform), true);
            if (newWaypoint != m_RigWaypoints[i])
            {
                m_RigWaypoints[i] = newWaypoint;
            }
            if (m_RigWaypoints[i] == null)
            {
                EditorGUILayout.LabelField("失效", EditorStyles.miniLabel, GUILayout.Width(30f));
            }
            EditorGUILayout.EndHorizontal();
        }

        // 添加/移除机位按钮
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ 添加机位", GUILayout.Width(100f)))
        {
            m_RigWaypoints.Add(null);
        }
        EditorGUI.BeginDisabledGroup(m_RigWaypoints.Count <= 2);
        if (GUILayout.Button("- 移除末尾", GUILayout.Width(100f)))
        {
            m_RigWaypoints.RemoveAt(m_RigWaypoints.Count - 1);
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        // LookAt 目标
        m_RigLookAtTarget = (Transform)EditorGUILayout.ObjectField("LookAt 目标", m_RigLookAtTarget, typeof(Transform), true);

        // 参数
        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled || m_IsRigPreviewing);
        m_RigSmoothTime = Mathf.Max(0.01f, EditorGUILayout.FloatField("平滑时间", m_RigSmoothTime));
        m_RigMaxSpeed = EditorGUILayout.FloatField("最大速度(∞=无上限)", m_RigMaxSpeed);
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.HelpBox(
            $"运动路径: 机位1→2→...→{m_RigWaypoints.Count}→1 结束。录制时长字段作为最大安全上限，机位回到起点会提前停止。",
            MessageType.None);
    }

    private bool TryValidateRigConfig(out string error)
    {
        error = null;
        int validCount = 0;
        foreach (Transform wp in m_RigWaypoints)
        {
            if (wp != null) validCount++;
        }
        if (validCount < 2) { error = "至少需要 2 个有效机位"; return false; }
        if (m_RigLookAtTarget == null) { error = "LookAt 目标未设置"; return false; }
        if (m_RigSmoothTime <= 0f) { error = "平滑时间必须 > 0"; return false; }
        return true;
    }
    private void StartRigPreview()
    {
        if (!TryValidateRigConfig(out string error))
        {
            EditorUtility.DisplayDialog("机位预览", error, "确定");
            return;
        }

        Camera previewCamera = ResolveTargetCamera(true);
        if (previewCamera == null)
        {
            return;
        }

        // 备份相机姿态
        m_PlaybackRestorePosition = previewCamera.transform.position;
        m_PlaybackRestoreRotation = previewCamera.transform.rotation;
        m_PlaybackRestoreFieldOfView = previewCamera.fieldOfView;
        m_PlaybackRestoreOrthographic = previewCamera.orthographic;
        m_PlaybackRestoreOrthographicSize = previewCamera.orthographicSize;

        m_RecordingCamera = previewCamera;
        m_RigDriver.Begin(previewCamera, m_RigWaypoints, m_RigLookAtTarget, m_RigSmoothTime, m_RigMaxSpeed);
        m_LastRigUpdateTime = EditorApplication.timeSinceStartup;
        m_IsRigPreviewing = true;
    }

    private void StopRigPreview()
    {
        if (!m_IsRigPreviewing)
        {
            return;
        }

        m_IsRigPreviewing = false;
        m_RigDriver.Reset();

        // 恢复相机姿态
        if (m_RecordingCamera != null)
        {
            m_RecordingCamera.transform.SetPositionAndRotation(m_PlaybackRestorePosition, m_PlaybackRestoreRotation);
            m_RecordingCamera.orthographic = m_PlaybackRestoreOrthographic;
            if (m_PlaybackRestoreOrthographic)
            {
                m_RecordingCamera.orthographicSize = m_PlaybackRestoreOrthographicSize;
            }
            else
            {
                m_RecordingCamera.fieldOfView = m_PlaybackRestoreFieldOfView;
            }
            m_RecordingCamera = null;
        }
    }

    private void DrawRecordButtons()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled || m_IsRigPreviewing);
        if (GUILayout.Button("开始录制相机", GUILayout.Height(28f)))
        {
            ScheduleRecording(null);
        }

        EditorGUI.EndDisabledGroup();

        // 回放轨迹按钮在机位模式下灰显
        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled || m_IsRigPreviewing || m_MovementMode == CameraMovementMode.Rig || !HasValidTrajectoryPreset(m_SelectedTrajectoryPreset));
        if (GUILayout.Button("回放轨迹并录制", GUILayout.Height(28f)))
        {
            ScheduleRecording(m_SelectedTrajectoryPreset);
        }

        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();



        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(!m_IsRecordScheduled);
        if (GUILayout.Button("取消延迟录制", GUILayout.Height(28f)))
        {
            CancelScheduledRecording();
        }

        EditorGUI.EndDisabledGroup();
        EditorGUI.BeginDisabledGroup(!IsRecordingActive());
        if (GUILayout.Button("停止录制", GUILayout.Height(28f)))
        {
            StopRecording();
        }

        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        // 机位模式预览按钮（独立于录制）
        if (m_MovementMode == CameraMovementMode.Rig)
        {
            EditorGUILayout.Space(4f);
            EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled || Application.isPlaying);
            if (!m_IsRigPreviewing)
            {
                if (GUILayout.Button("预览机位运动", GUILayout.Height(26f)))
                {
                    StartRigPreview();
                }
            }
            else
            {
                if (GUILayout.Button("停止预览", GUILayout.Height(26f)))
                {
                    StopRigPreview();
                }
            }
            EditorGUI.EndDisabledGroup();
        }
    }

    private void DrawLastRecordedTrajectorySection()
    {
        if (!HasRecordableTrajectoryCache())
        {
            return;
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.HelpBox(
            $"最近一次轨迹缓存: 相机 {m_LastRecordedTrajectoryCameraName}，时长 {m_LastRecordedTrajectoryDuration:F2} 秒，关键帧数 {m_LastRecordedTrajectoryKeyframes.Count}。",
            MessageType.Info);

        EditorGUI.BeginDisabledGroup(IsRecordingActive() || m_IsRecordScheduled);
        if (GUILayout.Button("保存最近录制轨迹预设", GUILayout.Height(26f)))
        {
            SaveLastRecordedTrajectoryPreset();
        }

        EditorGUI.EndDisabledGroup();
    }

    private void CaptureGameView()
    {
        string filePath = CreateOutputPath("png");
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
        Camera camera = ResolveTargetCamera(true);
        if (camera == null)
        {
            return;
        }

        string filePath = CreateOutputPath("png");
        RenderTexture rt = new RenderTexture(m_Width, m_Height, 24, RenderTextureFormat.ARGB32);
        Texture2D tex = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false);

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        try
        {
            camera.targetTexture = rt;
            RenderTexture.active = rt;
            camera.Render();
            tex.ReadPixels(new Rect(0, 0, m_Width, m_Height), 0, 0);
            tex.Apply(false, false);
            byte[] pngData = tex.EncodeToPNG();
            File.WriteAllBytes(filePath, pngData);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            DestroyImmediate(rt);
            DestroyImmediate(tex);
        }

        AssetDatabase.Refresh();
        if (m_RevealInExplorer)
        {
            EditorUtility.RevealInFinder(filePath);
        }

        Debug.Log($"[ScreenshotTool] 已保存相机截图: {filePath}");
    }

    private void ScheduleRecording(CameraTrajectoryPreset trajectoryPreset)
    {
        if (IsRecordingActive() || m_IsRecordScheduled)
        {
            return;
        }

        bool requiresAsyncGpuReadback = trajectoryPreset == null;
        if (!ValidateFfmpegPath(requiresAsyncGpuReadback))
        {
            return;
        }

        if (trajectoryPreset != null && !ValidateTrajectoryPreset(trajectoryPreset, true))
        {
            return;
        }

        Camera recordCamera = ResolveTargetCamera(true);
        if (recordCamera == null)
        {
            return;
        }

        string outputPath = CreateOutputPath("mp4");
        m_PendingTrajectoryPreset = trajectoryPreset;
        if (m_RecordDelaySeconds <= 0f)
        {
            StartRecording(outputPath, trajectoryPreset);
            return;
        }

        m_IsRecordScheduled = true;
        m_RecordStartTime = EditorApplication.timeSinceStartup + m_RecordDelaySeconds;
        m_PendingRecordOutputPath = outputPath;
        string scheduleMode = trajectoryPreset != null ? $"轨迹回放录制({trajectoryPreset.name})" : "直接相机录制";
        Debug.Log($"[ScreenshotTool] {scheduleMode}任务已排队，将在 {m_RecordDelaySeconds:F1} 秒后开始。相机: {recordCamera.name}，输出: {outputPath}");
    }

    private void StartRecording(string outputPath, CameraTrajectoryPreset trajectoryPreset)
    {
        Camera recordCamera = ResolveTargetCamera(true);
        if (recordCamera == null)
        {
            m_IsRecordScheduled = false;
            m_PendingRecordOutputPath = string.Empty;
            m_PendingTrajectoryPreset = null;
            return;
        }

        int recordWidth = EnsureEvenPositiveSize(m_RecordWidth);
        int recordHeight = EnsureEvenPositiveSize(m_RecordHeight);
        float activeRecordDurationSeconds = trajectoryPreset != null ? Mathf.Max(0.01f, trajectoryPreset.Duration) : Mathf.Max(1f, m_RecordDurationSeconds);

        try
        {
            ResetRecordingState();
            PrepareRecordResources(recordCamera, recordWidth, recordHeight);
            BeginTrajectoryRecordingSession(recordCamera);
            if (m_MovementMode == CameraMovementMode.Rig)
            {
                // 机位模式：备份相机姿态 + 启动 RigDriver（不走轨迹回放）
                m_PlaybackRestorePosition = recordCamera.transform.position;
                m_PlaybackRestoreRotation = recordCamera.transform.rotation;
                m_PlaybackRestoreFieldOfView = recordCamera.fieldOfView;
                m_PlaybackRestoreOrthographic = recordCamera.orthographic;
                m_PlaybackRestoreOrthographicSize = recordCamera.orthographicSize;
                m_RigDriver.Begin(recordCamera, m_RigWaypoints, m_RigLookAtTarget, m_RigSmoothTime, m_RigMaxSpeed);
                m_LastRigUpdateTime = EditorApplication.timeSinceStartup;
                PrepareOfflinePlaybackRecording(null, recordWidth, recordHeight, activeRecordDurationSeconds);
            }
            else
            {
                BeginTrajectoryPlayback(recordCamera, trajectoryPreset);
                PrepareOfflinePlaybackRecording(trajectoryPreset, recordWidth, recordHeight, activeRecordDurationSeconds);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = m_FfmpegExePath,
                Arguments = BuildFfmpegArguments(recordWidth, recordHeight, outputPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            m_RecordProcess = new Process();
            m_RecordProcess.StartInfo = startInfo;
            m_RecordProcess.EnableRaisingEvents = true;
            m_RecordProcess.OutputDataReceived += OnFfmpegDataReceived;
            m_RecordProcess.ErrorDataReceived += OnFfmpegDataReceived;
            m_RecordProcess.Start();
            m_RecordProcess.BeginOutputReadLine();
            m_RecordProcess.BeginErrorReadLine();

            StartRecordWriterThread();

            double startTime = EditorApplication.timeSinceStartup;
            m_RecordingOutputPath = outputPath;
            m_IsRecordScheduled = false;
            m_PendingRecordOutputPath = string.Empty;
            m_PendingTrajectoryPreset = null;
            m_ActiveRecordDurationSeconds = activeRecordDurationSeconds;
            m_RecordCaptureStartTime = startTime;
            m_RecordEndTime = startTime + activeRecordDurationSeconds;
            m_RecordNextFrameTime = startTime + RecordFrameInterval;

            if (!m_IsOfflinePlaybackRecording)
            {
                CaptureRealtimeRecordFrame();
            }

            string modeText = trajectoryPreset != null ? $"轨迹回放录制({trajectoryPreset.name})" : "直接相机录制";
            Debug.Log($"[ScreenshotTool] 已开始{modeText}，相机 {recordCamera.name}，分辨率 {recordWidth}x{recordHeight}，时长 {activeRecordDurationSeconds:F1} 秒，输出: {outputPath}");
        }
        catch (Exception ex)
        {
            DisposeRecordingProcess(true);
            m_IsRecordScheduled = false;
            m_PendingRecordOutputPath = string.Empty;
            m_PendingTrajectoryPreset = null;
            EditorUtility.DisplayDialog("截图工具", $"启动相机录屏失败:\n{ex.Message}", "确定");
        }
    }

    private void StopRecording()
    {
        if (!IsRecordingActive())
        {
            return;
        }

        RequestStopRecording();
    }

    private void RequestStopRecording()
    {
        if (m_IsStoppingRecording)
        {
            return;
        }

        FinalizeTrajectoryRecordingCache();
        m_IsStoppingRecording = true;
        Debug.Log("[ScreenshotTool] 已请求停止录制，正在等待剩余帧写入完成。");
    }

    private void CancelScheduledRecording()
    {
        if (!m_IsRecordScheduled)
        {
            return;
        }

        Debug.Log("[ScreenshotTool] 已取消延迟录屏任务。");
        m_IsRecordScheduled = false;
        m_PendingRecordOutputPath = string.Empty;
        m_PendingTrajectoryPreset = null;
        m_RecordStartTime = 0d;
    }

    private void OnEditorUpdate()
    {
        double currentTime = EditorApplication.timeSinceStartup;

        if (m_IsRecordScheduled && currentTime >= m_RecordStartTime)
        {
            StartRecording(m_PendingRecordOutputPath, m_PendingTrajectoryPreset);
        }

        if (m_IsRecordScheduled || IsRecordingActive())
        {
            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
            if (Time.frameCount % 5 == 0)
            {
                Repaint();
            }
            // 机位预览驱动（不录制，只移动相机观察运镜效果）
            if (m_IsRigPreviewing)
            {
                if (Application.isPlaying)
                {
                    StopRigPreview();
                }
                else
                {
                    float deltaTime = Mathf.Clamp((float)(currentTime - m_LastRigUpdateTime), 0f, 0.1f);
                    m_LastRigUpdateTime = currentTime;
                    m_RigDriver.Update(deltaTime);
                    if (m_RigDriver.IsFinished)
                    {
                        StopRigPreview();
                    }
                }
            }
        }

        if (IsRecordingActive())
        {
            UpdateTrajectorySampling(currentTime);

            if (!string.IsNullOrWhiteSpace(m_RecordPipelineError))
            {
                RequestStopRecording();
            }
            else if (m_IsOfflinePlaybackRecording)
            {
                UpdateOfflinePlaybackRecording();
                // 机位模式下离线录制由 IsFinished 提前停止
                if (m_MovementMode == CameraMovementMode.Rig && m_RigDriver.IsFinished)
                {
                    RequestStopRecording();
                }
            }
            else
            {
                // 机位模式下由 IsFinished 决定停止，否则用 m_RecordEndTime
                bool shouldStopByRig = m_MovementMode == CameraMovementMode.Rig && m_RigDriver.IsFinished;
                if (!m_IsStoppingRecording && (currentTime >= m_RecordEndTime || shouldStopByRig))
                {
                    RequestStopRecording();
                }

                if (!m_IsStoppingRecording && currentTime >= m_RecordNextFrameTime && CanCaptureRealtimeRecordFrame())
                {
                    // Play 模式下同一渲染帧不重复 Render，避免 GPU 双倍渲染；编辑模式无 frameCount
                    bool isSamePlayFrame = Application.isPlaying && m_LastRenderedFrameCount == Time.frameCount;
                    if (!isSamePlayFrame)
                    {
                        CaptureRealtimeRecordFrame();
                        m_LastRenderedFrameCount = Time.frameCount;
                    }
                    // 时间步无论是否实际渲染都推进，避免 CPU 空转
                    m_RecordNextFrameTime += RecordFrameInterval;
                    if (m_RecordNextFrameTime < currentTime - RecordFrameInterval)
                    {
                        m_RecordNextFrameTime = currentTime + RecordFrameInterval;
                    }
                }
            }

            TryCloseRecordInput();
        }

        if (m_RecordProcess != null && m_RecordProcess.HasExited)
        {
            FinalizeRecording();
        }
    }

    /// <summary>
    /// 将轨迹采样独立于视频抓帧单独执行。
    /// 这样即使实时录屏因为高分辨率而掉帧，轨迹本身也仍可按更稳定的时间步被记录。
    /// </summary>
    /// <param name="currentTime">当前编辑器时间。</param>
    private void UpdateTrajectorySampling(double currentTime)
    {
        if (!m_RecordCameraTrajectory || m_RecordingCamera == null || m_RecordCaptureStartTime <= 0d)
        {
            return;
        }

        if (m_IsOfflinePlaybackRecording)
        {
            return;
        }

        double sampleInterval = 1d / TrajectorySampleRate;
        if (m_TrajectoryNextSampleTime <= 0d)
        {
            m_TrajectoryNextSampleTime = m_RecordCaptureStartTime;
        }

        while (currentTime >= m_TrajectoryNextSampleTime)
        {
            float sampleTime = Mathf.Clamp((float)(m_TrajectoryNextSampleTime - m_RecordCaptureStartTime), 0f, Mathf.Max(0f, m_ActiveRecordDurationSeconds));
            RecordRawTrajectorySample(sampleTime);
            m_TrajectoryNextSampleTime += sampleInterval;
        }
    }

    private void FinalizeRecording()
    {
        if (m_RecordProcess == null)
        {
            return;
        }

        int exitCode = m_RecordProcess.ExitCode;
        string outputPath = m_RecordingOutputPath;
        string pipelineError = m_RecordPipelineError;
        DisposeRecordingProcess(false);
        AssetDatabase.Refresh();

        if (exitCode == 0 && string.IsNullOrWhiteSpace(pipelineError) && File.Exists(outputPath))
        {
            Debug.Log($"[ScreenshotTool] 相机录屏完成: {outputPath}");
            if (m_RevealInExplorer)
            {
                EditorUtility.RevealInFinder(outputPath);
            }
        }
        else
        {
            Debug.LogError($"[ScreenshotTool] 相机录屏失败，ffmpeg 退出码: {exitCode}。流水线错误: {pipelineError}。最后日志: {m_LastRecordLogLine}");
        }
    }

    /// <summary>
    /// 普通实时录制模式下按当前编辑器节奏抓取一帧。
    /// 该模式仍依赖编辑器实时刷新频率，因此更适合快速直录。
    /// </summary>
    private void CaptureRealtimeRecordFrame()
    {
        if (m_RecordProcess == null || m_RecordRenderTexture == null || m_RecordingCamera == null)
        {
            return;
        }

        if (m_MovementMode == CameraMovementMode.Rig)
        {
            float deltaTime = Mathf.Clamp((float)(EditorApplication.timeSinceStartup - m_LastRigUpdateTime), 0f, 0.1f);
            m_RigDriver.Update(deltaTime);
            m_LastRigUpdateTime = EditorApplication.timeSinceStartup;
        }
        else
        {
            float currentRecordTime = GetCurrentRecordElapsedSeconds();
            ApplyPlaybackTrajectory(currentRecordTime);
        }

        RenderTexture previousTarget = m_RecordingCamera.targetTexture;
        try
        {
            m_RecordingCamera.targetTexture = m_RecordRenderTexture;
            m_RecordingCamera.Render();
            m_PendingReadbackCount++;
            NativeArray<byte> targetArray = new NativeArray<byte>(
              m_RecordFrameByteSize, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            AsyncGPUReadback.RequestIntoNativeArray(ref targetArray, m_RecordRenderTexture, 0, TextureFormat.RGBA32,
                                                    request => OnRecordFrameReadback(request, targetArray));
        }
        catch (Exception ex)
        {
            m_PendingReadbackCount = Mathf.Max(0, m_PendingReadbackCount - 1);
            m_RecordPipelineError = $"采集相机帧失败: {ex.Message}";
        }
        finally
        {
            m_RecordingCamera.targetTexture = previousTarget;
        }
    }

    /// <summary>
    /// 检查实时录制链路是否还有可安全推进的新帧容量。
    /// 通过限制 GPU 回读中的帧数与待写入队列深度，避免主线程持续堆积大块内存，
    /// 从而减轻录制过程中因为 GC 和写入跟不上带来的明显卡顿。
    /// </summary>
    /// <returns>链路还有余量时返回 true。</returns>
    private bool CanCaptureRealtimeRecordFrame()
    {
        if (m_PendingReadbackCount >= MaxPendingGpuReadbackCount)
        {
            return false;
        }

        lock (m_RecordFrameQueueLock)
        {
            return m_RecordFrameQueue.Count < MaxQueuedRecordFrameCount;
        }
    }

    /// <summary>
    /// 初始化轨迹回放录制的离线逐帧采样资源。
    /// 只有在选择了轨迹预设时才启用，普通录制仍保持原有实时模式。
    /// </summary>
    /// <param name="trajectoryPreset">当前是否为轨迹回放录制。</param>
    /// <param name="recordWidth">输出宽度。</param>
    /// <param name="recordHeight">输出高度。</param>
    /// <param name="recordDurationSeconds">本次录制总时长。</param>
    private void PrepareOfflinePlaybackRecording(CameraTrajectoryPreset trajectoryPreset, int recordWidth, int recordHeight, float recordDurationSeconds)
    {
        m_IsOfflinePlaybackRecording = trajectoryPreset != null || m_MovementMode == CameraMovementMode.Rig;
        m_OfflinePlaybackFrameIndex = 0;
        m_OfflinePlaybackFrameCount = 0;

        if (!m_IsOfflinePlaybackRecording)
        {
            return;
        }

        m_OfflinePlaybackFrameCount = Mathf.Max(1, Mathf.CeilToInt(recordDurationSeconds * RecordFrameRate));
        m_OfflinePlaybackReadTexture = new Texture2D(recordWidth, recordHeight, TextureFormat.RGBA32, false, false)
        {
            name = "Azumi_ScreenshotTool_OfflineReadTexture"
        };
    }

    /// <summary>
    /// 离线逐帧录制驱动。
    /// 每次 Editor 更新只推进一个固定帧编号，不依赖真实时间，从而避免回放录制时的抖动和跳帧。
    /// </summary>
    private void UpdateOfflinePlaybackRecording()
    {
        if (m_IsStoppingRecording)
        {
            return;
        }

        if (m_OfflinePlaybackFrameIndex >= m_OfflinePlaybackFrameCount)
        {
            FinalizeTrajectoryRecordingCache();
            RequestStopRecording();
            return;
        }

        CaptureOfflinePlaybackFrame(m_OfflinePlaybackFrameIndex);
        m_OfflinePlaybackFrameIndex++;

        if (!string.IsNullOrWhiteSpace(m_RecordPipelineError))
        {
            RequestStopRecording();
            return;
        }

        if (m_OfflinePlaybackFrameIndex >= m_OfflinePlaybackFrameCount)
        {
            FinalizeTrajectoryRecordingCache();
            RequestStopRecording();
        }
    }

    /// <summary>
    /// 按固定帧编号采样轨迹并同步输出一帧原始视频数据。
    /// 即使编辑器当前刷新很慢，输出视频仍然严格保持目标帧率的时间步进。
    /// </summary>
    /// <param name="frameIndex">当前要录制的目标帧编号。</param>
    private void CaptureOfflinePlaybackFrame(int frameIndex)
    {
        if (m_RecordProcess == null || m_RecordRenderTexture == null || m_RecordingCamera == null || m_OfflinePlaybackReadTexture == null)
        {
            return;
        }

        if (m_MovementMode == CameraMovementMode.Rig)
        {
            m_RigDriver.Update(1f / RecordFrameRate);
        }
        else
        {
            float recordTime = Mathf.Min(m_ActiveRecordDurationSeconds, frameIndex / (float)RecordFrameRate);
            ApplyPlaybackTrajectory(recordTime);
            RecordRawTrajectorySample(recordTime);
        }

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = m_RecordingCamera.targetTexture;
        try
        {
            m_RecordingCamera.targetTexture = m_RecordRenderTexture;
            RenderTexture.active = m_RecordRenderTexture;
            m_RecordingCamera.Render();
            m_OfflinePlaybackReadTexture.ReadPixels(new Rect(0f, 0f, m_RecordRenderTexture.width, m_RecordRenderTexture.height), 0, 0, false);
            m_OfflinePlaybackReadTexture.Apply(false, false);

            NativeArray<byte> sourceData = m_OfflinePlaybackReadTexture.GetRawTextureData<byte>();
            NativeArray<byte> frameData = new NativeArray<byte>(sourceData.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            sourceData.CopyTo(frameData);
            EnqueueRecordFrame(frameData);
        }
        catch (Exception ex)
        {
            m_RecordPipelineError = $"离线采集相机帧失败: {ex.Message}";
        }
        finally
        {
            m_RecordingCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
        }
    }

    private void OnRecordFrameReadback(AsyncGPUReadbackRequest request, NativeArray<byte>
  frameData)
    {
        m_PendingReadbackCount = Mathf.Max(0, m_PendingReadbackCount - 1);

        if (request.hasError)
        {
            if (frameData.IsCreated) frameData.Dispose();
            m_RecordPipelineError = "GPU 回读失败，请检查当前平台是否支持 AsyncGPUReadback。";
            return;
        }

        if (m_RecordProcess == null)
        {
            if (frameData.IsCreated) frameData.Dispose();
            return;
        }

        try
        {
            EnqueueRecordFrame(frameData);
        }
        catch (Exception ex)
        {
            if (frameData.IsCreated) frameData.Dispose();
            m_RecordPipelineError = $"处理相机帧数据失败: {ex.Message}";
        }
    }

    private void EnqueueRecordFrame(NativeArray<byte> frameData)
    {
        lock (m_RecordFrameQueueLock)
        {
            m_RecordFrameQueue.Enqueue(frameData);
        }

        m_RecordFrameEvent.Set();
    }

    private void TryCloseRecordInput()
    {
        if (!m_IsStoppingRecording || m_IsRecordInputClosing)
        {
            return;
        }

        bool hasQueuedFrame;
        lock (m_RecordFrameQueueLock)
        {
            hasQueuedFrame = m_RecordFrameQueue.Count > 0;
        }

        if (m_PendingReadbackCount > 0 || hasQueuedFrame || m_IsWritingRecordFrame)
        {
            return;
        }

        m_IsRecordInputClosing = true;
        m_RecordWriterRunning = false;
        m_RecordFrameEvent.Set();
    }

    private void StartRecordWriterThread()
    {
        m_RecordWriterRunning = true;
        m_RecordWriterExited = false;
        m_RecordWriterThread = new Thread(RecordFrameWriterLoop)
        {
            IsBackground = true,
            Name = "AzumiCameraRecordWriter"
        };
        m_RecordWriterThread.Start();
    }

    private void RecordFrameWriterLoop()
    {
        byte[] frameBytes = null; // 循环外复用，避免每帧 LOH 分配
        try
        {
            while (true)
            {
                NativeArray<byte> frameData = default;
                bool hasFrame = false;
                lock (m_RecordFrameQueueLock)
                {
                    if (m_RecordFrameQueue.Count > 0)
                    {
                        frameData = m_RecordFrameQueue.Dequeue();
                        hasFrame = true;
                        m_IsWritingRecordFrame = true;
                    }
                    else if (!m_RecordWriterRunning)
                    {
                        break;
                    }
                }

                if (!hasFrame)
                {
                    m_RecordFrameEvent.WaitOne(100);
                    continue;
                }

                try
                {
                    if (m_RecordProcess != null && !m_RecordProcess.HasExited &&
  frameData.IsCreated)
                    {
                        if (frameBytes == null || frameBytes.Length < frameData.Length)
                        {
                            frameBytes = new byte[frameData.Length];
                        }
                        frameData.CopyTo(frameBytes);
                        if (m_RecordProcess != null && !m_RecordProcess.HasExited)
                        {
                            m_RecordProcess.StandardInput.BaseStream.Write(frameBytes, 0, frameData.Length);
                        }

                    }
                }
                catch (Exception ex)
                {
                    m_RecordPipelineError = $"写入 ffmpeg 视频帧失败: {ex.Message}";
                    m_RecordWriterRunning = false;
                }
                finally
                {
                    m_IsWritingRecordFrame = false;
                    if (frameData.IsCreated)
                    {
                        frameData.Dispose();
                    }
                }
            }

            try
            {
                if (m_RecordProcess != null && !m_RecordProcess.HasExited)
                {
                    m_RecordProcess.StandardInput.BaseStream.Flush();
                    m_RecordProcess.StandardInput.Close();
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(m_RecordPipelineError))
                {
                    m_RecordPipelineError = $"关闭 ffmpeg 输入流失败: {ex.Message}";
                }
            }
        }
        finally
        {
            m_IsWritingRecordFrame = false;
            m_RecordWriterExited = true;
        }
    }

    private void PrepareRecordResources(Camera recordCamera, int recordWidth, int recordHeight)
    {
        CleanupRecordResources();
        m_RecordingCamera = recordCamera;
        m_RecordFrameByteSize = recordWidth * recordHeight * 4;
        m_RecordRenderTexture = new RenderTexture(recordWidth, recordHeight, 24, RenderTextureFormat.ARGB32)
        {
            name = "Azumi_ScreenshotTool_RecordRT",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        m_RecordRenderTexture.Create();
    }

    private void CleanupRecordResources()
    {
        if (m_OfflinePlaybackReadTexture != null)
        {
            DestroyImmediate(m_OfflinePlaybackReadTexture);
            m_OfflinePlaybackReadTexture = null;
        }

        m_RecordingCamera = null;
        if (m_RecordRenderTexture != null)
        {
            m_RecordRenderTexture.Release();
            DestroyImmediate(m_RecordRenderTexture);
            m_RecordRenderTexture = null;
        }
    }

    private void ResetRecordingState()
    {
        m_IsStoppingRecording = false;
        m_IsRecordInputClosing = false;
        m_RecordEndTime = 0d;
        m_RecordNextFrameTime = 0d;
        m_RecordCaptureStartTime = 0d;
        m_TrajectoryNextSampleTime = 0d;
        m_PendingReadbackCount = 0;
        m_LastRecordLogLine = string.Empty;
        m_RecordPipelineError = string.Empty;
        m_RecordWriterRunning = false;
        m_RecordWriterExited = true;
        m_IsWritingRecordFrame = false;
        m_ActiveRecordDurationSeconds = 0f;
        m_RecordFrameByteSize = 0;
        m_IsOfflinePlaybackRecording = false;
        m_OfflinePlaybackFrameIndex = 0;
        m_OfflinePlaybackFrameCount = 0;
        m_RawRecordedTrajectoryKeyframes.Clear();

        lock (m_RecordFrameQueueLock)
        {
            while (m_RecordFrameQueue.Count > 0)
            {
                NativeArray<byte> frameData = m_RecordFrameQueue.Dequeue();
                if (frameData.IsCreated) frameData.Dispose();
            }

            m_RecordFrameQueue.Clear();
        }
        m_RigDriver.Reset();
        m_IsRigPreviewing = false;
    }

    /// <summary>
    /// 从缓冲池中复用一块与当前录制分辨率匹配的原始帧缓冲。
    /// 录屏每帧都是完整 RGBA 原始数据，如果持续 new 新数组会制造大量托管堆压力，
    /// 因此这里优先复用旧缓冲，尽量减少录制期的大对象分配。
    /// </summary>
    /// <param name="requiredLength">本帧所需字节数。</param>
    /// <returns>可写入当前帧数据的字节数组。</returns>
    /* private byte[] RentRecordFrameBuffer(int requiredLength)
    {
        lock (m_RecordFrameBufferPoolLock)
        {
            while (m_RecordFrameBufferPool.Count > 0)
            {
                byte[] cachedBuffer = m_RecordFrameBufferPool.Pop();
                if (cachedBuffer != null && cachedBuffer.Length == requiredLength)
                {
                    return cachedBuffer;
                }
            }
        }

        return new byte[requiredLength];
    } */

    /// <summary>
    /// 将已写入 ffmpeg 的帧缓冲归还到池中，供后续帧继续复用。
    /// 只缓存当前分辨率对应的缓冲区，避免切换分辨率后残留无效大数组。
    /// </summary>
    /// <param name="frameBytes">准备回收的帧缓冲。</param>
    /* private void ReturnRecordFrameBuffer(byte[] frameBytes)
    {
        if (frameBytes == null || m_RecordFrameByteSize <= 0 || frameBytes.Length != m_RecordFrameByteSize)
        {
            return;
        }

        lock (m_RecordFrameBufferPoolLock)
        {
            if (m_RecordFrameBufferPool.Count < MaxRecordFrameBufferPoolCount)
            {
                m_RecordFrameBufferPool.Push(frameBytes);
            }
        }
    } */

    /// <summary>
    /// 开始一轮新的轨迹采样缓存。
    /// 每次正式开始录制前都会重置最近一次轨迹结果，避免用户误保存旧数据。
    /// </summary>
    /// <param name="recordCamera">当前用于录制的相机。</param>
    private void BeginTrajectoryRecordingSession(Camera recordCamera)
    {
        m_RawRecordedTrajectoryKeyframes.Clear();
        m_LastRecordedTrajectoryKeyframes.Clear();
        m_LastRecordedTrajectoryCameraName = recordCamera != null ? recordCamera.name : string.Empty;
        m_LastRecordedTrajectoryDuration = 0f;
        m_TrajectoryNextSampleTime = 0d;

        if (!m_RecordCameraTrajectory || recordCamera == null)
        {
            return;
        }

        RecordRawTrajectorySample(0f);
    }

    /// <summary>
    /// 如果本次录制指定了轨迹预设，则提前记录相机原始状态并把相机切到轨迹起点。
    /// 这样录制结束后可以无侵入地恢复用户原本的场景相机姿态。
    /// </summary>
    /// <param name="recordCamera">即将被驱动回放的相机。</param>
    /// <param name="trajectoryPreset">待回放的轨迹预设，传空表示普通录制。</param>
    private void BeginTrajectoryPlayback(Camera recordCamera, CameraTrajectoryPreset trajectoryPreset)
    {
        m_ActiveTrajectoryPreset = null;
        m_IsTrajectoryPlaybackActive = false;

        if (recordCamera == null || trajectoryPreset == null)
        {
            return;
        }

        m_PlaybackRestorePosition = recordCamera.transform.position;
        m_PlaybackRestoreRotation = recordCamera.transform.rotation;
        m_PlaybackRestoreFieldOfView = recordCamera.fieldOfView;
        m_PlaybackRestoreOrthographic = recordCamera.orthographic;
        m_PlaybackRestoreOrthographicSize = recordCamera.orthographicSize;
        m_ActiveTrajectoryPreset = trajectoryPreset;
        m_IsTrajectoryPlaybackActive = true;
        ApplyPlaybackTrajectory(0f);
    }

    /// <summary>
    /// 在录制驱动循环中按当前时间对轨迹预设取样，并写回目标相机。
    /// </summary>
    /// <param name="recordTime">相对录制起点的时间。</param>
    private void ApplyPlaybackTrajectory(float recordTime)
    {
        if (!m_IsTrajectoryPlaybackActive || m_ActiveTrajectoryPreset == null || m_RecordingCamera == null)
        {
            return;
        }

        Vector3 position;
        Quaternion rotation;
        float fieldOfView;
        bool isOrthographic;
        float orthographicSize;
        m_ActiveTrajectoryPreset.Evaluate(recordTime, out position, out rotation, out fieldOfView, out isOrthographic, out orthographicSize);
        m_RecordingCamera.transform.SetPositionAndRotation(position, rotation);
        m_RecordingCamera.orthographic = isOrthographic;
        if (isOrthographic)
        {
            m_RecordingCamera.orthographicSize = orthographicSize;
        }
        else
        {
            m_RecordingCamera.fieldOfView = fieldOfView;
        }
    }

    /// <summary>
    /// 记录原始轨迹采样点。
    /// 录制阶段尽量保留更多原始时间点，后续再统一重采样为稳定轨迹。
    /// </summary>
    /// <param name="recordTime">相对录制起点的时间。</param>
    private void RecordRawTrajectorySample(float recordTime)
    {
        if (!m_RecordCameraTrajectory || m_RecordingCamera == null)
        {
            return;
        }

        CameraTrajectoryKeyframe keyframe = CameraTrajectoryKeyframe.CreateFromCamera(m_RecordingCamera, Mathf.Max(0f, recordTime));
        int lastIndex = m_RawRecordedTrajectoryKeyframes.Count - 1;
        if (lastIndex >= 0 && Mathf.Abs(m_RawRecordedTrajectoryKeyframes[lastIndex].m_Time - keyframe.m_Time) <= 0.0001f)
        {
            m_RawRecordedTrajectoryKeyframes[lastIndex] = keyframe;
        }
        else
        {
            m_RawRecordedTrajectoryKeyframes.Add(keyframe);
        }
    }

    /// <summary>
    /// 将原始轨迹采样点重采样为稳定的目标帧率轨迹，并写入最近一次录制缓存。
    /// 自动回放录制读取的就是这份缓存，从而避免原始实时录制掉帧直接污染最终运镜。
    /// </summary>
    private void FinalizeTrajectoryRecordingCache()
    {
        if (!m_RecordCameraTrajectory)
        {
            m_LastRecordedTrajectoryKeyframes.Clear();
            m_LastRecordedTrajectoryDuration = 0f;
            return;
        }

        float duration = Mathf.Max(0f, m_ActiveRecordDurationSeconds);
        if (m_RawRecordedTrajectoryKeyframes.Count == 0 && m_RecordingCamera != null)
        {
            RecordRawTrajectorySample(0f);
        }

        if (m_RawRecordedTrajectoryKeyframes.Count == 0)
        {
            m_LastRecordedTrajectoryKeyframes.Clear();
            m_LastRecordedTrajectoryDuration = 0f;
            return;
        }

        List<CameraTrajectoryKeyframe> resampledKeyframes = ResampleTrajectoryKeyframes(m_RawRecordedTrajectoryKeyframes, duration, RecordFrameRate);
        m_LastRecordedTrajectoryKeyframes.Clear();
        m_LastRecordedTrajectoryKeyframes.AddRange(resampledKeyframes);
        m_LastRecordedTrajectoryDuration = duration;
    }

    /// <summary>
    /// 按固定帧率对原始轨迹进行重采样。
    /// 对位置、旋转和镜头参数做线性/球形插值，得到时间分布稳定的回放轨迹。
    /// </summary>
    /// <param name="sourceKeyframes">原始采样点。</param>
    /// <param name="duration">轨迹时长。</param>
    /// <param name="sampleRate">目标采样帧率。</param>
    /// <returns>重采样后的关键帧列表。</returns>
    private List<CameraTrajectoryKeyframe> ResampleTrajectoryKeyframes(List<CameraTrajectoryKeyframe> sourceKeyframes, float duration, int sampleRate)
    {
        List<CameraTrajectoryKeyframe> result = new List<CameraTrajectoryKeyframe>();
        if (sourceKeyframes == null || sourceKeyframes.Count == 0)
        {
            return result;
        }

        sourceKeyframes.Sort(CompareTrajectoryKeyframeTime);
        if (sourceKeyframes.Count == 1 || duration <= 0f)
        {
            result.Add(CloneTrajectoryKeyframeAtTime(sourceKeyframes[0], 0f));
            return result;
        }

        int sampleCount = Mathf.Max(MinTrajectoryKeyframeCount, Mathf.RoundToInt(duration * Mathf.Max(1, sampleRate)) + 1);
        for (int index = 0; index < sampleCount; index++)
        {
            float normalized = sampleCount <= 1 ? 0f : index / (float)(sampleCount - 1);
            float sampleTime = Mathf.Lerp(0f, duration, normalized);
            result.Add(EvaluateTrajectoryKeyframe(sourceKeyframes, sampleTime));
        }

        return result;
    }

    /// <summary>
    /// 在原始轨迹上按时间采样单个关键帧。
    /// </summary>
    /// <param name="sourceKeyframes">原始采样点。</param>
    /// <param name="sampleTime">目标时间。</param>
    /// <returns>插值后的关键帧。</returns>
    private CameraTrajectoryKeyframe EvaluateTrajectoryKeyframe(List<CameraTrajectoryKeyframe> sourceKeyframes, float sampleTime)
    {
        if (sourceKeyframes == null || sourceKeyframes.Count == 0)
        {
            return new CameraTrajectoryKeyframe();
        }

        if (sourceKeyframes.Count == 1)
        {
            return CloneTrajectoryKeyframeAtTime(sourceKeyframes[0], sampleTime);
        }

        float clampedTime = Mathf.Clamp(sampleTime, 0f, Mathf.Max(sampleTime, sourceKeyframes[sourceKeyframes.Count - 1].m_Time));
        CameraTrajectoryKeyframe previousKeyframe = sourceKeyframes[0];
        CameraTrajectoryKeyframe nextKeyframe = sourceKeyframes[sourceKeyframes.Count - 1];

        for (int index = 1; index < sourceKeyframes.Count; index++)
        {
            CameraTrajectoryKeyframe currentKeyframe = sourceKeyframes[index];
            if (clampedTime <= currentKeyframe.m_Time)
            {
                nextKeyframe = currentKeyframe;
                break;
            }

            previousKeyframe = currentKeyframe;
        }

        if (ReferenceEquals(previousKeyframe, nextKeyframe) || Mathf.Approximately(previousKeyframe.m_Time, nextKeyframe.m_Time))
        {
            return CloneTrajectoryKeyframeAtTime(nextKeyframe, sampleTime);
        }

        float lerpFactor = Mathf.InverseLerp(previousKeyframe.m_Time, nextKeyframe.m_Time, clampedTime);
        return new CameraTrajectoryKeyframe
        {
            m_Time = sampleTime,
            m_Position = Vector3.LerpUnclamped(previousKeyframe.m_Position, nextKeyframe.m_Position, lerpFactor),
            m_Rotation = Quaternion.SlerpUnclamped(previousKeyframe.m_Rotation, nextKeyframe.m_Rotation, lerpFactor),
            m_FieldOfView = Mathf.LerpUnclamped(previousKeyframe.m_FieldOfView, nextKeyframe.m_FieldOfView, lerpFactor),
            m_IsOrthographic = lerpFactor < 0.5f ? previousKeyframe.m_IsOrthographic : nextKeyframe.m_IsOrthographic,
            m_OrthographicSize = Mathf.LerpUnclamped(previousKeyframe.m_OrthographicSize, nextKeyframe.m_OrthographicSize, lerpFactor)
        };
    }

    /// <summary>
    /// 复制关键帧并写入新的时间值。
    /// </summary>
    /// <param name="sourceKeyframe">源关键帧。</param>
    /// <param name="time">新时间。</param>
    /// <returns>复制后的关键帧。</returns>
    private CameraTrajectoryKeyframe CloneTrajectoryKeyframeAtTime(CameraTrajectoryKeyframe sourceKeyframe, float time)
    {
        return new CameraTrajectoryKeyframe
        {
            m_Time = time,
            m_Position = sourceKeyframe.m_Position,
            m_Rotation = sourceKeyframe.m_Rotation,
            m_FieldOfView = sourceKeyframe.m_FieldOfView,
            m_IsOrthographic = sourceKeyframe.m_IsOrthographic,
            m_OrthographicSize = sourceKeyframe.m_OrthographicSize
        };
    }

    /// <summary>
    /// 关键帧时间比较器，用于保证原始采样和重采样前的时间顺序稳定。
    /// </summary>
    /// <param name="left">左关键帧。</param>
    /// <param name="right">右关键帧。</param>
    /// <returns>时间比较结果。</returns>
    private int CompareTrajectoryKeyframeTime(CameraTrajectoryKeyframe left, CameraTrajectoryKeyframe right)
    {
        if (left == null && right == null)
        {
            return 0;
        }

        if (left == null)
        {
            return -1;
        }

        if (right == null)
        {
            return 1;
        }

        return left.m_Time.CompareTo(right.m_Time);
    }

    /// <summary>
    /// 获取当前录制已经进行的时间，供轨迹采样与回放共用。
    /// </summary>
    /// <returns>相对录制起点的秒数。</returns>
    private float GetCurrentRecordElapsedSeconds()
    {
        if (m_RecordCaptureStartTime <= 0d)
        {
            return 0f;
        }

        double elapsedSeconds = EditorApplication.timeSinceStartup - m_RecordCaptureStartTime;
        return Mathf.Clamp((float)elapsedSeconds, 0f, Mathf.Max(0f, m_ActiveRecordDurationSeconds));
    }

    /// <summary>
    /// 获取当前显示在工具窗口中的录制时长。
    /// 实时录制使用真实经过时间，离线逐帧录制则按已完成的帧编号换算时长，
    /// 这样用户在两种录制模式下都能看到准确、连续的进度反馈。
    /// </summary>
    /// <returns>当前已录制的秒数。</returns>
    private float GetDisplayedRecordElapsedSeconds()
    {
        if (m_IsOfflinePlaybackRecording)
        {
            float elapsedSeconds = m_OfflinePlaybackFrameIndex / (float)RecordFrameRate;
            return Mathf.Clamp(elapsedSeconds, 0f, Mathf.Max(0f, m_ActiveRecordDurationSeconds));
        }

        return GetCurrentRecordElapsedSeconds();
    }

    /// <summary>
    /// 判断最近一次录制缓存是否足够生成可回放的轨迹资产。
    /// </summary>
    /// <returns>关键帧数量与持续时间都满足回放要求时返回 true。</returns>
    private bool HasRecordableTrajectoryCache()
    {
        return m_LastRecordedTrajectoryKeyframes.Count >= MinTrajectoryKeyframeCount && m_LastRecordedTrajectoryDuration > 0f;
    }

    /// <summary>
    /// 判断预设是否包含可回放轨迹。
    /// </summary>
    /// <param name="trajectoryPreset">待检查的轨迹预设。</param>
    /// <returns>满足回放条件时返回 true。</returns>
    private bool HasValidTrajectoryPreset(CameraTrajectoryPreset trajectoryPreset)
    {
        return trajectoryPreset != null
            && trajectoryPreset.HasValidData()
            && trajectoryPreset.Keyframes != null
            && trajectoryPreset.Keyframes.Count >= MinTrajectoryKeyframeCount
            && trajectoryPreset.Duration > 0f;
    }

    /// <summary>
    /// 检查轨迹预设是否可用于回放；必要时弹窗提示用户修正。
    /// </summary>
    /// <param name="trajectoryPreset">用户当前选择的轨迹预设。</param>
    /// <param name="showDialog">是否在失败时弹出提示窗口。</param>
    /// <returns>预设可回放时返回 true。</returns>
    private bool ValidateTrajectoryPreset(CameraTrajectoryPreset trajectoryPreset, bool showDialog)
    {
        bool isValid = HasValidTrajectoryPreset(trajectoryPreset);
        if (!isValid && showDialog)
        {
            EditorUtility.DisplayDialog("截图工具", "当前轨迹预设无有效关键帧或时长为 0，无法执行回放录制。", "确定");
        }

        return isValid;
    }

    /// <summary>
    /// 将最近一次录制得到的轨迹缓存保存为可复用的 ScriptableObject 资产。
    /// 保存成功后会自动回填到“回放预设”栏，便于立刻再次录制。
    /// </summary>
    private void SaveLastRecordedTrajectoryPreset()
    {
        if (!HasRecordableTrajectoryCache())
        {
            EditorUtility.DisplayDialog("截图工具", "当前没有可保存的轨迹缓存，请先完成一次轨迹录制。", "确定");
            return;
        }

        string defaultAssetName = $"{m_FilePrefix}_Trajectory_{DateTime.Now:yyyyMMdd_HHmmss}";
        string assetPath = EditorUtility.SaveFilePanelInProject(
            "保存相机轨迹预设",
            defaultAssetName,
            "asset",
            "请选择轨迹预设保存位置。");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        CameraTrajectoryPreset trajectoryPreset = CreateInstance<CameraTrajectoryPreset>();
        trajectoryPreset.SetData(m_LastRecordedTrajectoryCameraName, m_LastRecordedTrajectoryDuration, m_LastRecordedTrajectoryKeyframes);
        AssetDatabase.CreateAsset(trajectoryPreset, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        m_SelectedTrajectoryPreset = trajectoryPreset;
        Debug.Log($"[ScreenshotTool] 已保存相机轨迹预设: {assetPath}");
    }

    /// <summary>
    /// 结束轨迹回放并恢复相机录制前的姿态，避免工具修改污染场景编辑状态。
    /// </summary>
    private void RestorePlaybackCameraState()
    {
        bool needsRestore = m_IsTrajectoryPlaybackActive || m_MovementMode == CameraMovementMode.Rig;
        if (!needsRestore || m_RecordingCamera == null)
        {
            m_ActiveTrajectoryPreset = null;
            m_IsTrajectoryPlaybackActive = false;
            return;
        }

        m_RecordingCamera.transform.SetPositionAndRotation(m_PlaybackRestorePosition, m_PlaybackRestoreRotation);
        m_RecordingCamera.orthographic = m_PlaybackRestoreOrthographic;
        if (m_PlaybackRestoreOrthographic)
        {
            m_RecordingCamera.orthographicSize = m_PlaybackRestoreOrthographicSize;
        }
        else
        {
            m_RecordingCamera.fieldOfView = m_PlaybackRestoreFieldOfView;
        }

        m_ActiveTrajectoryPreset = null;
        m_IsTrajectoryPlaybackActive = false;
    }

    private Camera ResolveTargetCamera(bool showErrorDialog)
    {
        Camera camera = m_TargetCamera != null ? m_TargetCamera : Camera.main;
        if (camera == null && showErrorDialog)
        {
            EditorUtility.DisplayDialog("截图工具", "未找到可用相机，请先指定目标相机或确保场景中存在 Main Camera。", "确定");
        }

        return camera;
    }

    private bool ValidateFfmpegPath(bool requiresAsyncGpuReadback)
    {
        if (string.IsNullOrWhiteSpace(m_FfmpegExePath))
        {
            EditorUtility.DisplayDialog("截图工具", "请先指定 ffmpeg.exe 的完整路径。", "确定");
            return false;
        }

        if (!File.Exists(m_FfmpegExePath))
        {
            EditorUtility.DisplayDialog("截图工具", "指定的 ffmpeg.exe 路径不存在，请重新选择。", "确定");
            return false;
        }

        if (requiresAsyncGpuReadback && !SystemInfo.supportsAsyncGPUReadback)
        {
            EditorUtility.DisplayDialog("截图工具", "当前环境不支持 AsyncGPUReadback，无法使用相机 MP4 录屏。", "确定");
            return false;
        }

        return true;
    }

    private bool IsRecordingActive()
    {
        return m_RecordProcess != null && !m_RecordProcess.HasExited;
    }

    private string BuildFfmpegArguments(int recordWidth, int recordHeight, string outputPath)
    {
        string frameRateText = RecordFrameRate.ToString(CultureInfo.InvariantCulture);
        return string.Format(
            CultureInfo.InvariantCulture,
            "-y -f rawvideo -pix_fmt rgba -s {0}x{1} -r {2} -i - -vf \"vflip\" -an -c:v libx264 -preset ultrafast -crf {3} -pix_fmt yuv420p -movflags +faststart {4}",
            recordWidth,
            recordHeight,
            frameRateText,
            RecordQualityCrf,
            QuoteArgument(outputPath));
    }

    /// <summary>
    /// 获取 ffmpeg 浏览对话框的初始目录。
    /// 优先使用当前已填写路径所在目录，其次回退到 Program Files，
    /// 最后回退到用户目录，避免向 OpenFilePanel 传入无效路径导致无法弹窗。
    /// </summary>
    private string GetFfmpegBrowseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(m_FfmpegExePath))
        {
            if (Directory.Exists(m_FfmpegExePath))
            {
                return m_FfmpegExePath;
            }

            string currentDirectory = Path.GetDirectoryName(m_FfmpegExePath);
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            {
                return currentDirectory;
            }
        }

        string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFilesPath) && Directory.Exists(programFilesPath))
        {
            return programFilesPath;
        }

        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath) && Directory.Exists(userProfilePath))
        {
            return userProfilePath;
        }

        return Application.dataPath;
    }

    private int EnsureEvenPositiveSize(int value)
    {
        int safeValue = Mathf.Max(2, value);
        if ((safeValue & 1) != 0)
        {
            safeValue += 1;
        }

        return safeValue;
    }

    private void OnFfmpegDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            m_LastRecordLogLine = args.Data;
        }
    }

    private void DisposeRecordingProcess(bool forceKill)
    {
        StopRecordWriterThread(forceKill);

        if (m_RecordProcess != null)
        {
            try
            {
                if (forceKill && !m_RecordProcess.HasExited)
                {
                    m_RecordProcess.Kill();
                    m_RecordProcess.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ScreenshotTool] 释放 ffmpeg 进程时发生异常: {ex.Message}");
            }
            finally
            {
                m_RecordProcess.OutputDataReceived -= OnFfmpegDataReceived;
                m_RecordProcess.ErrorDataReceived -= OnFfmpegDataReceived;
                m_RecordProcess.Dispose();
                m_RecordProcess = null;
            }
        }

        RestorePlaybackCameraState();
        CleanupRecordResources();
        ResetRecordingState();
    }

    private void StopRecordWriterThread(bool forceKill)
    {
        m_RecordWriterRunning = false;
        m_RecordFrameEvent.Set();

        if (m_RecordWriterThread != null)
        {
            try
            {
                int waitMilliseconds = forceKill ? 500 : 2000;
                m_RecordWriterThread.Join(waitMilliseconds);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ScreenshotTool] 等待录屏写入线程退出时发生异常: {ex.Message}");
            }
            finally
            {
                m_RecordWriterThread = null;
            }
        }
    }

    private string CreateOutputPath(string extension)
    {
        string projectRootPath = Directory.GetParent(Application.dataPath).FullName;
        string safeFolderName = string.IsNullOrWhiteSpace(m_OutputFolderName) ? DefaultFolderName : m_OutputFolderName.Trim();
        string safePrefix = string.IsNullOrWhiteSpace(m_FilePrefix) ? DefaultFilePrefix : m_FilePrefix.Trim();
        string safeExtension = extension.Trim().TrimStart('.');
        string outputDirectory = Path.Combine(projectRootPath, safeFolderName);
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{safePrefix}_{timestamp}.{safeExtension}";
        return Path.Combine(outputDirectory, fileName);
    }

    private string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private void LoadPrefs()
    {
        m_OutputFolderName = EditorPrefs.GetString(PrefKeyPrefix + "OutputFolderName", DefaultFolderName);
        m_FilePrefix = EditorPrefs.GetString(PrefKeyPrefix + "FilePrefix", DefaultFilePrefix);
        m_Width = EditorPrefs.GetInt(PrefKeyPrefix + "Width", 1920);
        m_Height = EditorPrefs.GetInt(PrefKeyPrefix + "Height", 1080);
        m_SuperSize = EditorPrefs.GetInt(PrefKeyPrefix + "SuperSize", 1);
        m_RevealInExplorer = EditorPrefs.GetBool(PrefKeyPrefix + "RevealInExplorer", true);
        m_FfmpegExePath = EditorPrefs.GetString(PrefKeyPrefix + "FfmpegExePath", string.Empty);
        m_RecordDurationSeconds = EditorPrefs.GetFloat(PrefKeyPrefix + "RecordDurationSeconds", 10f);
        m_RecordDelaySeconds = EditorPrefs.GetFloat(PrefKeyPrefix + "RecordDelaySeconds", 0f);
        m_RecordWidth = EnsureEvenPositiveSize(EditorPrefs.GetInt(PrefKeyPrefix + "RecordWidth", 1920));
        m_RecordHeight = EnsureEvenPositiveSize(EditorPrefs.GetInt(PrefKeyPrefix + "RecordHeight", 1080));
        m_RecordCameraTrajectory = EditorPrefs.GetBool(PrefKeyPrefix + "RecordCameraTrajectory", true);

        // 机位配置
        m_MovementMode = (CameraMovementMode)EditorPrefs.GetInt(PrefKeyPrefix + "MovementMode", (int)CameraMovementMode.Trajectory);
        m_RigSmoothTime = EditorPrefs.GetFloat(PrefKeyPrefix + "RigSmoothTime", 2f);
        m_RigMaxSpeed = EditorPrefs.GetFloat(PrefKeyPrefix + "RigMaxSpeed", float.PositiveInfinity);
        m_RigLookAtTarget = RestoreTransformByInstanceID(EditorPrefs.GetInt(PrefKeyPrefix + "RigLookAtTargetID", 0));
        int waypointCount = EditorPrefs.GetInt(PrefKeyPrefix + "RigWaypointCount", DefaultRigWaypointCount);
        m_RigWaypoints = new List<Transform>(new Transform[Mathf.Max(2, waypointCount)]);
        for (int i = 0; i < m_RigWaypoints.Count; i++)
        {
            m_RigWaypoints[i] = RestoreTransformByInstanceID(EditorPrefs.GetInt(PrefKeyPrefix + "RigWaypoint" + i + "ID", 0));
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
        EditorPrefs.SetString(PrefKeyPrefix + "FfmpegExePath", m_FfmpegExePath);
        EditorPrefs.SetFloat(PrefKeyPrefix + "RecordDurationSeconds", Mathf.Max(1f, m_RecordDurationSeconds));
        EditorPrefs.SetFloat(PrefKeyPrefix + "RecordDelaySeconds", Mathf.Max(0f, m_RecordDelaySeconds));
        EditorPrefs.SetInt(PrefKeyPrefix + "RecordWidth", EnsureEvenPositiveSize(m_RecordWidth));
        EditorPrefs.SetInt(PrefKeyPrefix + "RecordHeight", EnsureEvenPositiveSize(m_RecordHeight));
        EditorPrefs.SetBool(PrefKeyPrefix + "RecordCameraTrajectory", m_RecordCameraTrajectory);

        // 机位配置
        EditorPrefs.SetInt(PrefKeyPrefix + "MovementMode", (int)m_MovementMode);
        EditorPrefs.SetFloat(PrefKeyPrefix + "RigSmoothTime", m_RigSmoothTime);
        EditorPrefs.SetFloat(PrefKeyPrefix + "RigMaxSpeed", m_RigMaxSpeed);
        EditorPrefs.SetInt(PrefKeyPrefix + "RigLookAtTargetID", m_RigLookAtTarget != null ? m_RigLookAtTarget.GetInstanceID() : 0);
        EditorPrefs.SetInt(PrefKeyPrefix + "RigWaypointCount", m_RigWaypoints.Count);
        for (int i = 0; i < m_RigWaypoints.Count; i++)
        {
            int instanceID = m_RigWaypoints[i] != null ? m_RigWaypoints[i].GetInstanceID() : 0;
            EditorPrefs.SetInt(PrefKeyPrefix + "RigWaypoint" + i + "ID", instanceID);
        }
    }
    private static Transform RestoreTransformByInstanceID(int instanceID)
    {
        if (instanceID == 0)
        {
            return null;
        }
        UnityEngine.Object obj = EditorUtility.InstanceIDToObject(instanceID);
        return obj as Transform;
    }
}
