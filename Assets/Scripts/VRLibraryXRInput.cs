using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// 2026-11-09:真机输入统一读取。
///
/// 背景:PICO Neo 3 + SDK 2.5.3 的手柄在 InputSystem 里是**自动生成的**
/// `XRInputV1::PicoVR::PICOControllerLeft/Right` 布局(不是 PICO 自己的 PXR_Controller),
/// 因此:
///   · 旧的 UnityEngine.XR.InputDevices.GetDeviceAtXRNode + CommonUsages 在这台固件上
///     读不到 A/B/Grip(菜单键、小地图键、跑步键全部"像没绑");
///   · 设备名要用 displayName("PICO Controller-Left"),`name` 是布局名,不能用来分左右。
///
/// 所以所有按键都走这里:InputSystem + displayName 分左右手 + 多别名兜底。
/// </summary>
public static class VRLibraryXRInput
{
    static InputDevice cachedLeft;
    static InputDevice cachedRight;
    static bool hookRegistered;

    static void EnsureHook()
    {
        if (hookRegistered) return;
        hookRegistered = true;
        InputSystem.onDeviceChange += OnDeviceChange;
    }

    static void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Added ||
            change == InputDeviceChange.Removed ||
            change == InputDeviceChange.Disconnected ||
            change == InputDeviceChange.Reconnected ||
            change == InputDeviceChange.ConfigurationChanged)
        {
            cachedLeft = null;
            cachedRight = null;
        }
    }

    /// <summary>是不是 XR 手柄设备(自动生成的 XRInputV1 布局)。</summary>
    public static bool IsXRDevice(InputDevice d)
        => d != null && d.added && d.layout != null && d.layout.StartsWith("XRInputV1");

    /// <summary>按左右手取设备(displayName 里带 Left / Right)。找不到返回 null。</summary>
    public static InputDevice Hand(bool right)
    {
        EnsureHook();
        var cached = right ? cachedRight : cachedLeft;
        if (IsXRDevice(cached)) return cached;

        foreach (var d in InputSystem.devices)
        {
            if (!IsXRDevice(d)) continue;
            string n = d.displayName ?? d.name ?? "";
            if (right ? n.Contains("Right") : n.Contains("Left"))
            {
                if (right) cachedRight = d;
                else cachedLeft = d;
                return d;
            }
        }
        return null;
    }

    /// <summary>某只手的某个/某些按钮是否按下(按别名依次尝试)。</summary>
    public static bool Button(bool right, params string[] controlNames)
    {
        var d = Hand(right);
        if (d == null) return false;
        for (int i = 0; i < controlNames.Length; i++)
        {
            var c = d.TryGetChildControl(controlNames[i]);
            if (c is ButtonControl b)
            {
                if (b.isPressed) return true;
            }
            else if (c is AxisControl a)
            {
                // 有的设备只暴露轴(如 grip 0..1),超过一半当按下
                if (a.ReadValue() > 0.5f) return true;
            }
        }
        return false;
    }

    /// <summary>左右任意一只手按下。</summary>
    public static bool EitherButton(params string[] controlNames)
        => Button(true, controlNames) || Button(false, controlNames);

    /// <summary>按键按下的那一帧(用于开合类操作)。</summary>
    public static bool ButtonDown(bool right, params string[] controlNames)
    {
        var d = Hand(right);
        if (d == null) return false;
        for (int i = 0; i < controlNames.Length; i++)
        {
            var b = d.TryGetChildControl(controlNames[i]) as ButtonControl;
            if (b != null && b.wasPressedThisFrame) return true;
        }
        return false;
    }

    /// <summary>读一个轴(扳机/摇杆等)。</summary>
    public static float Axis(bool right, string controlName)
    {
        var d = Hand(right);
        if (d == null) return 0f;
        var a = d.TryGetChildControl(controlName) as AxisControl;
        return a != null ? a.ReadValue() : 0f;
    }
}
