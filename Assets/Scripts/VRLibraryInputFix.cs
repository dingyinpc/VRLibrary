using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 2026-11-09:PICO Neo 3 + SDK 2.5.3 的手柄输入修正。
///
/// 现象:XRI 的射线/移动全无反应,但底层设备是有的:
///   InputDevices: "PICO Controller-Left / Right"(trigger/grip/摇杆都能读)
///   InputSystem : 布局是自动生成的 `XRInputV1::PicoVR::PICOControllerLeft`
///
/// PICO SDK 注册 `PXR_Controller` 布局时用的匹配串是 `^(PICO Controller)`(带空格),
/// 而运行时给出的产品名是 `PICOControllerLeft`(无空格) → 匹配不上,
/// 设备落到通用 XRInputV1 布局,且**没有 LeftHand/RightHand 用法**,
/// 于是 `<XRController>{LeftHand}/…` 的绑定全部失效。
///
/// 这里在启动后给这两个设备补上用法标签(以及缺失的 XR 控件别名),让 XRI 绑定能命中。
/// </summary>
public class VRLibraryInputFix : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[InputFix]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryInputFix>();
    }

    void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange;
    }

    void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;
    }

    void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Added ||
            change == InputDeviceChange.Reconnected ||
            change == InputDeviceChange.ConfigurationChanged)
        {
            ApplyDevice(device, change.ToString());
        }
    }

    IEnumerator Start()
    {
        Apply("0s");
        yield return new WaitForSecondsRealtime(1.0f);
        Apply("1s");
        yield return new WaitForSecondsRealtime(3.0f);
        Apply("4s");
    }

    static bool ApplyDevice(InputDevice d, string tag)
    {
        if (d == null || d.layout == null || !d.layout.StartsWith("XRInputV1")) return false;
        string name = (d.name ?? "") + "|" + (d.displayName ?? "");
        bool left = name.Contains("Left");
        bool right = name.Contains("Right");
        if (!left && !right) return false;

        string usage = left ? "LeftHand" : "RightHand";
        foreach (var u in d.usages)
            if (u == usage) return false;

        InputSystem.AddDeviceUsage(d, usage);
        Debug.Log("[InputFix " + tag + "] + " + d.displayName + " 加用法 " + usage);
        return true;
    }

    static void Apply(string tag)
    {
        var sb = new StringBuilder();
        sb.Append("[InputFix " + tag + "]");
        int count = 0;

        foreach (var d in InputSystem.devices)
        {
            if (d == null || d.layout == null) continue;
            if (!d.layout.StartsWith("XRInputV1")) continue;
            string name = (d.name ?? "") + "|" + (d.displayName ?? "");
            bool left = name.Contains("Left");
            bool right = name.Contains("Right");
            if (!left && !right) continue;
            count++;

            string usage = left ? "LeftHand" : "RightHand";
            bool has = false;
            foreach (var u in d.usages) if (u == usage) { has = true; break; }
            if (!has)
            {
                InputSystem.AddDeviceUsage(d, usage);
                sb.Append("\n  + " + d.displayName + " 加用法 " + usage);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 把常见的 XR 控件列出来,确认绑定要的控件确实存在
            var ctrl = new StringBuilder();
            foreach (var want in new[] { "trigger", "triggerPressed", "grip", "gripPressed",
                                         "primary2DAxis", "primaryButton", "secondaryButton",
                                         "menu", "primary2DAxisClick", "thumbstickClicked",
                                         "devicePosition", "deviceRotation" })
            {
                var c = d.TryGetChildControl(want);
                ctrl.Append(" " + want + (c != null ? "✓" : "✗"));
            }
            sb.Append("\n  · " + d.displayName + " <" + d.layout + "> usages=" + string.Join(",", d.usages) + ctrl);

            // 再把该设备实际拥有的控件名全列一遍(排查"按键没绑全")
            var names = new List<string>();
            foreach (var c in d.children) names.Add(c.name);
            sb.Append("\n     全部控件: " + string.Join(",", names));
#endif
        }

        if (count > 0 || tag == "4s")
            Debug.Log(sb.ToString());
    }
}
