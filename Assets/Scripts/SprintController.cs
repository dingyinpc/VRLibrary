using UnityEngine;

/// <summary>
/// 智能图书馆 · 加速行走
/// 触发方式:
///   · PICO 真机:按住【左手 Grip】或【左摇杆按下】= 加速(右手留抓书);
///   · 编辑器模拟器:按住【左 Shift】= 加速。
/// 实现:运行时把 Continuous/Dynamic Move Provider 的 moveSpeed 在基础值与加速值间切换。
/// 挂在 XR Interaction Setup 根上,Move Provider 自动在子级查找。
///
/// 2026-11-09:之前 Awake 里反射遍历会抛 NullReferenceException,
/// 初始化在找到 provider 之前就中断 → 跑步完全没生效。现在全部加了空判断与重试。
/// </summary>
public class SprintController : MonoBehaviour
{
    [Header("加速设置")]
    public float sprintMultiplier = 3f;
    [Tooltip("VR 中人工视角晃动易致晕，默认关闭；开启也仅叠加增量偏移不破坏眼高")]
    public bool enableHeadBob = false;

    [Tooltip("留空自动在子级查找 DynamicMoveProvider / ContinuousMoveProvider")]
    public MonoBehaviour moveProviderBehaviour;

    float baseSpeed = -1f;
    bool sprinting;

    static readonly string[] SprintControls = { "gripPressed", "grip", "primary2DAxisClick", "thumbstickClicked" };

    System.Reflection.PropertyInfo speedProp;
    Transform camOffset;          // 相机偏移节点
    Vector3 appliedBobOffset;
    float bobT;
    float nextFind;

    void Awake()
    {
        InitProvider();
        InitCameraOffset();
    }

    void InitProvider()
    {
        // Starter Assets 采样程序集不被 Assembly-CSharp 引用 → 按类型名反射查找
        try
        {
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;                                   // 缺脚本的子物体可能给 null
                string tn;
                try { tn = mb.GetType().Name; } catch { continue; }
                if (tn == "DynamicMoveProvider" || tn == "ActionBasedContinuousMoveProvider" || tn == "ContinuousMoveProvider")
                {
                    var prop = mb.GetType().GetProperty("moveSpeed");
                    if (prop == null) continue;
                    moveProviderBehaviour = mb;
                    speedProp = prop;
                    break;
                }
            }

            if (speedProp != null && moveProviderBehaviour != null)
            {
                object v = speedProp.GetValue(moveProviderBehaviour, null);
                if (v is float f && f > 0f) baseSpeed = f;
            }
            if (baseSpeed <= 0f) baseSpeed = 1.5f;

            Debug.Log("[Sprint] 初始化 " + (speedProp != null
                ? "成功 provider=" + moveProviderBehaviour.GetType().Name + " baseSpeed=" + baseSpeed
                : "失败:没找到 move provider(稍后重试)"));
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[Sprint] 初始化异常(已兜底): " + e.GetType().Name + " " + e.Message);
            if (baseSpeed <= 0f) baseSpeed = 1.5f;
        }
    }

    void InitCameraOffset()
    {
        try
        {
            var co = transform.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>(true);
            if (co != null)
            {
                var cot = co.transform.Find("Camera Offset");
                if (cot != null) camOffset = cot;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[Sprint] 找相机偏移失败(不影响加速): " + e.Message);
        }
    }

    /// <summary>
    /// 星空露台观影模式下由 <c>TerraceCinema</c> 置 true:左手 Grip 在那儿要负责呼出播控面板,
    /// 不能再当加速;退出观影态时复位。抑制期间速度写回基础值、跑步震荡归零。
    /// </summary>
    public static bool suppressed;

    void Update()
    {
        // provider 没找到就隔 2 秒再试一次(场景加载顺序不确定)
        if (speedProp == null && Time.unscaledTime >= nextFind)
        {
            nextFind = Time.unscaledTime + 2f;
            InitProvider();
        }
        if (moveProviderBehaviour == null || speedProp == null) return;

        bool sprint = !suppressed && ReadSprintInput();
        if (sprint != sprinting)
        {
            sprinting = sprint;
            try { speedProp.SetValue(moveProviderBehaviour, baseSpeed * (sprinting ? sprintMultiplier : 1f), null); }
            catch (System.Exception e) { Debug.LogWarning("[Sprint] 设置速度失败: " + e.Message); }
            Debug.Log("[Sprint] " + (sprinting ? "开始加速 ×" + sprintMultiplier : "结束加速"));
        }

        // 跑步镜头震荡默认关闭(防 VR 晕动症);仅在主动启用时增量叠加并在停止时精确减去,不覆盖 XROrigin 眼高
        if (camOffset != null)
        {
            if (appliedBobOffset.sqrMagnitude > 0f)
            {
                camOffset.localPosition -= appliedBobOffset;
                appliedBobOffset = Vector3.zero;
            }
            if (enableHeadBob && sprinting)
            {
                bobT += Time.deltaTime * 11f;
                appliedBobOffset = new Vector3(
                    Mathf.Cos(bobT * 0.5f) * 0.012f,
                    Mathf.Sin(bobT) * 0.028f,
                    0f);
                camOffset.localPosition += appliedBobOffset;
            }
            else if (bobT > 0f)
            {
                bobT = 0f;
            }
        }
    }

    void OnDisable()
    {
        if (camOffset != null && appliedBobOffset.sqrMagnitude > 0f)
        {
            camOffset.localPosition -= appliedBobOffset;
            appliedBobOffset = Vector3.zero;
        }
        bobT = 0f;
    }

    /// <summary>左手 Grip / 摇杆按下(真机)或 左 Shift(编辑器)。</summary>
    static bool ReadSprintInput()
    {
        // 编辑器:左 Shift
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.leftShiftKey.isPressed) return true;

        // 真机:左手 Grip,或左摇杆按下(两种都收,哪个顺手用哪个)
        return VRLibraryXRInput.Button(false, SprintControls);
    }
}
