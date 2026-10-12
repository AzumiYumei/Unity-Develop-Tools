// ================================================================
// 相机轨迹预设
//
// 文件名称: CameraTrajectoryPreset.cs
// 版本: 1.1
//
// 描述: 定义截图工具使用的相机轨迹预设资产。
// 该资产会记录相机在录制过程中的世界坐标、旋转和核心镜头参数，
// 便于后续在编辑器中一键回放相同的运镜轨迹并同步开始录屏。
//
// 修改历史:
// 2026-07-03 - 初始版本
// 2026-07-30 - 为大幅度位移段增加平滑插值，降低轨迹回放卡顿感
// ================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class CameraTrajectoryKeyframe
{
    [SerializeField] public float m_Time;
    [SerializeField] public Vector3 m_Position;
    [SerializeField] public Quaternion m_Rotation;
    [SerializeField] public float m_FieldOfView;
    [SerializeField] public bool m_IsOrthographic;
    [SerializeField] public float m_OrthographicSize;

    /// <summary>
    /// 从当前相机状态创建一个轨迹关键帧。
    /// </summary>
    /// <param name="camera">需要采样的相机。</param>
    /// <param name="time">相对轨迹起点的时间，单位秒。</param>
    /// <returns>可直接写入预设或缓存的关键帧数据。</returns>
    public static CameraTrajectoryKeyframe CreateFromCamera(Camera camera, float time)
    {
        return new CameraTrajectoryKeyframe
        {
            m_Time = Mathf.Max(0f, time),
            m_Position = camera.transform.position,
            m_Rotation = camera.transform.rotation,
            m_FieldOfView = camera.fieldOfView,
            m_IsOrthographic = camera.orthographic,
            m_OrthographicSize = camera.orthographicSize
        };
    }
}

[CreateAssetMenu(fileName = "CameraTrajectoryPreset", menuName = "Azumi/Camera Trajectory Preset")]
public class CameraTrajectoryPreset : ScriptableObject
{
    /// <summary>
    /// 判断相邻关键帧是否属于“大跨度位移”的阈值。
    /// 当两个关键帧间的相机位置变化超过该值时，会启用更柔和的插值策略，
    /// 避免回放时出现明显的位移跳变感。
    /// </summary>
    private const float LargeMovementDistanceThreshold = 0.5f;

    /// <summary>
    /// 大跨度位移段的平滑权重增长区间。
    /// 位移越大，越偏向使用平滑路径；但仍保留一部分原始线性结果，
    /// 避免 Catmull-Rom 在极端数据下产生过冲过大。
    /// </summary>
    private const float LargeMovementBlendDistance = 2f;

    /// <summary>
    /// 平滑路径的最大混合权重。
    /// 不直接完全替换为曲线结果，而是做有限混合，确保回放轨迹仍贴近原始采样。
    /// </summary>
    private const float MaxLargeMovementSmoothWeight = 0.6f;

    [SerializeField] private string m_SourceCameraName = string.Empty;
    [SerializeField] private float m_Duration = 0f;
    [SerializeField] private List<CameraTrajectoryKeyframe> m_Keyframes = new List<CameraTrajectoryKeyframe>();

    public string SourceCameraName
    {
        get
        {
            return m_SourceCameraName;
        }
    }

    public float Duration
    {
        get
        {
            return m_Duration;
        }
    }

    public IReadOnlyList<CameraTrajectoryKeyframe> Keyframes
    {
        get
        {
            return m_Keyframes;
        }
    }

    /// <summary>
    /// 使用新的关键帧列表覆盖当前预设数据。
    /// </summary>
    /// <param name="sourceCameraName">轨迹录制来源相机名称。</param>
    /// <param name="duration">轨迹总时长，单位秒。</param>
    /// <param name="keyframes">按时间顺序采样得到的关键帧。</param>
    public void SetData(string sourceCameraName, float duration, List<CameraTrajectoryKeyframe> keyframes)
    {
        m_SourceCameraName = sourceCameraName ?? string.Empty;
        m_Duration = Mathf.Max(0f, duration);
        m_Keyframes = keyframes != null ? new List<CameraTrajectoryKeyframe>(keyframes) : new List<CameraTrajectoryKeyframe>();
        m_Keyframes.Sort(CompareKeyframeTime);
    }

    /// <summary>
    /// 判断当前预设是否包含可用于回放的轨迹数据。
    /// </summary>
    /// <returns>至少存在一个关键帧且时长有效时返回 true。</returns>
    public bool HasValidData()
    {
        return m_Keyframes != null && m_Keyframes.Count > 0 && m_Duration >= 0f;
    }

    /// <summary>
    /// 按指定时间对轨迹进行采样，并输出插值后的相机状态。
    /// </summary>
    /// <param name="time">相对轨迹起点的采样时间，单位秒。</param>
    /// <param name="position">输出的位置。</param>
    /// <param name="rotation">输出的旋转。</param>
    /// <param name="fieldOfView">输出的视野角。</param>
    /// <param name="isOrthographic">输出的正交模式。</param>
    /// <param name="orthographicSize">输出的正交尺寸。</param>
    public void Evaluate(
        float time,
        out Vector3 position,
        out Quaternion rotation,
        out float fieldOfView,
        out bool isOrthographic,
        out float orthographicSize)
    {
        if (!HasValidData())
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            fieldOfView = 60f;
            isOrthographic = false;
            orthographicSize = 5f;
            return;
        }

        if (m_Keyframes.Count == 1)
        {
            CameraTrajectoryKeyframe singleKeyframe = m_Keyframes[0];
            position = singleKeyframe.m_Position;
            rotation = singleKeyframe.m_Rotation;
            fieldOfView = singleKeyframe.m_FieldOfView;
            isOrthographic = singleKeyframe.m_IsOrthographic;
            orthographicSize = singleKeyframe.m_OrthographicSize;
            return;
        }

        float clampedTime = Mathf.Clamp(time, 0f, m_Duration);
        int previousIndex = 0;
        int nextIndex = m_Keyframes.Count - 1;
        CameraTrajectoryKeyframe previousKeyframe = m_Keyframes[0];
        CameraTrajectoryKeyframe nextKeyframe = m_Keyframes[m_Keyframes.Count - 1];

        for (int index = 1; index < m_Keyframes.Count; index++)
        {
            CameraTrajectoryKeyframe currentKeyframe = m_Keyframes[index];
            if (clampedTime <= currentKeyframe.m_Time)
            {
                nextIndex = index;
                nextKeyframe = currentKeyframe;
                break;
            }

            previousIndex = index;
            previousKeyframe = currentKeyframe;
        }

        if (ReferenceEquals(previousKeyframe, nextKeyframe) || Mathf.Approximately(previousKeyframe.m_Time, nextKeyframe.m_Time))
        {
            position = nextKeyframe.m_Position;
            rotation = nextKeyframe.m_Rotation;
            fieldOfView = nextKeyframe.m_FieldOfView;
            isOrthographic = nextKeyframe.m_IsOrthographic;
            orthographicSize = nextKeyframe.m_OrthographicSize;
            return;
        }

        float lerpFactor = Mathf.InverseLerp(previousKeyframe.m_Time, nextKeyframe.m_Time, clampedTime);
        bool isLargeMovementSegment = IsLargeMovementSegment(previousKeyframe, nextKeyframe);
        float interpolationFactor = isLargeMovementSegment ? Mathf.SmoothStep(0f, 1f, lerpFactor) : lerpFactor;
        position = EvaluatePosition(previousIndex, nextIndex, interpolationFactor, isLargeMovementSegment);
        rotation = Quaternion.SlerpUnclamped(previousKeyframe.m_Rotation, nextKeyframe.m_Rotation, interpolationFactor);
        fieldOfView = Mathf.LerpUnclamped(previousKeyframe.m_FieldOfView, nextKeyframe.m_FieldOfView, interpolationFactor);
        orthographicSize = Mathf.LerpUnclamped(previousKeyframe.m_OrthographicSize, nextKeyframe.m_OrthographicSize, interpolationFactor);
        isOrthographic = lerpFactor < 0.5f ? previousKeyframe.m_IsOrthographic : nextKeyframe.m_IsOrthographic;
    }

    /// <summary>
    /// 计算当前位置采样结果。
    /// 普通段保持线性插值；当检测到大跨度位移时，引入相邻关键帧做 Catmull-Rom 曲线平滑，
    /// 并与线性结果按权重混合，以兼顾稳定性和原始轨迹贴合度。
    /// </summary>
    /// <param name="previousIndex">起始关键帧索引。</param>
    /// <param name="nextIndex">结束关键帧索引。</param>
    /// <param name="interpolationFactor">当前段内插值系数。</param>
    /// <param name="useLargeMovementSmoothing">是否启用大位移平滑。</param>
    /// <returns>当前时间点对应的位置。</returns>
    private Vector3 EvaluatePosition(int previousIndex, int nextIndex, float interpolationFactor, bool useLargeMovementSmoothing)
    {
        CameraTrajectoryKeyframe previousKeyframe = m_Keyframes[previousIndex];
        CameraTrajectoryKeyframe nextKeyframe = m_Keyframes[nextIndex];
        Vector3 linearPosition = Vector3.LerpUnclamped(previousKeyframe.m_Position, nextKeyframe.m_Position, interpolationFactor);
        if (!useLargeMovementSmoothing)
        {
            return linearPosition;
        }

        float smoothWeight = GetLargeMovementSmoothWeight(previousKeyframe, nextKeyframe);
        if (smoothWeight <= 0f)
        {
            return linearPosition;
        }

        Vector3 controlPoint0 = GetControlPointPosition(previousIndex - 1, previousKeyframe, nextKeyframe, false);
        Vector3 controlPoint1 = previousKeyframe.m_Position;
        Vector3 controlPoint2 = nextKeyframe.m_Position;
        Vector3 controlPoint3 = GetControlPointPosition(nextIndex + 1, previousKeyframe, nextKeyframe, true);
        Vector3 smoothedPosition = EvaluateCatmullRom(controlPoint0, controlPoint1, controlPoint2, controlPoint3, interpolationFactor);
        return Vector3.LerpUnclamped(linearPosition, smoothedPosition, smoothWeight);
    }

    /// <summary>
    /// 判断当前关键帧段是否属于需要额外平滑的大跨度位移。
    /// </summary>
    /// <param name="previousKeyframe">段起点关键帧。</param>
    /// <param name="nextKeyframe">段终点关键帧。</param>
    /// <returns>位移超过阈值时返回 true。</returns>
    private bool IsLargeMovementSegment(CameraTrajectoryKeyframe previousKeyframe, CameraTrajectoryKeyframe nextKeyframe)
    {
        if (previousKeyframe == null || nextKeyframe == null)
        {
            return false;
        }

        float movementDistance = Vector3.Distance(previousKeyframe.m_Position, nextKeyframe.m_Position);
        return movementDistance > LargeMovementDistanceThreshold;
    }

    /// <summary>
    /// 根据当前段的位移大小返回平滑混合权重。
    /// 位移越大，平滑参与度越高；但会被限制在最大权重内，避免曲线过度偏离原轨迹。
    /// </summary>
    /// <param name="previousKeyframe">段起点关键帧。</param>
    /// <param name="nextKeyframe">段终点关键帧。</param>
    /// <returns>0 到 MaxLargeMovementSmoothWeight 之间的权重值。</returns>
    private float GetLargeMovementSmoothWeight(CameraTrajectoryKeyframe previousKeyframe, CameraTrajectoryKeyframe nextKeyframe)
    {
        if (previousKeyframe == null || nextKeyframe == null)
        {
            return 0f;
        }

        float movementDistance = Vector3.Distance(previousKeyframe.m_Position, nextKeyframe.m_Position);
        float normalizedDistance = Mathf.InverseLerp(LargeMovementDistanceThreshold, LargeMovementDistanceThreshold + LargeMovementBlendDistance, movementDistance);
        return normalizedDistance * MaxLargeMovementSmoothWeight;
    }

    /// <summary>
    /// 获取 Catmull-Rom 所需的控制点位置。
    /// 如果相邻控制点不存在，则使用当前段方向做镜像外推，
    /// 以保证首尾段同样可以参与平滑计算，而不需要额外构造假关键帧资产。
    /// </summary>
    /// <param name="index">控制点索引。</param>
    /// <param name="previousKeyframe">段起点关键帧。</param>
    /// <param name="nextKeyframe">段终点关键帧。</param>
    /// <param name="isForward">是否获取前向控制点。</param>
    /// <returns>用于曲线插值的控制点位置。</returns>
    private Vector3 GetControlPointPosition(int index, CameraTrajectoryKeyframe previousKeyframe, CameraTrajectoryKeyframe nextKeyframe, bool isForward)
    {
        if (index >= 0 && index < m_Keyframes.Count && m_Keyframes[index] != null)
        {
            return m_Keyframes[index].m_Position;
        }

        Vector3 segmentDelta = nextKeyframe.m_Position - previousKeyframe.m_Position;
        return isForward
            ? nextKeyframe.m_Position + segmentDelta
            : previousKeyframe.m_Position - segmentDelta;
    }

    /// <summary>
    /// 计算 Catmull-Rom 曲线上的位置。
    /// 该曲线会利用前后控制点推导更自然的切线方向，
    /// 用于缓和大位移段在首尾交界处的速度变化。
    /// </summary>
    /// <param name="point0">前一控制点。</param>
    /// <param name="point1">当前段起点。</param>
    /// <param name="point2">当前段终点。</param>
    /// <param name="point3">后一控制点。</param>
    /// <param name="t">0 到 1 的段内插值系数。</param>
    /// <returns>曲线位置。</returns>
    private Vector3 EvaluateCatmullRom(Vector3 point0, Vector3 point1, Vector3 point2, Vector3 point3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * ((2f * point1)
            + (-point0 + point2) * t
            + (2f * point0 - 5f * point1 + 4f * point2 - point3) * t2
            + (-point0 + 3f * point1 - 3f * point2 + point3) * t3);
    }

    private static int CompareKeyframeTime(CameraTrajectoryKeyframe left, CameraTrajectoryKeyframe right)
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
}
