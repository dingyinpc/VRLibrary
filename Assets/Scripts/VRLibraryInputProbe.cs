#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 2026-11-09:一次性输入诊断(启动后 5s / 10s 各打一行)。
/// 用来确认 PICO Neo 3 + SDK 2.5.3 下 Unity 到底有没有拿到手柄设备。
/// 查清后可删除或保留(只在启动时打两行,不影响运行)。
/// </summary>
public class VRLibraryInputProbe : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[InputProbe]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryInputProbe>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(5f);
        Dump("5s");
        yield return new WaitForSecondsRealtime(5f);
        Dump("10s");
    }

    static void Dump(string tag)
    {
        var sb = new StringBuilder();
        sb.Append("[InputProbe " + tag + "] XR isDeviceActive=" + XRSettings.isDeviceActive
                  + " loaderPresent=" + XRSettings.loadedDeviceName);

        var devs = new System.Collections.Generic.List<InputDevice>();
        InputDevices.GetDevices(devs);
        sb.Append(" | XR InputDevices=" + devs.Count);
        for (int i = 0; i < devs.Count; i++)
        {
            var d = devs[i];
            bool trig = d.TryGetFeatureValue(CommonUsages.trigger, out float t);
            bool grip = d.TryGetFeatureValue(CommonUsages.grip, out float g);
            bool stick = d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 s);
            bool btnA = d.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
            sb.Append("\n    · " + d.name + "(" + d.characteristics + ")"
                      + " trigger=" + (trig ? t.ToString("0.00") : "×")
                      + " grip=" + (grip ? g.ToString("0.00") : "×")
                      + " stick=" + (stick ? s.ToString("0.00") : "×")
                      + " A=" + (btnA ? a.ToString() : "×"));
        }

        sb.Append("\n  | InputSystem total=" + UnityEngine.InputSystem.InputSystem.devices.Count + " ; XR 相关:");
        foreach (var d in UnityEngine.InputSystem.InputSystem.devices)
        {
            string lay = d.layout ?? "";
            if (lay.Contains("XR") || lay.Contains("Pico") || lay.Contains("Hand") || lay.Contains("Controller"))
                sb.Append("\n    · " + d.displayName + " <" + lay + ">");
        }

        Debug.Log(sb.ToString());
    }
}
#endif
