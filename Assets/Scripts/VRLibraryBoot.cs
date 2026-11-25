using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

// 真机上跑到的画质档位不一定等于编辑器当前档,凡是影响清晰度的参数都在这里钉死,
// 并且让设备把"实际拿到的眼缓冲"报出来 —— 没有这行日志就只能靠猜真机为什么糊。
public class VRLibraryBoot : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void PinTextureFiltering()
    {
        QualitySettings.globalTextureMipmapLimit = 0;
        // 按每张贴图自身的 anisoLevel 启用各向异性,避免对 UI/光照贴图强行全局采样
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
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
        ApplyTargetFrameRate();
        Debug.Log("[VRLibrary] 清晰度自检(建场景后) " + Snapshot());
#if UNITY_EDITOR
        yield return RecoverLineVisuals();          // 仅编辑器:治 XR Device Simulator 的激活时序毛刺
        yield break;                                // 编辑器没有 XR 显示,再等也不会有眼缓冲数值
#else
        yield return new WaitForSecondsRealtime(1.5f);
        Debug.Log("[VRLibrary] 清晰度自检(1.5s 后) " + Snapshot());
#endif
    }

    /// <summary>
    /// 编辑器专用:XR Device Simulator 在原生输入回调里加虚拟设备/切 UI 悬停,会把部分手柄组件
    /// 带进"OnEnable 先于 Awake"的异常窗口 —— XRInteractorLineVisual 在这种窗口里量不到
    /// ILineRenderable,直接 enabled=false 自杀,这支手柄的激光线整局消失。这里在开场 2 秒后
    /// 把这类误杀的线视觉重新拉起来(此时 Awake 早已补跑完,OnEnable 能正常走通)。
    /// 真机没有模拟器,不存在此问题,不参与构建。
    /// </summary>
    IEnumerator RecoverLineVisuals()
    {
        yield return new WaitForSecondsRealtime(2f);
        int fixedCount = 0;
        var visuals = FindObjectsOfType<XRInteractorLineVisual>(true);
        foreach (var lv in visuals)
        {
            if (lv.enabled) continue;
            var ray = lv.GetComponent<XRBaseInteractor>();
            if (ray == null || !ray.isActiveAndEnabled) continue;
            lv.enabled = true;                       // 重走 OnEnable:此时 Awake 已完成,不会再自杀
            fixedCount++;
        }
        if (fixedCount > 0)
            Debug.LogWarning("[VRLibrary] 补活被模拟器时序误杀的线视觉 " + fixedCount + " 个(仅编辑器)");
    }

    // ------------------------------------------------------------ 帧率目标(与 ASW 联动)

    /// <summary>
    /// ASW 开着的时候,应用**必须按半帧率提交**(72Hz → 36),否则运行期没有可插合成的空档,等于白开。
    /// 这里**不写死常量**,而是去读 `PXR_Settings.enableAppSpaceWarp` —— 免得"菜单把 ASW 开了、
    /// 代码里的常量还是关的"这种两处不同步的坑(本工程已经在"档位写在哪儿"上踩过两次)。
    /// 走反射是为了不把 SDK 类型绑进程序集。
    /// </summary>
    static void ApplyTargetFrameRate()
    {
        if (Application.isEditor) return;
        int hz = Screen.currentResolution.refreshRate > 1 ? Screen.currentResolution.refreshRate : 72;
        bool asw = AswEnabled();
        Application.targetFrameRate = asw ? Mathf.Max(1, hz / 2) : hz;
        Debug.Log("[VRLibrary] 帧率目标 " + Application.targetFrameRate
            + "(" + (asw ? "ASW 已开 → 半刷新率" : "ASW 未开 → 跟刷新率") + ",刷新率 " + hz + "Hz)");
    }

    static bool AswEnabled()
    {
        try
        {
            var t = System.Type.GetType("Unity.XR.PXR.PXR_Settings, Unity.XR.PICO");
            if (t == null) return false;
            object inst = null;
            // SDK 3.x 提供静态 GetSettings(),SDK 2.5.3 运行时在静态字段 settings 里
            var mi = t.GetMethod("GetSettings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (mi != null) inst = mi.Invoke(null, null);
            if (inst == null)
            {
                var sfi = t.GetField("settings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (sfi != null) inst = sfi.GetValue(null);
            }
            if (inst == null)
            {
                var found = Resources.FindObjectsOfTypeAll(t);
                if (found != null && found.Length > 0) inst = found[0];
            }
            if (inst == null) return false;
            var fi = t.GetField("enableAppSpaceWarp",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi == null) return false;
            var v = fi.GetValue(inst);
            if (v is bool b) return b;
            return System.Convert.ToInt32(v) != 0;      // SDK 内部按 ushort 传,这里两种都认
        }
        catch { return false; }
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

    // 真机上 format 反映构建后压成的真实格式:只在编辑器/开发包中做全量内存扫描,避免 Release 启动扫全表。
    static string TexProbe()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        var all = Resources.FindObjectsOfTypeAll<Texture2D>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name != "T_TechFloor") continue;
            return "T_TechFloor " + all[i].width + "x" + all[i].height
                   + " mip=" + all[i].mipmapCount + " 过滤=" + all[i].filterMode
                   + " 各向异性=" + all[i].anisoLevel + " 格式=" + all[i].format;
        }
        return "T_TechFloor 不在包里";
#else
        return "TexProbe(Release跳过)";
#endif
    }
}
