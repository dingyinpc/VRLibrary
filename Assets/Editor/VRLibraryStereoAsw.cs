using System;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 性能⑦:真机侧的三件套 —— **Multiview / 72Hz / 晚锁存**,以及可选的 **ASW**。
/// 菜单 VRLibrary/性能·⑦ xxx。原理见知识库 `XR学习知识库/10-立体渲染与ASW应用空间扭曲.md`。
///
/// 为什么这三件必须一起做(实测证据):
///   ① 上一版 APK 的 manifest 里 `rendering_mode = 0`(**MultiPass**)—— 真机上左右眼**各提交一遍**,
///      编辑器是单眼 mono,所以这部分开销**在编辑器里永远量不出来**。改 Multiview 是 `0 → 1`。
///   ② `display_rate = 0`(Default)—— 设备多半按 90Hz 走 = **11.1ms** 预算;钉 72Hz 就是 **13.9ms**,白得 25%。
///   ③ 晚锁存(`pvr.LateLatching`)只降延迟不增帧率,代价极小,顺手开。
///
/// ASW 单独一个菜单,因为它**有前提**:PICO SDK 的编辑器代码里写死了三条门槛
/// (Unity 2021 LTS+ / 图形 API 首位 Vulkan / **立体模式不能是 MultiPass**),
/// 不满足就强制关掉并提示原因 —— 所以顺序只能是"先 Multiview,再 ASW"。
/// 而且 ASW 要额外付深度+运动矢量的渲染开销(见 PXR_Manager.cs:587),**不是白拿的**:
/// 先量一次,GPU 贴得住 13.9ms 就别开。开的时候**必须同时把 targetFrameRate 设成半刷新率**
/// (运行时由 VRLibraryBoot 按 PXR_Settings 自动设,免得菜单和代码两处不同步)。
///
/// 回退:`性能·⑦ 回退(回 MultiPass + Default + 关 ASW)`。
/// </summary>
public static class VRLibraryStereoAsw
{
    const string PxrSettings = "Assets/XR/Settings/PXR_Settings.asset";
    const string ProjectSettings = "ProjectSettings/ProjectSettings.asset";
    const string ProjConfig = "Assets/Resources/PXR_ProjectSetting.asset";
    const string ReportPath = "Logs/perf-stereo.txt";

    // PICO 枚举:StereoRenderingModeAndroid { MultiPass = 0, Multiview = 1 }
    //           SystemDisplayFrequency { Default = 0, RefreshRate72 = 72, RefreshRate90 = 90, RefreshRate120 = 120 }

    [MenuItem("VRLibrary/性能·⑦ 立体与ASW·现状(只报告)")]
    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑦ 立体渲染与 ASW:现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(Describe());
        sb.AppendLine("\n---- SDK 里 ASW 的三条硬门槛(源码写死)----");
        sb.AppendLine("  1) Unity 2021 LTS 或更新:" + (Application.unityVersion.StartsWith("2022") || Application.unityVersion.StartsWith("2023") ? "过" : "存疑") + "(" + Application.unityVersion + ")");
        sb.AppendLine("  2) 图形 API 首位必须是 Vulkan:" + GraphicsApis());
        sb.AppendLine("  3) 立体模式不能是 MultiPass(见上面读数)");
        sb.AppendLine("\n提示:ASW 不写进 manifest(由 PXR_Loader 初始化时交给原生层),只能在真机 logcat 里看运行期日志。");
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑦ 应用真机三件套(Multiview + 72Hz + 晚锁存)")]
    public static void ApplyThree()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑦ 应用真机三件套 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("改前:\n" + Describe());

        var so = Load(PxrSettings);
        if (so == null) return;
        SetInt(so, "stereoRenderingModeAndroid", 1, sb, "立体模式 MultiPass → Multiview");
        SetInt(so, "systemDisplayFrequency", 72, sb, "刷新率 Default → 72Hz(预算 11.1ms → 13.9ms)");
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();

        // 晚锁存在项目设置里(构建时写 manifest 的 pvr.LateLatching)
        var pc = Load(ProjConfig);
        if (pc != null)
        {
            SetInt(pc, "latelatching", 1, sb, "晚锁存 0 → 1(只降延迟,几乎免费)");
            pc.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        // Unity 自己的老设置(内置 XR 用;XR SDK 下由加载器接管,写上只为一致,不写也行)
        var ps = AssetDatabase.LoadAllAssetsAtPath(ProjectSettings);
        if (ps != null && ps.Length > 0)
        {
            var so2 = new SerializedObject(ps[0]);
            if (SetInt(so2, "m_StereoRenderingPath", 2, sb, "ProjectSettings 立体路径 0(MultiPass) → 2(Instanced)"))
            { so2.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssets(); }
        }

        sb.AppendLine("\n改后:\n" + Describe());
        sb.AppendLine("\n下一步:打一次 APK,用 aapt 验 manifest 的 rendering_mode=1 / display_rate=72 / pvr.LateLatching=1。");
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑦ 开 ASW(前提:已 Multiview)")]
    public static void AswOn()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑦ 开 ASW ==");
        sb.AppendLine("改前:\n" + Describe());
        var so = Load(PxrSettings);
        if (so == null) return;
        var stereo = so.FindProperty("stereoRenderingModeAndroid");
        if (stereo != null && stereo.intValue == 0)
        {
            sb.AppendLine("  !! 立体模式还是 MultiPass —— SDK 会强制把 ASW 关掉,先跑「应用真机三件套」。本次不写。");
            Flush(sb.ToString());
            return;
        }
        SetInt(so, "enableAppSpaceWarp", 1, sb, "ASW 0 → 1");
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        sb.AppendLine("\n改后:\n" + Describe());
        sb.AppendLine("\n⚠ 开 ASW 后应用应**按半帧率**渲染:VRLibraryBoot 会在真机上自动把 targetFrameRate 设成刷新率的一半并打日志。");
        sb.AppendLine("⚠ 先确认 GPU 是不是真的贴不住 13.9ms —— 贴得住就别开(白送重影)。");
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑦ 关 ASW")]
    public static void AswOff()
    {
        var so = Load(PxrSettings);
        if (so == null) return;
        var sb = new StringBuilder("== 性能⑦ 关 ASW ==\n改前:\n" + Describe());
        SetInt(so, "enableAppSpaceWarp", 0, sb, "ASW 1 → 0");
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        sb.AppendLine("\n改后:\n" + Describe());
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑦ 回退(回 MultiPass + Default + 关 ASW)")]
    public static void Revert()
    {
        var sb = new StringBuilder("== 性能⑦ 回退 ==\n改前:\n" + Describe());
        var so = Load(PxrSettings);
        if (so != null)
        {
            SetInt(so, "stereoRenderingModeAndroid", 0, sb, "立体模式 → MultiPass");
            SetInt(so, "systemDisplayFrequency", 0, sb, "刷新率 → Default");
            SetInt(so, "enableAppSpaceWarp", 0, sb, "ASW → 关");
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
        var pc = Load(ProjConfig);
        if (pc != null)
        {
            SetInt(pc, "latelatching", 0, sb, "晚锁存 → 0");
            pc.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
        var ps = AssetDatabase.LoadAllAssetsAtPath(ProjectSettings);
        if (ps != null && ps.Length > 0)
        {
            var so2 = new SerializedObject(ps[0]);
            if (SetInt(so2, "m_StereoRenderingPath", 0, sb, "ProjectSettings 立体路径 → 0"))
            { so2.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssets(); }
        }
        sb.AppendLine("\n改后:\n" + Describe());
        Flush(sb.ToString());
    }

    // ------------------------------------------------------------ 工具

    static SerializedObject Load(string path)
    {
        var o = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        if (o == null) { Debug.LogError("[Stereo] 找不到 " + path); return null; }
        return new SerializedObject(o);
    }

    static bool SetInt(SerializedObject so, string field, int v, StringBuilder sb, string note)
    {
        var p = so.FindProperty(field);
        if (p == null) { sb.AppendLine("  !! 找不到字段 " + field); return false; }
        sb.AppendLine("  " + note + "   [" + field + ": " + p.intValue + " → " + v + "]");
        p.intValue = v;
        return true;
    }

    static string Describe()
    {
        var sb = new StringBuilder();
        var so = Load(PxrSettings);
        if (so != null)
        {
            Func<string, string> v = n => { var p = so.FindProperty(n); return p == null ? "?" : p.intValue.ToString(); };
            int stereo = int.Parse(v("stereoRenderingModeAndroid"));
            sb.AppendLine("  PXR_Settings 立体模式 = " + stereo + "(" + (stereo == 0 ? "MultiPass(双眼各画一遍)" : "Multiview(一趟画两只眼)") + ")");
            sb.AppendLine("  PXR_Settings 刷新率   = " + v("systemDisplayFrequency") + "(0=Default, 72/90/120)");
            sb.AppendLine("  PXR_Settings ASW      = " + v("enableAppSpaceWarp") + (v("enableAppSpaceWarp") == "1" ? "(开)" : "(关)"));
        }
        var pc = Load(ProjConfig);
        if (pc != null)
        {
            var p = pc.FindProperty("latelatching");
            sb.AppendLine("  PXR_ProjectSetting 晚锁存 = " + (p == null ? "?" : p.intValue.ToString()));
        }
        return sb.ToString().TrimEnd();
    }

    static string GraphicsApis()
    {
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
        var names = new StringBuilder();
        for (int i = 0; i < apis.Length; i++)
        {
            names.Append(apis[i]);
            if (i == 0) names.Append("(首位)");
            if (i < apis.Length - 1) names.Append(" > ");
        }
        bool ok = apis.Length > 0 && apis[0] == UnityEngine.Rendering.GraphicsDeviceType.Vulkan;
        return names + (ok ? "  ✓ 满足" : "  ✗ 不满足(ASW 会被强制关闭)");
    }

    static void Flush(string text)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText(ReportPath, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Stereo] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}