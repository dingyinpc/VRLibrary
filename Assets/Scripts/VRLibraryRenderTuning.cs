using System.Collections;
using UnityEngine;

/// <summary>
/// 2026-11-09:深度精度修正。
///
/// 现象:远处物体和地板持续闪烁(z-fighting / 深度抖动)。
/// 原因:XR Origin 的 Main Camera 是 `near = 0.01 / far = 1000`,
/// 24 位深度缓冲在 0.01 近平面下,20 m 处的精度只有 ~2mm,
/// 而地板/嵌条之间只有 5mm 间距 → 直接打架。
///
/// 处理:near 0.01 → 0.05,far 1000 → 300(场景雾也在 300 结束),
/// 同距离深度精度提升约 16 倍。prefab 里也同步改了,这里再兜一层,
/// 防止别的脚本/示例重置相机参数。
/// </summary>
public class VRLibraryRenderTuning : MonoBehaviour
{
    public const float NearClip = 0.05f;
    public const float FarClip = 300f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[RenderTuning]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryRenderTuning>();
    }

    IEnumerator Start()
    {
        yield return null;

        Camera cam = null;
        foreach (var c in Camera.allCameras)
        {
            if (c != null && c.stereoTargetEye != StereoTargetEyeMask.None) { cam = c; break; }
        }
        if (cam == null) cam = Camera.main;
        if (cam == null) { Debug.LogWarning("[Render] 没找到相机，跳过裁剪面修正"); yield break; }

        float oldN = cam.nearClipPlane;
        float oldF = cam.farClipPlane;
        cam.nearClipPlane = NearClip;
        cam.farClipPlane = FarClip;

        Debug.Log("[Render] 裁剪面 " + oldN.ToString("0.###") + " / " + oldF.ToString("0")
                  + " → " + cam.nearClipPlane.ToString("0.###") + " / " + cam.farClipPlane.ToString("0")
                  + "（深度精度提升，修地板/远处闪烁）");
    }
}
