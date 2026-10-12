// ================================================================
// 相机机位平滑驱动器
//
// 文件名称: CameraRigDriver.cs
// 版本: 1.0
//
// 描述: 独立的相机机位运动驱动器。接收一组场景空物体作为机位，
// 使用 Vector3.SmoothDamp 平滑推进相机位置，并用 Transform.LookAt
// 锁定全局目标。机位按顺序首尾相连形成闭合路径，走完一圈后触发
// OnComplete 回调。
//
// 本类只负责"写相机 transform"，不负责渲染、编码或 GPU 回读。
// 调用方负责在每帧驱动时传入 deltaTime，并订阅 OnComplete 决定
// 是否停止录制。
//
// 依赖: 无（纯逻辑 + UnityEngine.Transform 写入）
// 线程安全: 非线程安全，仅限 Unity 主线程调用
// ================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

public class CameraRigDriver
{
    // ----------------------------------------------------------------
    // 配置（Begin 时传入，运行中不变）
    // ----------------------------------------------------------------
    private Camera m_TargetCamera;
    private List<Transform> m_Waypoints;
    private Transform m_LookAtTarget;
    private float m_SmoothTime;
    private float m_MaxSpeed;

    // ----------------------------------------------------------------
    // 运行时状态
    // ----------------------------------------------------------------
    private int m_CurrentSegment;
    private Vector3 m_SmoothVelocity;
    private bool m_IsFinished;

    // 到达阈值（米），位置距离小于此值视为到达当前目标机位
    private const float k_ArrivalThreshold = 0.01f;
    // 到达阈值的平方，用于 SqrMagnitude 比较，避免开方运算
    private const float k_ArrivalThresholdSqr = k_ArrivalThreshold * k_ArrivalThreshold;

    // ----------------------------------------------------------------
    // 公开属性
    // ----------------------------------------------------------------
    /// <summary>
    /// 机位是否已走完一圈回到起点。
    /// true 后 Update 不再推进，需调用 Reset 或 Begin 重新启动。
    /// </summary>
    public bool IsFinished => m_IsFinished;

    /// <summary>
    /// 当前正在过渡的段索引（0 = waypoint[0]→waypoint[1]）。
    /// </summary>
    public int CurrentSegment => m_CurrentSegment;

    /// <summary>
    /// 当机位走完一圈回到起点时触发。调用方可订阅此事件来停止录制。
    /// 事件在主线程同步触发，订阅者不应在此回调中销毁驱动器自身。
    /// </summary>
    public event Action OnComplete;

    // ----------------------------------------------------------------
    // 生命周期
    // ----------------------------------------------------------------

    /// <summary>
    /// 启动机位驱动。立刻把相机移到第一个机位，速度从零开始。
    /// </summary>
    /// <param name="target">被驱动的相机。</param>
    /// <param name="waypoints">机位 Transform 列表，至少 2 个有效元素。</param>
    /// <param name="lookAt">LookAt 锁定目标，可为 null（此时旋转保持不变）。</param>
    /// <param name="smoothTime">SmoothDamp 平滑时间（秒），必须 > 0。</param>
    /// <param name="maxSpeed">SmoothDamp 最大速度，float.PositiveInfinity 表示无上限。</param>
    public void Begin(Camera target, List<Transform> waypoints, Transform lookAt,
                      float smoothTime, float maxSpeed)
    {
        m_TargetCamera = target;
        // 拷贝引用列表，避免外部修改影响运行中状态
        m_Waypoints = (waypoints != null) ? new List<Transform>(waypoints) : new List<Transform>();
        m_LookAtTarget = lookAt;
        m_SmoothTime = smoothTime;
        m_MaxSpeed = maxSpeed;
        m_CurrentSegment = 0;
        m_SmoothVelocity = Vector3.zero;
        m_IsFinished = false;

        // 立刻把相机移到起点，避免录制开始时从原位置"飞"到起点的过渡被录进去
        if (m_TargetCamera != null && m_Waypoints.Count > 0 && m_Waypoints[0] != null)
        {
            m_TargetCamera.transform.position = m_Waypoints[0].position;
        }
    }

    /// <summary>
    /// 每帧推进相机运动。由调用方在 OnEditorUpdate / CaptureOffline / 预览循环中调用。
    /// </summary>
    /// <param name="deltaTime">本帧时间增量（秒）。<=0 时直接返回，>0.1f 应由调用方 clamp。</param>
    public void Update(float deltaTime)
    {
        if (m_IsFinished) return;
        if (deltaTime <= 0f) return;
        if (m_TargetCamera == null) return;
        if (m_Waypoints == null || m_Waypoints.Count < 2) return;

        int nextIndex = (m_CurrentSegment + 1) % m_Waypoints.Count;
        Transform nextWaypoint = m_Waypoints[nextIndex];

        // 目标机位为空：跳过本段，继续下一段（避免卡死）
        if (nextWaypoint == null)
        {
            AdvanceSegment();
            return;
        }

        // 1. 位置: SmoothDamp 朝 nextWaypoint 推进
        Vector3 currentPos = m_TargetCamera.transform.position;
        Vector3 targetPos = nextWaypoint.position;
        Vector3 newPos = Vector3.SmoothDamp(
            currentPos, targetPos, ref m_SmoothVelocity, m_SmoothTime, m_MaxSpeed, deltaTime);
        m_TargetCamera.transform.position = newPos;

        // 2. 旋转: LookAt 锁定目标 (无平滑, 避免双重平滑眩晕)
        //    m_LookAtTarget 为空时旋转保持不变
        if (m_LookAtTarget != null)
        {
            m_TargetCamera.transform.LookAt(m_LookAtTarget);
        }

        // 3. 到达判定: 距离 < 阈值 → 切换下一段
        //    不重置 m_SmoothVelocity, 让惯性自然延续到下一段
        if ((newPos - targetPos).sqrMagnitude < k_ArrivalThresholdSqr)
        {
            AdvanceSegment();
        }
    }

    /// <summary>
    /// 清空所有状态并卸载事件订阅。停止录制/预览时调用。
    /// </summary>
    public void Reset()
    {
        m_TargetCamera = null;
        m_Waypoints = null;
        m_LookAtTarget = null;
        m_SmoothTime = 0f;
        m_MaxSpeed = 0f;
        m_CurrentSegment = 0;
        m_SmoothVelocity = Vector3.zero;
        m_IsFinished = false;
        OnComplete = null;
    }

    // ----------------------------------------------------------------
    // 内部
    // ----------------------------------------------------------------

    /// <summary>
    /// 推进到下一段。若已走完一圈则标记完成并触发 OnComplete。
    /// </summary>
    private void AdvanceSegment()
    {
        m_CurrentSegment++;
        if (m_CurrentSegment >= m_Waypoints.Count)
        {
            m_IsFinished = true;
            OnComplete?.Invoke();
        }
    }
}
