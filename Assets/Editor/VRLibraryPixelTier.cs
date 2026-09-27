using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 性能③:像素成本与阴影档位。菜单 VRLibrary/性能·③ xxx。
///
/// 实测依据(Logs/perf-static.txt):
///   URP-Balanced:renderScale **1.25** × MSAA **4** → 片元像素成本 ≈ **6.3 倍**;
///   主光阴影图 **4096²**、阴影距离 **50 m**(室内场景);且阴影 pass 会把几何**再画一遍** ——
///   定点实测 Pad_Service 一站 4214 DrawCall,而全场渲染器只有 2352,多出来的就是阴影 pass。
///
/// 所以这里动四个数:renderScale 1.25→1.0、MSAA 4→2、主光阴影图 4096→2048、阴影距离 50→25。
/// 预期:片元成本 6.3 → 2.0 倍;阴影投射件大幅减少。
///
/// 注意两个"档位写在哪儿"的坑:
///   ① 真正生效的管线资产可能是 **QualitySettings 的逐档覆写**,不一定是 GraphicsSettings 那份 ——
///      所以下面把两者的并集都算进来,谁被引用就改谁;
///   ② `m_MainLightShadowmapResolution` 的 setter 是 internal,用属性写不进去 —— 统一走 SerializedObject。
/// </summary>
public static class VRLibraryPixelTier
{
    const string SnapshotDir = "Logs/perf-urp-snapshot";

    [MenuItem("VRLibrary/性能·③ 像素与阴影档位·现状(只报告)")]
    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能③ 像素与阴影档位:现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        foreach (var p in Pipes()) sb.AppendLine(Describe(p));
        sb.AppendLine("\n当前画质档:" + QualitySettings.GetQualityLevel() + " " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
        Flush(sb.ToString(), "Logs/perf-pixel.txt");
    }

    // 菜单名里不能出现 " / "(会被 Unity 当成子菜单层级,直接找不到)—— 所以这里用空格隔开
    [MenuItem("VRLibrary/性能·③ 应用像素档位(renderScale1.0 MSAA2 阴影2048 25m 级联1)")]
    public static void Apply() { Set(1.0f, 2, 2048, 25f, 1); }

    [MenuItem("VRLibrary/性能·③ 回退像素档位(renderScale1.25 MSAA4 阴影4096 50m 级联4)")]
    public static void Revert() { Set(1.25f, 4, 4096, 50f, 4); }

    static void Set(float renderScale, int msaa, int shadowRes, float shadowDist, int cascades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能③ 改档位 → renderScale " + renderScale + " / MSAA " + msaa
            + " / 主光阴影图 " + shadowRes + " / 阴影距离 " + shadowDist + "m / 级联 " + cascades + " ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Directory.CreateDirectory(SnapshotDir);

        foreach (var p in Pipes())
        {
            string path = AssetDatabase.GetAssetPath(p);
            string snap = Path.Combine(SnapshotDir, Path.GetFileName(path) + ".bak");
            try { if (!File.Exists(snap)) { File.Copy(path, snap); sb.AppendLine("  快照 " + snap); } }
            catch (Exception e) { sb.AppendLine("  !! 快照失败 " + e.Message); }

            sb.AppendLine("资产 " + path);
            sb.AppendLine("  改前 " + Describe(p).Replace("\n", "\n  "));
            // 顺序**必须**是:先用 SerializedObject 写"没有公开 setter"的字段并 Apply,再用反射写公开属性。
            // 反过来会被 SerializedObject 的缓存盖回去 —— 2026-09-27 实测:属性写 msaaSampleCount=2 报成功,
            // ApplyModifiedProperties 之后读回来还是 4(它把构造时缓存的 4 写了回去)。
            var so = new SerializedObject(p);
            bool ok = true;
            ok &= SetProp(p, so, "m_MainLightShadowmapResolution", null, shadowRes, sb);   // setter 是 internal,只能走字段
            so.ApplyModifiedPropertiesWithoutUndo();
            ok &= SetProp(p, so, "m_RenderScale", "renderScale", renderScale, sb);
            ok &= SetProp(p, so, "m_MSAA", "msaaSampleCount", msaa, sb);
            ok &= SetProp(p, so, "m_ShadowDistance", "shadowDistance", shadowDist, sb);
            ok &= SetProp(p, so, "m_ShadowCascadeCount", "shadowCascadeCount", cascades, sb);
            if (!ok) sb.AppendLine("  !! 有字段没写进去(名字对不上),看下面的改后读数自证");
            EditorUtility.SetDirty(p);
            sb.AppendLine("  改后 " + Describe(p).Replace("\n", "\n  "));
        }
        AssetDatabase.SaveAssets();
        sb.AppendLine("\n回退:菜单 VRLibrary/性能·③ 回退(…);或 copy " + SnapshotDir + "\\*.bak 回 Assets/Settings/");
        Flush(sb.ToString(), "Logs/perf-pixel-apply.txt");
    }

    /// <summary>
    /// 先试公开属性(URP 的 setter 会顺手维护内部一致性,例如 msaaSampleCount 会同步 _AnyShadowsSupported),
    /// 写不进去再退回序列化字段。枚举型字段(MSAA、阴影图分辨率)在 SerializedProperty 里是 Enum 类型,
    /// **不能当 Integer 直接塞**(上一次就是这么失败的:静默没写进去,读数自证才看出来),
    /// 这里对 Enum 用 intValue 写枚举的底层值,并在最后靠 read-back 自证。
    /// </summary>
    static bool SetProp(UniversalRenderPipelineAsset asset, SerializedObject so, string field, string prop, float v, StringBuilder sb)
    {
        if (prop != null)
        {
            var pi = asset.GetType().GetProperty(prop);
            if (pi != null && pi.CanWrite)
            {
                try
                {
                    object val = pi.PropertyType == typeof(float) ? (object)v
                        : pi.PropertyType == typeof(int) ? (object)Mathf.RoundToInt(v)
                        : Convert.ChangeType(Mathf.RoundToInt(v), pi.PropertyType);
                    pi.SetValue(asset, val, null);
                    sb.AppendLine("  属性 " + prop + " = " + v);
                    return true;
                }
                catch (Exception e) { sb.AppendLine("  属性 " + prop + " 写不进(" + e.Message + ")→ 退回字段 " + field); }
            }
        }
        var p = so.FindProperty(field);
        if (p == null) { sb.AppendLine("  !! 找不到字段 " + field); return false; }
        if (p.propertyType == SerializedPropertyType.Float) p.floatValue = v;
        else if (p.propertyType == SerializedPropertyType.Integer) p.intValue = Mathf.RoundToInt(v);
        else if (p.propertyType == SerializedPropertyType.Enum) p.intValue = Mathf.RoundToInt(v);
        else { sb.AppendLine("  !! 字段 " + field + " 类型不认识:" + p.propertyType); return false; }
        sb.AppendLine("  字段 " + field + " = " + v);
        return true;
    }

    static List<UniversalRenderPipelineAsset> Pipes()
    {
        var set = new List<UniversalRenderPipelineAsset>();
        var g = GraphicsSettings.renderPipelineAsset as UniversalRenderPipelineAsset;
        if (g != null) set.Add(g);
        int old = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            var q = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (q != null && !set.Contains(q)) set.Add(q);
        }
        QualitySettings.SetQualityLevel(old, false);
        return set;
    }

    static string Describe(UniversalRenderPipelineAsset p)
    {
        var so = new SerializedObject(p);
        Func<string, string> v = n => { var pr = so.FindProperty(n); return pr == null ? "?" : (pr.propertyType == SerializedPropertyType.Float ? pr.floatValue.ToString("0.###") : pr.intValue.ToString()); };
        float rs = F(v("m_RenderScale"), 1f); int ms = Mathf.RoundToInt(F(v("m_MSAA"), 1f));
        return "renderScale=" + v("m_RenderScale") + "  MSAA=" + v("m_MSAA")
            + "  片元成本≈" + (rs * rs * ms).ToString("0.0") + " 倍"
            + "\n主光阴影图=" + v("m_MainLightShadowmapResolution") + "  阴影距离=" + v("m_ShadowDistance") + "m"
            + "  级联=" + v("m_ShadowCascadeCount")
            + "\n主光阴影=" + v("m_MainLightShadowsSupported") + "  额外光阴影=" + v("m_AdditionalLightShadowsSupported")
            + "  软阴影=" + v("m_SoftShadowsSupported")
            + "  动态合批=" + v("m_SupportsDynamicBatching") + "  HDR=" + v("m_SupportsHDR");
    }

    static float F(string s, float dflt) { float f; return float.TryParse(s, out f) ? f : dflt; }

    static void Flush(string text, string path)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Pixel] 落盘失败:" + e.Message); }
        Debug.Log(text);
        Debug.Log("[Pixel] 报告 " + path);
    }
}