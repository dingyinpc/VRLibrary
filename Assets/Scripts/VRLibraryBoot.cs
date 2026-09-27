using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

// 真机上跑到的画质档位不一定等于编辑器当前档,凡是影响清晰度的参数都在这里钉死,
// 并且让设备把"实际拿到的眼缓冲"报出来 —— 没有这行日志就只能靠猜真机为什么糊。
public class VRLibraryBoot : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void PinTextureFiltering()
    {
        QualitySettings.globalTextureMipmapLimit = 0;
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("[VRLibraryBoot]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryBoot>();
    }

    IEnumerator Start()
    {
        EnableFfr();
        Debug.Log("[VRLibrary] 清晰度自检(建场景后) " + Snapshot());
        if (Application.isEditor) yield break;      // 编辑器没有 XR 显示,再等也不会有眼缓冲数值
        yield return new WaitForSecondsRealtime(1.5f);
        Debug.Log("[VRLibrary] 清晰度自检(1.5s 后) " + Snapshot());
    }

    // ------------------------------------------------------------ 固定视场渲染(FFR)

    /// <summary>PICO 的 FFR 档位:None=-1, Low=0, Med=1, High=2, TopHigh=3。</summary>
    const int FfrLevel = 2;

    /// <summary>
    /// 开固定视场渲染:中心保持全分辨率、边缘降采样,一体机上通常省 20~30% GPU,
    /// 而边缘的降采样正好被镜片畸变遮住。**走反射调用**是为了不把 SDK 的类型绑死进程序集
    /// (本工程其它地方也是这么做的:PXR_Manager 就是反射挂上去的),SDK 换版本也不会编译不过。
    /// 编辑器里不调(那是本机显卡,调了没意义还会刷告警),所以这行只在真机 logcat 里出现。
    /// </summary>
    static void EnableFfr()
    {
        if (Application.isEditor) return;
        try
        {
            var t = System.Type.GetType("Unity.XR.PXR.PXR_FoveationRendering, Unity.XR.PICO");
            var e = System.Type.GetType("Unity.XR.PXR.FoveationLevel, Unity.XR.PICO");
            if (t == null || e == null) { Debug.LogWarning("[VRLibrary] FFR:找不到 PXR 类型,跳过"); return; }
            var mi = t.GetMethod("SetFoveationLevel", new[] { e, typeof(bool) });
            if (mi == null) { Debug.LogWarning("[VRLibrary] FFR:找不到 SetFoveationLevel,跳过"); return; }
            bool ok = (bool)mi.Invoke(null, new object[] { System.Enum.ToObject(e, FfrLevel), false });
            Debug.Log("[VRLibrary] FFR 设置 " + FfrName(e) + " → " + (ok ? "成功" : "**失败(设备可能不支持)**") + ";" + Snapshot());
        }
        catch (System.Exception ex) { Debug.LogWarning("[VRLibrary] FFR 调用异常:" + ex.Message); }
    }

    static string FfrName(System.Type e)
    {
        try
        {
            var t = System.Type.GetType("Unity.XR.PXR.PXR_FoveationRendering, Unity.XR.PICO");
            var mi = t == null ? null : t.GetMethod("GetFoveationLevel", System.Type.EmptyTypes);
            if (mi == null) return FfrLevel.ToString();
            return mi.Invoke(null, null).ToString();
        }
        catch { return FfrLevel.ToString(); }
    }

    static string Snapshot()
    {
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        return "机型=" + SystemInfo.deviceModel
               + " | 画质档=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
               + " mipmapLimit=" + QualitySettings.globalTextureMipmapLimit
               + " 各向异性=" + QualitySettings.anisotropicFiltering
               + " | 眼缓冲=" + XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight
               + " 渲染缩放(URP)=" + (urp == null ? 0f : urp.renderScale)
               + " viewportScale=" + XRSettings.renderViewportScale
               + " eyeTexScale=" + XRSettings.eyeTextureResolutionScale
               + " | 刷新率=" + CurrentRefreshRate()
               + " 纹理上限=" + SystemInfo.maxTextureSize
               + " 显存=" + (SystemInfo.graphicsMemorySize / 1024f).ToString("F1") + "GB"
               + " | " + TexProbe()
               + " | 图形API=" + SystemInfo.graphicsDeviceType;
    }

    static string CurrentRefreshRate()
    {
        return Display.displays.Length > 0 ? Screen.currentResolution.refreshRate.ToString() : "?";
    }

    // 真机上 format 反映构建后压成的真实格式:编辑器里是 RGBA32,这里若变成 ASTC/被降尺寸,
    // 就说明脚本生成的 .asset 纹理在打包时被重新导入过了。
    static string TexProbe()
    {
        var all = Resources.FindObjectsOfTypeAll<Texture2D>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name != "T_TechFloor") continue;
            return "T_TechFloor " + all[i].width + "x" + all[i].height
                   + " mip=" + all[i].mipmapCount + " 过滤=" + all[i].filterMode
                   + " 各向异性=" + all[i].anisoLevel + " 格式=" + all[i].format;
        }
        return "T_TechFloor 不在包里";
    }
}
