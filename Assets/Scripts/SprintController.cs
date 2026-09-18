using UnityEngine;

/// <summary>
/// 智能图书馆 · 加速行走
/// 触发方式:
///   · PICO 真机:按住【左手 Grip 侧键】= 加速(右手留抓书);
///   · 编辑器模拟器:按住【左 Shift】= 加速。
/// 实现:运行时把 Continuous/Dynamic Move Provider 的 moveSpeed 在基础值与加速值间切换。
/// 挂在 XR Interaction Setup 根上,Move Provider 自动在子级查找。
/// </summary>
public class SprintController : MonoBehaviour
{
    [Header("加速设置")]
    public float sprintMultiplier = 3f;

    [Tooltip("留空自动在子级查找 DynamicMoveProvider / ContinuousMoveProvider")]
    public MonoBehaviour moveProviderBehaviour;

    float baseSpeed = -1f;
    bool sprinting;

    System.Reflection.PropertyInfo speedProp;
    Transform camOffset;          // 相机偏移节点(不被头显追踪驱动,可安全加晃动)
    Vector3 camOffsetBase;
    float bobT;

    void Awake()
    {
        // Starter Assets 采样程序集不被 Assembly-CSharp 引用 → 按类型名反射查找
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
        {
            var tn = mb.GetType().Name;
            if (tn == "DynamicMoveProvider" || tn == "ActionBasedContinuousMoveProvider" || tn == "ContinuousMoveProvider")
            {
                moveProviderBehaviour = mb;
                speedProp = mb.GetType().GetProperty("moveSpeed");
                break;
            }
        }
        if (speedProp != null) baseSpeed = (float)speedProp.GetValue(moveProviderBehaviour, null);
        if (baseSpeed <= 0f) baseSpeed = 1.5f;
        // 相机偏移节点(跑步震动载体)
        var co = transform.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>(true);
        if (co != null) {
            var cot = co.transform.Find("Camera Offset");
            if (cot != null) { camOffset = cot; camOffsetBase = cot.localPosition; }
        }
    }

    void Update()
    {
        if (moveProviderBehaviour == null || speedProp == null) return;
        bool sprint = ReadSprintInput();
        if (sprint != sprinting)
        {
            sprinting = sprint;
            speedProp.SetValue(moveProviderBehaviour, baseSpeed * (sprinting ? sprintMultiplier : 1f), null);
        }
        // 跑步镜头震荡:轻微纵向颠簸(模拟跑步节奏),停止后回弹
        if (camOffset != null)
        {
            if (sprinting)
            {
                bobT += Time.deltaTime * 11f;
                var p = camOffsetBase;
                p.y += Mathf.Sin(bobT) * 0.028f;
                p.x += Mathf.Cos(bobT * 0.5f) * 0.012f;
                camOffset.localPosition = p;
            }
            else if (bobT > 0f)
            {
                bobT = 0f;
                camOffset.localPosition = camOffsetBase;
            }
        }
    }

    /// <summary>左手 Grip(真机)或 左 Shift(编辑器)。</summary>
    static bool ReadSprintInput()
    {
        // 编辑器:左 Shift
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.leftShiftKey.isPressed) return true;

        // 真机:左手 Grip 键(legacy XR Input,PICO/OpenXR 均支持)
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
        bool grip;
        if (device != null && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out grip) && grip)
            return true;
        return false;
    }
}
