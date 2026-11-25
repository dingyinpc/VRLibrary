#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 2026-11-07 真机 GPU 瘦身(第一档)。
///
/// 现象:一体机 27 fps 稳定,CPU 约 20%,GPU 占满。
/// 静态体检结论:3.8 万三角面 / 37 材质 / 贴图 0.8MB —— 几何与带宽都很小,
/// 真正吃 GPU 的是"全屏 pass":
///   1) SSAO RendererFeature 仍是激活状态。相机 renderPostProcessing=False 并不会拦住它
///      (URP 14 的 SSAO 是 renderer feature,Setup 里只校验 Intensity/Radius/Falloff),
///      所以它每帧仍在跑 深度 + AO + 模糊;
///   2) URP 资产 HDR=开,MSAA=2x —— 移动端 tile GPU 的眼缓冲带宽翻倍;
///   3) 19 盏灯全是 Baked,但 Moon Light 仍开着实时阴影 → 每帧多一整个 shadow pass。
///
/// 本工具做四件事(都可在"回退"菜单里还原):
///   · 关 SSAO(URP-Balanced-Renderer 上的 RendererFeature)
///   · 关 HDR(URP 资产 + 场景相机 allowHDR)
///   · 关主光实时阴影 + 软阴影,阴影图 2048→1024
/// 报告:Logs/perf-gpu-slim.txt
/// </summary>
public static class VRLibraryGpuSlim
{
    const string UrpAssetPath = "Assets/Settings/URP-Balanced.asset";
    const string UrpRendererPath = "Assets/Settings/URP-Balanced-Renderer.asset";
    const string ReportPath = "Logs/perf-gpu-slim.txt";

    [MenuItem("VRLibrary/性能·⑬ GPU瘦身·应用(关SSAO/HDR/实时阴影)", false, 520)]
    public static void Apply()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== GPU 瘦身(第一档) ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp == null) { sb.AppendLine("!! 找不到 " + UrpAssetPath); Flush(sb); return; }

        // 1) URP 资产:HDR / 软阴影 / 阴影图
        var so = new SerializedObject(urp);
        SetBool(so, "m_SupportsHDR", false, sb, "HDR");
        SetBool(so, "m_SoftShadowsSupported", false, sb, "软阴影");
        SetInt(so, "m_MainLightShadowmapResolution", 1024, sb, "主光阴影图");
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(urp);

        // 2) Renderer Feature:关掉 SSAO(以及任何其它全屏特性)
        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
        if (rd == null)
        {
            sb.AppendLine("!! 找不到 " + UrpRendererPath);
        }
        else
        {
            var rso = new SerializedObject(rd);
            var list = rso.FindProperty("m_RendererFeatures");
            if (list != null && list.isArray)
            {
                for (int i = 0; i < list.arraySize; i++)
                {
                    var el = list.GetArrayElementAtIndex(i);
                    var feat = el.objectReferenceValue as ScriptableRendererFeature;
                    if (feat == null) continue;
                    var fso = new SerializedObject(feat);
                    var act = fso.FindProperty("m_Active");
                    if (act != null && act.boolValue)
                    {
                        act.boolValue = false;
                        fso.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(feat);
                        sb.AppendLine("  关闭 RendererFeature: " + feat.name);
                    }
                }
            }
            else sb.AppendLine("  (该 Renderer 没有 RendererFeature 列表)");
            rso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rd);
        }

        // 3) 场景:相机 HDR、主光实时阴影
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.IsValid())
        {
            int camHdrOff = 0;
            foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            {
                if (!cam.allowHDR) continue;
                cam.allowHDR = false;
                camHdrOff++;
            }
            sb.AppendLine("  相机 allowHDR 关闭 ×" + camHdrOff);

            int lightShadowOff = 0;
            foreach (var l in Object.FindObjectsOfType<Light>(true))
            {
                if (l.shadows == LightShadows.None) continue;
                l.shadows = LightShadows.None;
                lightShadowOff++;
                sb.AppendLine("  关闭实时阴影: " + l.name + " (" + l.type + ")");
            }
            if (lightShadowOff == 0) sb.AppendLine("  没有开实时阴影的灯");

            EditorSceneManager.MarkSceneDirty(scene);
            sb.AppendLine("  场景已保存 = " + EditorSceneManager.SaveScene(scene));
        }

        AssetDatabase.SaveAssets();
        sb.AppendLine();
        sb.AppendLine(Describe());
        Flush(sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑬ GPU瘦身·复核(只报告)", false, 521)]
    public static void Verify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== GPU 瘦身 · 复核 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(Describe());

        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
        if (rd != null)
        {
            var rso = new SerializedObject(rd);
            var list = rso.FindProperty("m_RendererFeatures");
            if (list != null && list.isArray)
                for (int i = 0; i < list.arraySize; i++)
                {
                    var feat = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererFeature;
                    if (feat == null) continue;
                    var act = new SerializedObject(feat).FindProperty("m_Active");
                    sb.AppendLine("  RendererFeature " + feat.name + " m_Active = " + (act != null ? act.boolValue.ToString() : "?"));
                }
        }

        foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            sb.AppendLine("  相机 " + cam.name + " allowHDR=" + cam.allowHDR + " far=" + cam.farClipPlane);

        foreach (var l in Object.FindObjectsOfType<Light>(true))
            if (l.type == LightType.Directional)
                sb.AppendLine("  方向光 " + l.name + " 模式=" + l.lightmapBakeType + " 阴影=" + l.shadows);

        Flush(sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑬ GPU瘦身·回退(恢复HDR/软阴影/实时阴影)", false, 522)]
    public static void Revert()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== GPU 瘦身 · 回退 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp != null)
        {
            var so = new SerializedObject(urp);
            SetBool(so, "m_SupportsHDR", true, sb, "HDR");
            SetBool(so, "m_SoftShadowsSupported", true, sb, "软阴影");
            SetInt(so, "m_MainLightShadowmapResolution", 2048, sb, "主光阴影图");
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();
        }

        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
        if (rd != null)
        {
            var rso = new SerializedObject(rd);
            var list = rso.FindProperty("m_RendererFeatures");
            if (list != null && list.isArray)
                for (int i = 0; i < list.arraySize; i++)
                {
                    var feat = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererFeature;
                    if (feat == null) continue;
                    var fso = new SerializedObject(feat);
                    var act = fso.FindProperty("m_Active");
                    if (act != null) { act.boolValue = true; fso.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(feat); sb.AppendLine("  恢复 RendererFeature: " + feat.name); }
                }
            rso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rd);
        }

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.IsValid())
        {
            foreach (var cam in Object.FindObjectsOfType<Camera>(true)) cam.allowHDR = true;
            foreach (var l in Object.FindObjectsOfType<Light>(true))
                if (l.type == LightType.Directional) l.shadows = LightShadows.Soft;
            EditorSceneManager.MarkSceneDirty(scene);
            sb.AppendLine("  场景已保存 = " + EditorSceneManager.SaveScene(scene));
        }

        AssetDatabase.SaveAssets();
        sb.AppendLine();
        sb.AppendLine(Describe());
        Flush(sb);
        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------------------ 工具

    static string Describe()
    {
        var sb = new StringBuilder();
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp != null)
        {
            var so = new SerializedObject(urp);
            sb.AppendLine("URP " + UrpAssetPath);
            sb.AppendLine("  renderScale=" + GetF(so, "m_RenderScale") +
                          "  MSAA=" + GetInt(so, "m_MSAA") +
                          "  HDR=" + GetBool(so, "m_SupportsHDR") +
                          "  软阴影=" + GetBool(so, "m_SoftShadowsSupported"));
            sb.AppendLine("  主光阴影图=" + GetInt(so, "m_MainLightShadowmapResolution") +
                          "  阴影距离=" + GetF(so, "m_ShadowDistance") +
                          "  级联=" + GetInt(so, "m_ShadowCascadeCount"));
        }
        return sb.ToString().TrimEnd();
    }

    static void SetBool(SerializedObject so, string prop, bool v, StringBuilder sb, string label)
    {
        var p = so.FindProperty(prop);
        if (p == null) { sb.AppendLine("  !! 没有字段 " + prop); return; }
        p.boolValue = v;
        sb.AppendLine("  " + label + "(" + prop + ") → " + v);
    }

    static void SetInt(SerializedObject so, string prop, int v, StringBuilder sb, string label)
    {
        var p = so.FindProperty(prop);
        if (p == null) { sb.AppendLine("  !! 没有字段 " + prop); return; }
        p.intValue = v;
        sb.AppendLine("  " + label + "(" + prop + ") → " + v);
    }

    static int GetInt(SerializedObject so, string prop) { var p = so.FindProperty(prop); return p == null ? -1 : p.intValue; }
    static bool GetBool(SerializedObject so, string prop) { var p = so.FindProperty(prop); return p != null && p.boolValue; }
    static float GetF(SerializedObject so, string prop) { var p = so.FindProperty(prop); return p == null ? -1f : p.floatValue; }

    static void Flush(StringBuilder sb)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false)); }
        catch (System.Exception e) { Debug.LogWarning("[GpuSlim] 报告落盘失败:" + e.Message); }
    }
}
#endif
