using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 场景灯色大替换:白→白青、青→极光蓝、粉/品红→芍药粉。
/// 只动发光源(Light 组件 / 自发光材质 / 天空盒发光色),不漫反射墙面天花板。
/// 换色按"保持原有亮度"换算,所以不会顺带改变曝光。
/// 幂等靠 IsTarget 的色相判定(不是靠"旧规则刚好不匹配"),换目标色重跑前最好先 git 回退,
/// 否则强度会在已补偿的基础上再补偿一次。
/// 回退:git checkout -- Assets/_Scenes Assets/Materials Assets/Skybox Assets/Scripts
/// </summary>
public static class LightColorSwap
{
    // ↓ 目标色,0-255。Light.color 和 _EmissionColor 都是线性倍率,不做 sRGB 解码,直接 /255,
    //   与工程里既有数值写法一致。
    static readonly Color White  = new Color(152f / 255f, 182f / 255f, 194f / 255f);
    static readonly Color Aurora = new Color(120f / 255f, 199f / 255f, 210f / 255f);
    static readonly Color Peony  = new Color(248f / 255f, 181f / 255f, 208f / 255f);

    /// <summary>
    /// 历史目标色:曾经定案、后来被换掉的色。必须显式登记,否则"已经改过一次"的灯
    /// 会变成分类器眼里的非灯色而被静默跳过 —— 旧雪白 rgb(255,254,24) 的 mx/mn=10.6,
    /// 既不满足白系阈值(<=1.5)也不满足青系判据,不登记就是 0 个待改的假通过。
    /// 归到哪个系就填下面的 Kind。
    /// </summary>
    static readonly Color[] LegacyWhite =
    {
        new Color(255f / 255f, 254f / 255f, 24f / 255f),   // v7.12~v7.13 的"雪白",实为饱和黄
    };

    /// <summary>带自发光、且属于"灯具/屏/引导光"的材质。书本封面和标牌的美术色不在名单里,避免连坐。</summary>
    static readonly string[] GlowMats =
    {
        "M_LightBar", "M_NeonCyan", "M_NeonMagenta", "M_GridLine", "M_TeleportPad", "M_ScreenOn", "M_ScreenDark"
    };

    const float LR = 0.2126f, LG = 0.7152f, LB = 0.0722f;
    static float Luma(Color c) { return LR * c.r + LG * c.g + LB * c.b; }

    /// 0=不是灯色, 1=白系, 2=青系, 3=粉/品红系
    /// 粉系必须排在白系前面:浅粉的 mx/mn 只有 1.37,会被白系阈值(<=1.5)误吞。
    static int Kind(Color c)
    {
        float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        if (mx < 0.1f) return 0;
        if (IsTarget(c)) return 0;
        if (IsLegacy(c, LegacyWhite)) return 1;
        if (c.r >= 0.2f && c.r >= c.g * 1.2f && c.b >= c.g * 1.05f) return 3;
        if (mn > 0.001f && mx / mn <= 1.5f && mx >= 0.35f) return 1;
        if (c.g >= c.r * 1.4f && c.b >= c.r * 1.4f && c.b >= c.g * 0.7f && mx / Mathf.Max(mn, 1e-4f) >= 2f) return 2;
        return 0;
    }

    static Color Target(int k) { return k == 1 ? White : k == 2 ? Aurora : Peony; }
    static string Label(int k) { return k == 1 ? "白→白青" : k == 2 ? "青→极光蓝" : "粉→芍药粉"; }

    /// <summary>色相(去掉亮度后的相对配比)已经等于某个目标色 → 视为改过,不再动。保证幂等。</summary>
    static bool IsTarget(Color c)
    {
        var a = Chromaticity(c);
        return Close(a, Chromaticity(White)) || Close(a, Chromaticity(Aurora)) || Close(a, Chromaticity(Peony));
    }

    /// <summary>是不是某个"上一版目标色"。换目标色后,场上残留的旧目标色要靠这里认领回来。</summary>
    static bool IsLegacy(Color c, Color[] list)
    {
        var a = Chromaticity(c);
        for (int i = 0; i < list.Length; i++)
            if (Close(a, Chromaticity(list[i]))) return true;
        return false;
    }

    static Color Chromaticity(Color c)
    {
        float l = Luma(c);
        return l < 1e-5f ? new Color(0, 0, 0, 1) : new Color(c.r / l, c.g / l, c.b / l, 1f);
    }

    static bool Close(Color a, Color b)
    {
        return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b))) < 0.02f;
    }

    /// 换成 target 的色相,亮度(对人眼)保持原样。
    static Color Keep(Color target, Color old)
    {
        float lt = Luma(target), lo = Luma(old);
        float k = lt < 1e-5f ? 1f : lo / lt;
        return new Color(target.r * k, target.g * k, target.b * k, old.a);
    }

    static string Rgb(Color c)
    {
        return string.Format("rgb({0},{1},{2})", Mathf.RoundToInt(c.r * 255),
                             Mathf.RoundToInt(c.g * 255), Mathf.RoundToInt(c.b * 255));
    }

    static IEnumerable<Light> AllLights()
    {
        foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
            if (l.hideFlags == HideFlags.None && l.gameObject.scene.IsValid()) yield return l;
    }

    [MenuItem("VRLibrary/灯色/扫描(只报告不改)")]
    static void Scan() { Report(false); }

    [MenuItem("VRLibrary/灯色/替换为 白青+极光蓝+芍药粉")]
    static void Apply() { Report(true); }

    /// <summary>
    /// 灯色替换只写 `_EmissionColor`,从不碰 `_EMISSION` 关键字 —— 于是 GlowMats 里的
    /// `M_ScreenOn` 是"色配了、关键字没活"的状态,场上其实不发光(坑 22)。
    /// 本菜单只补接线、不动任何颜色:非黑发光色 → `_EMISSION` + `RealtimeEmissive`。
    /// 没在 GlowMats 名单、也没接 `_EmissionMap` 的材质一律不碰(那是"没打算发光",不是 bug);
    /// 发光色为黑的(地板发光开关关掉那种)也只登记不改。
    /// </summary>
    [MenuItem("VRLibrary/灯色/补齐发光接线(只补 _EMISSION 与 GI 标记)")]
    static void FixEmissionWiring()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 补齐发光接线 ==");
        int n = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Materials", "Assets/Skybox" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null || !m.HasProperty("_EmissionColor")) continue;
            bool listed = System.Array.IndexOf(GlowMats, m.name) >= 0;
            bool hasMap = m.GetTexture("_EmissionMap") != null;
            if (!listed && !hasMap) continue;

            var ec = m.GetColor("_EmissionColor");
            if (ec.maxColorComponent <= 0.001f)
            { sb.AppendLine(string.Format("  {0,-16} 发光色是黑的 → 按「关」处理,不动", m.name)); continue; }
            bool kw = m.IsKeywordEnabled("_EMISSION");
            var gi = m.globalIlluminationFlags;
            if (kw && gi == MaterialGlobalIlluminationFlags.RealtimeEmissive)
            { sb.AppendLine(string.Format("  {0,-16} 接线已对(发光色 {1})", m.name, Rgb(ec))); continue; }

            Undo.RecordObject(m, "补齐发光接线");
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            EditorUtility.SetDirty(m);
            n++;
            sb.AppendLine(string.Format("  !! {0,-16} 补上 _EMISSION + RealtimeEmissive(原来 kw={1} gi={2} 发光色 {3})",
                m.name, kw, gi, Rgb(ec)));
        }
        AssetDatabase.SaveAssets();
        sb.AppendLine("合计补 " + n + " 份(只动接线,颜色一字未改)。重跑 VRLibrary/全场合理化体检 的 J 节复核");
        Debug.Log(sb.ToString());
        sb.Insert(0, string.Format("指纹 {0:0.000}  活动场景 {1}\n",
            EditorApplication.timeSinceStartup, EditorSceneManager.GetActiveScene().path));
        try
        {
            if (!System.IO.Directory.Exists("Logs")) System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.AppendAllText("Logs/lightswap-report.txt",
                sb + "\n----\n", new System.Text.UTF8Encoding(false));
        } catch (System.Exception e) { Debug.LogWarning("[灯色] 报告落盘失败:" + e.Message); }
    }

    static void Report(bool apply)
    {
        var sb = new StringBuilder();
        sb.AppendLine(apply ? "== 灯色替换 ==" : "== 灯色扫描(未改) ==");
        int nl = 0, nm = 0;

        sb.AppendLine("-- Light 组件(颜色直接给目标色,强度按亮度补偿) --");
        var skip = new List<string>();
        foreach (var l in AllLights())
        {
            int k = Kind(l.color);
            if (k == 0) { skip.Add(string.Format("{0,-22} {1}", l.gameObject.name, Rgb(l.color))); continue; }
            Color t = Target(k);
            float oldL = Luma(l.color), newL = Luma(t);
            float comp = newL < 1e-5f ? 1f : oldL / newL;
            string src = IsLegacy(l.color, LegacyWhite) ? "(旧目标色)" : "";
            sb.AppendLine(string.Format("  {0,-22} {1}{2} {3,-16} 强度 {4:0.000}→{5:0.000}",
                l.gameObject.name, Label(k), src, Rgb(t), l.intensity, l.intensity * comp));
            nl++;
            if (apply) { l.color = t; l.intensity *= comp; EditorUtility.SetDirty(l); }
        }

        if (skip.Count > 0)
        {
            sb.AppendLine("-- 判定为不改的光源(" + skip.Count + " 个) --");
            foreach (var s in skip) sb.AppendLine("  " + s);
        }

        sb.AppendLine("-- 材质 _EmissionColor / 天空盒发光色(色相换掉,发光量保持) --");
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Materials", "Assets/Skybox" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) continue;
            // 注意:天空盒材质根本没有 _EmissionColor,不能在这里提前 continue,
            // 否则下面那几个发光参数永远扫不到(v7.12 就这么漏掉的)。
            if (m.HasProperty("_EmissionColor") && System.Array.IndexOf(GlowMats, m.name) >= 0)
            {
                var old = m.GetColor("_EmissionColor");
                int k = Kind(old);
                if (k > 0)
                {
                    Color t = Keep(Target(k), old);
                    sb.AppendLine(string.Format("  {0,-16} {1} {2} → {3}   {4}", m.name,
                        Label(k), Rgb(old), Rgb(t), t.maxColorComponent > 1f ? "(HDR)" : ""));
                    nm++;
                    if (apply) { m.SetColor("_EmissionColor", t); EditorUtility.SetDirty(m); }
                }
            }

            if (!path.Contains("Skybox")) continue;
            foreach (var prop in new[] { "_MoonColor", "_HorizonGlowColor", "_HorizonLineColor" })
            {
                if (!m.HasProperty(prop)) continue;
                var old = m.GetColor(prop);
                int k = Kind(old);
                if (k == 0) continue;
                Color t = Target(k);
                t.a = old.a;
                sb.AppendLine(string.Format("  {0}.{1} {2} {3} → {4}", m.name, prop,
                    Label(k), Rgb(old), Rgb(t)));
                nm++;
                if (apply) { m.SetColor(prop, t); EditorUtility.SetDirty(m); }
            }
        }

        sb.AppendLine("-- DayNightController 序列化字段 --");
        foreach (var d in Resources.FindObjectsOfTypeAll<DayNightController>())
        {
            if (d.hideFlags != HideFlags.None || !d.gameObject.scene.IsValid()) continue;
            int k = Kind(d.lightBarOn);
            if (k == 0) continue;
            Color t = Keep(Target(k), d.lightBarOn);
            sb.AppendLine(string.Format("  {0}.lightBarOn {1} → {2}(灯灭态 {3} 是深色,不动)",
                d.gameObject.name, Rgb(d.lightBarOn), Rgb(t), Rgb(d.lightBarOff)));
            if (apply) { d.lightBarOn = t; EditorUtility.SetDirty(d); }
        }

        sb.AppendLine(string.Format("合计: {0} 个光源, {1} 个材质色{2}", nl, nm, apply ? " 已写入" : " 待改"));
        Debug.Log(sb.ToString());

        // Editor.log 是同机多个编辑器共用且带缓冲的,光靠它是分不清"本次结果"和"上次结果"的,
        // 所以报告自己落盘,并带一个每次必然变化的指纹:两个指纹相同 = 根本没跑。
        sb.Insert(0, string.Format("指纹 {0:0.000}  活动场景 {1}\n",
            EditorApplication.timeSinceStartup,
            EditorSceneManager.GetActiveScene().path));
        try
        {
            if (!System.IO.Directory.Exists("Logs")) System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.WriteAllText("Logs/lightswap-report.txt", sb.ToString(),
                new System.Text.UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[灯色] 报告落盘失败: " + e.Message); }

        if (!apply) return;

        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        Debug.Log("[灯色] 已保存。截图验收:VRLibrary/取景(读Logs/frame.txt) → node Tools/shot.mjs lights-now");
    }
}
