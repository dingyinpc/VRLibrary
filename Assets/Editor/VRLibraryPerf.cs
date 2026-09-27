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
/// 性能体检(全部只报告,不改场景)。菜单 VRLibrary/性能·xxx。
///
/// 存在的理由:帧率低这件事**不能靠猜**。这个工程里"看起来贵"和"真的很贵"经常是两回事
/// —— 地砖贴图在 Android 上已经是 1024 / ASTC 6x6(约 0.6 MB),它根本不是显存大头;
/// 真正吃帧率的是**绘制批次**(740 件书架/书/文字牌,每件一个 MeshRenderer + TextMesh)
/// 和**像素成本**(renderScale 1.25 × MSAA 4 = 片元开销约 6.3 倍)。
///
/// 所以这里量三本账:
///   ① 静态账:每个顶层分组的 物体/渲染器/三角面/材质/贴图 与静态合批标记;
///   ② 贴图账:按 **Android 实际导入格式**折算显存(编辑器里的 RGBA32 不是真机占用);
///   ③ 露台账:星空观影那棵子树单独一本账 + **跨引用图**(能不能抠成预制体就看这张图)。
///
/// 报告落盘 Logs/ 下,同时 Debug.Log 一份(桥能直接读回)。
/// </summary>
public static class VRLibraryPerf
{
    const string Root = "SmartLibrary";
    static StringBuilder sb;

    // ============================================================ ① 静态账

    [MenuItem("VRLibrary/性能·静态体检(只报告)")]
    public static void StaticAudit()
    {
        sb = Head("静态体检");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        sb.AppendLine("场景 " + scene.name + " 顶层根 " + roots.Length + " 个");

        var all = new List<GameObject>();
        foreach (var r in roots) Collect(r.transform, all);
        sb.AppendLine("物体总数 " + all.Count);

        // ---- 全局组件计数
        var rends = all.SelectMany(g => g.GetComponents<Renderer>()).Where(r => r != null).ToList();
        var mfs = all.SelectMany(g => g.GetComponents<MeshFilter>()).Where(m => m != null).ToList();
        var lods = all.SelectMany(g => g.GetComponents<LODGroup>()).ToArray();
        var lights = all.SelectMany(g => g.GetComponents<Light>()).ToArray();
        var cols = all.SelectMany(g => g.GetComponents<Collider>()).ToArray();
        var canvases = all.SelectMany(g => g.GetComponents<Canvas>()).ToArray();
        var tms = all.SelectMany(g => g.GetComponents<TextMesh>()).ToArray();
        var parts = all.SelectMany(g => g.GetComponents<ParticleSystem>()).ToArray();
        var anims = all.SelectMany(g => g.GetComponents<Animator>()).ToArray();
        var auds = all.SelectMany(g => g.GetComponents<AudioSource>()).ToArray();
        var rigs = all.SelectMany(g => g.GetComponents<ReflectionProbe>()).ToArray();
        var cams = all.SelectMany(g => g.GetComponents<Camera>()).ToArray();

        int tris = 0, verts = 0, submesh = 0;
        var meshes = new HashSet<Mesh>();
        var mats = new HashSet<Material>();
        var shaders = new HashSet<Shader>();
        foreach (var r in rends)
        {
            var m = MeshOf(r);
            if (m == null) continue;
            if (meshes.Add(m)) { verts += m.vertexCount; tris += (int)(m.GetIndexCount(0) / 3) * Mathf.Max(1, m.subMeshCount); }
            else { verts += m.vertexCount; tris += (int)(m.GetIndexCount(0) / 3) * Mathf.Max(1, m.subMeshCount); }
            submesh += Mathf.Max(1, m.subMeshCount);
            foreach (var mm in r.sharedMaterials) if (mm != null) { mats.Add(mm); if (mm.shader != null) shaders.Add(mm.shader); }
        }

        sb.AppendLine("渲染器 " + rends.Count + "(MeshRenderer " + rends.Count(r => r is MeshRenderer)
            + " / Skinned " + rends.Count(r => r is SkinnedMeshRenderer)
            + " / Particle " + rends.Count(r => r is ParticleSystemRenderer) + ")");
        sb.AppendLine("三角面 " + tris.ToString("N0") + "  顶点 " + verts.ToString("N0")
            + "  子网格(≈SetPass) " + submesh.ToString("N0")
            + "  唯一网格 " + meshes.Count + "  唯一材质 " + mats.Count + "  唯一 Shader " + shaders.Count);
        sb.AppendLine("LODGroup " + lods.Length + "  Canvas " + canvases.Length + "  TextMesh " + tms.Length
            + "  粒子 " + parts.Length + "  Animator " + anims.Length + "  AudioSource " + auds.Length
            + "  反射探针 " + rigs.Length + "  Camera " + cams.Length);
        sb.AppendLine("碰撞体 " + cols.Length);

        // ---- 静态标记(移动端合批的前提)
        int batching = 0, gi = 0, occluder = 0, occludee = 0, none = 0;
        foreach (var g in all)
        {
            if (g.GetComponent<Renderer>() == null) continue;
            var f = GameObjectUtility.GetStaticEditorFlags(g);
            if ((f & StaticEditorFlags.BatchingStatic) != 0) batching++;
            if ((f & StaticEditorFlags.ContributeGI) != 0) gi++;
            if ((f & StaticEditorFlags.OccluderStatic) != 0) occluder++;
            if ((f & StaticEditorFlags.OccludeeStatic) != 0) occludee++;
            if (f == 0) none++;
        }
        sb.AppendLine("静态标记(有渲染器的物体):BatchingStatic " + batching + "  ContributeGI " + gi
            + "  Occluder " + occluder + "  Occludee " + occludee + "  **完全没打标** " + none);
        sb.AppendLine("  → 没打 BatchingStatic 的每件都是独立 draw call;没打 Occludee 的遮挡剔除永远剔不掉它");

        // ---- 灯光
        sb.AppendLine("灯光:平行 " + lights.Count(l => l.type == LightType.Directional)
            + "  点 " + lights.Count(l => l.type == LightType.Point)
            + "  聚光 " + lights.Count(l => l.type == LightType.Spot)
            + "  开阴影 " + lights.Count(l => l.shadows != LightShadows.None)
            + "  实时模式 " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime)
            + "  Baked " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked)
            + "  Mixed " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed));
        foreach (var l in lights.Where(l => l.shadows != LightShadows.None).Take(12))
            sb.AppendLine("    [投影] " + Path(l.transform) + " " + l.type + " 强度 " + l.intensity.ToString("0.0")
                + " 距离 " + (l.type == LightType.Directional ? "-" : l.range.ToString("0.0"))
                + " 烘焙=" + l.lightmapBakeType);

        // ---- 光照贴图/GI
        var lms = LightmapSettings.lightmaps;
        long lmBytes = 0; foreach (var lm in lms) if (lm.lightmapColor != null) lmBytes += EstBytes(lm.lightmapColor);
        sb.AppendLine("光照贴图 " + lms.Length + " 张 ≈ " + MB(lmBytes)
            + "  环境光=" + RenderSettings.ambientMode + "  雾=" + RenderSettings.fog);
        if (lms.Length == 0) sb.AppendLine("  ⚠ 没有烘焙光照贴图 → 全场照明必须靠实时灯+自发光,这是帧率与'远处糊'的常见根因");

        // ---- 摄像机与像素成本
        var pipe = (QualitySettings.renderPipeline as UniversalRenderPipelineAsset)
                   ?? (GraphicsSettings.renderPipelineAsset as UniversalRenderPipelineAsset);
        if (pipe != null)
        {
            int msaa = Get(pipe, "msaaSampleCount", 1);
            float rs = GetF(pipe, "renderScale", 1f);
            sb.AppendLine("管线:" + AssetDatabase.GetAssetPath(pipe)
                + "\n  renderScale=" + rs.ToString("0.00") + "  MSAA=" + msaa + "×"
                + "  → 片元像素成本 ≈ " + (rs * rs * msaa).ToString("0.0") + " 倍"
                + "\n  主光阴影图 " + Get(pipe, "mainLightShadowmapResolution", 0) + "²"
                + "  额外光阴影 " + (Get(pipe, "supportsAdditionalLightShadows", 0) == 1 ? "开(" + Get(pipe, "additionalLightsShadowmapResolution", 0) + "²)" : "关")
                + "  阴影距离 " + GetF(pipe, "shadowDistance", 0f).ToString("0.0")
                + "\n  动态合批 " + (Get(pipe, "supportsDynamicBatching", 0) == 1 ? "开" : "关")
                + "  SRP Batcher " + (Get(pipe, "useSRPBatcher", 0) == 1 ? "开" : "关")
                + "  HDR=" + (Get(pipe, "supportsHDR", 0) == 1));
        }
        foreach (var c in cams)
            sb.AppendLine("相机 " + Path(c.transform) + " HDR=" + c.allowHDR + " MSAA=" + c.allowMSAA
                + " 后处理=" + c.GetUniversalAdditionalCameraData().renderPostProcessing
                + " far=" + c.farClipPlane + " 层=" + c.cullingMask);
        int vol = all.SelectMany(g => g.GetComponents<Volume>()).Count();
        sb.AppendLine("Volume(后处理) " + vol + " 个" + (vol > 0 ? "  → Bloom/景深在一体机上是整屏多次采样,最贵的后处理" : ""));

        // ---- 按顶层分组:直接按 SmartLibrary 的一级子物体分更直观
        sb.AppendLine("\n---- 按顶层分组(按三角面降序)----");
        var rootGo = all.FirstOrDefault(g => g.name == Root);
        if (rootGo != null)
        {
            sb.AppendLine("(根 " + Root + " 一级子物体)");
            var rows = new List<Tuple<int, string>>();
            foreach (Transform k in rootGo.transform)
            {
                var kids = new List<GameObject>(); Collect(k, kids);
                int tk = 0; foreach (var g in kids) tk += TrisOf(g);
                rows.Add(Tuple.Create(tk, Ledger(kids, k.name)));
            }
            foreach (var row in rows.OrderByDescending(x => x.Item1)) sb.AppendLine("  " + row.Item2);
        }

        // ---- 最贵的单体
        sb.AppendLine("\n---- 三角面最多的 25 个物体 ----");
        foreach (var g in all.OrderByDescending(TrisOf).Take(25))
            sb.AppendLine("  " + TrisOf(g).ToString("N0") + " 面  " + Path(g.transform));

        Flush("Logs/perf-static.txt");
    }

    static string Ledger(List<GameObject> kids, string label = null)
    {
        int tris = 0, rends = 0, txt = 0, stat = 0;
        var mats = new HashSet<Material>();
        foreach (var g in kids)
        {
            var r = g.GetComponent<Renderer>();
            if (r != null)
            {
                rends++; tris += TrisOf(g);
                foreach (var m in r.sharedMaterials) if (m != null) mats.Add(m);
                if ((GameObjectUtility.GetStaticEditorFlags(g) & StaticEditorFlags.BatchingStatic) != 0) stat++;
            }
            if (g.GetComponent<TextMesh>() != null) txt++;
        }
        return (label == null ? "" : label.PadRight(22))
            + " 物体 " + kids.Count.ToString().PadLeft(5)
            + "  渲染器 " + rends.ToString().PadLeft(5)
            + "  三角面 " + tris.ToString("N0").PadLeft(11)
            + "  材质 " + mats.Count.ToString().PadLeft(4)
            + "  TextMesh " + txt.ToString().PadLeft(4)
            + "  静态合批标记 " + stat;
    }

    // ============================================================ ② 贴图账

    [MenuItem("VRLibrary/性能·贴图账(只报告)")]
    public static void TextureAudit()
    {
        sb = Head("贴图账(按 Android 实际导入格式折算)");
        var all = new List<GameObject>();
        foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) Collect(r.transform, all);

        var tex = new Dictionary<Texture2D, List<string>>();
        var mats = new HashSet<Material>();
        foreach (var g in all) foreach (var r in g.GetComponents<Renderer>())
            foreach (var m in r.sharedMaterials) if (m != null) mats.Add(m);
        foreach (var m in mats)
        {
            foreach (var p in m.GetTexturePropertyNames())
            {
                var t = m.GetTexture(p) as Texture2D;
                if (t == null) continue;
                List<string> who;
                if (!tex.TryGetValue(t, out who)) { who = new List<string>(); tex[t] = who; }
                if (who.Count < 3) who.Add(m.name + "." + p);
            }
        }
        // 场景里直接挂的纹理(天空盒/光照贴图/UI)
        foreach (var t in all.SelectMany(g => g.GetComponents<UnityEngine.UI.RawImage>()).Select(x => x.texture as Texture2D))
            if (t != null && !tex.ContainsKey(t)) tex[t] = new List<string> { "RawImage" };

        sb.AppendLine("场景引用到的贴图 " + tex.Count + " 张,material 唯一数 " + mats.Count);

        long total = 0, totalEditor = 0;
        var rows = new List<Tuple<long, string>>();
        foreach (var kv in tex)
        {
            var t = kv.Key;
            string path = AssetDatabase.GetAssetPath(t);
            var imp = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            int w = t.width, h = t.height;
            long android = EstAndroid(t, imp, out string fmtNote, out int aw, out int ah);
            long editor = (long)w * h * Bpp(t.format) / 8 * 4 / 3;
            total += android; totalEditor += editor;
            rows.Add(Tuple.Create(android, "  " + Path0(t.name, 26)
                + " 源 " + w + "×" + h
                + " → Android " + aw + "×" + ah + " " + fmtNote
                + " ≈ " + MB(android).PadLeft(9)
                + "  (编辑器 " + t.format + " ≈ " + MB(editor) + ")"
                + "  用它的:" + string.Join(",", kv.Value.ToArray())));
        }
        foreach (var r in rows.OrderByDescending(x => x.Item1)) sb.AppendLine(r.Item2);
        sb.AppendLine("\n合计:Android ≈ " + MB(total) + "   编辑器 ≈ " + MB(totalEditor));
        sb.AppendLine("(带 mip 的算式:宽×高×每像素位/8 × 4/3;ASTC 4x4=8bpp 5x5=5.12 6x6=3.56 8x8=2 10x10=1.28 12x12=0.89)");

        Flush("Logs/perf-tex.txt");
    }

    static long EstAndroid(Texture2D t, TextureImporter imp, out string note, out int w, out int h)
    {
        w = t.width; h = t.height;
        int bpp = 4; note = "ETC2?";
        if (imp != null)
        {
            var ps = imp.GetPlatformTextureSettings("Android");
            if (ps.overridden && ps.maxTextureSize > 0 && ps.maxTextureSize < Mathf.Max(w, h))
            {
                float k = (float)ps.maxTextureSize / Mathf.Max(w, h);
                w = Mathf.Max(1, Mathf.RoundToInt(w * k)); h = Mathf.Max(1, Mathf.RoundToInt(h * k));
            }
            else if (!ps.overridden && imp.maxTextureSize > 0 && imp.maxTextureSize < Mathf.Max(w, h))
            {
                float k = (float)imp.maxTextureSize / Mathf.Max(w, h);
                w = Mathf.Max(1, Mathf.RoundToInt(w * k)); h = Mathf.Max(1, Mathf.RoundToInt(h * k));
            }
            string f = ps.overridden ? ps.format.ToString() : "Automatic";
            note = f;
            bpp = BppByName(f);
            if (f == "Automatic" || f == "-1" || f == "AutomaticCompressed")
            {
                note = "Automatic(移动端按 ASTC 6x6 估)";
                bpp = 36;  // 3.56 bpp → 用千分之位表示
            }
        }
        long px = (long)w * h;
        return px * bpp / 1000L * 4L / 3L;
    }

    static int BppByName(string f)
    {
        switch (f)
        {
            case "ASTC_4x4": return 80;
            case "ASTC_5x5": return 51;
            case "ASTC_6x6": return 36;
            case "ASTC_8x8": return 20;
            case "ASTC_10x10": return 13;
            case "ASTC_12x12": return 9;
            case "ETC2_RGB": return 40;
            case "ETC2_RGBA8": return 80;
            case "ETC_RGB4": return 40;
            case "DXT1": return 40;
            case "DXT5": return 80;
            case "RGBA32": return 320;
            case "RGB24": return 240;
            default: return 36;
        }
    }

    static int Bpp(TextureFormat f)
    {
        switch (f)
        {
            case TextureFormat.RGBA32: case TextureFormat.ARGB32: return 32;
            case TextureFormat.RGB24: return 24;
            case TextureFormat.RGB565: case TextureFormat.RGBA4444: case TextureFormat.ARGB4444: return 16;
            case TextureFormat.Alpha8: return 8;
            case TextureFormat.DXT1: case TextureFormat.ETC_RGB4: return 4;
            case TextureFormat.DXT5: case TextureFormat.ETC2_RGBA8: return 8;
            default: return 32;
        }
    }

    // ============================================================ ③ 露台账

    [MenuItem("VRLibrary/性能·露台观影子树账(只报告)")]
    public static void TerraceLedger()
    {
        sb = Head("星空观影(露台)子树账 + 跨引用图");
        var all = new List<GameObject>();
        foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) Collect(r.transform, all);
        var rootGo = all.FirstOrDefault(g => g.name == Root);

        // 露台这块由 TerraceSetup 建:以下名字是它的落点(Build 里写死的)
        string[] heads = { "StarryTerrace", "Pad_Terrace", "TerracePanel", "TerraceSign", "CinemaController", "CinemaSeat" };
        var tops = new List<Transform>();
        foreach (var n in heads)
        {
            var g = FindByName(all, n);
            if (g == null) { sb.AppendLine("  !! 场上找不到 " + n); continue; }
            tops.Add(g.transform);
            sb.AppendLine("[" + n + "] 在 " + Path(g.transform));
        }
        if (tops.Count == 0) { Flush("Logs/perf-terrace.txt"); return; }

        var subtree = new List<GameObject>();
        foreach (var t in tops) Collect(t, subtree);
        subtree = subtree.Distinct().ToList();

        int tris = 0, txt = 0;
        var mats = new HashSet<Material>();
        var texs = new HashSet<Texture2D>();
        foreach (var g in subtree)
        {
            var r = g.GetComponent<Renderer>();
            if (r != null)
            {
                tris += TrisOf(g);
                foreach (var m in r.sharedMaterials) { if (m == null) continue; mats.Add(m);
                    foreach (var p in m.GetTexturePropertyNames()) { var t = m.GetTexture(p) as Texture2D; if (t != null) texs.Add(t); } }
            }
            if (g.GetComponent<TextMesh>() != null) txt++;
        }
        long tbytes = texs.Sum(t => EstAndroid(t, AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter, out string _, out int _, out int _));
        sb.AppendLine("子树:物体 " + subtree.Count + "  三角面 " + tris.ToString("N0") + "  材质 " + mats.Count
            + "  TextMesh " + txt + "  贴图 " + texs.Count + " ≈ " + MB(tbytes)
            + "  灯 " + subtree.Sum(g => g.GetComponents<Light>().Length)
            + "  碰撞体 " + subtree.Sum(g => g.GetComponents<Collider>().Length)
            + "  AudioSource " + subtree.Sum(g => g.GetComponents<AudioSource>().Length));
        sb.AppendLine("占全场比例:物体 " + (100f * subtree.Count / all.Count).ToString("0.0") + "%  "
            + "三角面 " + (100f * tris / Mathf.Max(1, all.Sum(TrisOf))).ToString("0.0") + "%");

        sb.AppendLine("\n---- 一级子分组 ----");
        foreach (var t in tops)
        {
            var kids = new List<GameObject>(); Collect(t, kids);
            sb.AppendLine("  " + Ledger(kids, t.name));
            foreach (Transform k in t)
            {
                var kk = new List<GameObject>(); Collect(k, kk);
                sb.AppendLine("      " + Ledger(kk, "└" + k.name));
            }
        }

        // ---- 跨引用图:抠预制体能不能成,全看这张图
        var inside = new HashSet<GameObject>(subtree);
        sb.AppendLine("\n---- 引用方向 A:子树内部 → 指向子树外(预制体里会变成断链/跨场景引用)----");
        var outEdges = new List<string>();
        foreach (var g in subtree)
            foreach (var c in g.GetComponents<Component>())
            {
                if (c == null) continue;
                foreach (var v in Refs(c))
                {
                    var go = GoOf(v); if (go == null || inside.Contains(go)) continue;
                    outEdges.Add("  " + Path(c.transform) + " [" + c.GetType().Name + "] → " + Path(go.transform) + " (" + v.GetType().Name + ")");
                }
            }
        sb.AppendLine(outEdges.Count == 0 ? "  (无)" : string.Join("\n", outEdges.Distinct().Take(60).ToArray()));
        sb.AppendLine("  小计 " + outEdges.Distinct().Count() + " 条");

        sb.AppendLine("\n---- 引用方向 B:子树外 → 指向子树内(预制体生成后必须运行时重新接线)----");
        var inEdges = new List<string>();
        foreach (var g in all)
        {
            if (inside.Contains(g)) continue;
            foreach (var c in g.GetComponents<Component>())
            {
                if (c == null) continue;
                foreach (var v in Refs(c))
                {
                    var go = GoOf(v); if (go == null || !inside.Contains(go)) continue;
                    inEdges.Add("  " + Path(c.transform) + " [" + c.GetType().Name + "] → " + Path(go.transform) + " (" + v.GetType().Name + ")");
                }
            }
        }
        sb.AppendLine(inEdges.Count == 0 ? "  (无)" : string.Join("\n", inEdges.Distinct().Take(80).ToArray()));
        sb.AppendLine("  小计 " + inEdges.Distinct().Count() + " 条");

        sb.AppendLine("\n---- 子树里挂的脚本(预制体要带的逻辑)----");
        var scripts = new Dictionary<string, int>();
        foreach (var g in subtree) foreach (var c in g.GetComponents<Component>())
        { if (c == null) continue; var n = c.GetType().Name; if (n == "Transform" || n == "MeshFilter" || n == "MeshRenderer" || n == "BoxCollider") continue;
          scripts[n] = (scripts.TryGetValue(n, out int x) ? x : 0) + 1; }
        foreach (var kv in scripts.OrderByDescending(k => k.Value)) sb.AppendLine("  " + kv.Key + " × " + kv.Value);

        Flush("Logs/perf-terrace.txt");
    }

    // ============================================================ ② 定点实测

    /// <summary>写请求文件 → 进 Play,由 VRLibraryPerfSweep 在场景加载后接手跑固定机位采样。</summary>
    [MenuItem("VRLibrary/性能·② 定点实测(进Play自动跑)")]
    public static void Sweep()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[Perf] 已在 Play 中,先退出再跑"); return; }
        // 注意:这个类里有个自己的 Path(Transform) 方法,会盖住 System.IO.Path,所以这里必须全写全名
        string dir = System.IO.Path.GetDirectoryName(Application.dataPath);
        string req = System.IO.Path.Combine(dir, "Logs" + System.IO.Path.DirectorySeparatorChar + "perf-sweep-request.txt");
        string rep = System.IO.Path.Combine(dir, "Logs" + System.IO.Path.DirectorySeparatorChar + "perf-viewpoints.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(req));
        File.WriteAllText(req, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (File.Exists(rep)) File.Delete(rep);      // 先删旧报告:Play 没真跑起来时读到的就是上一轮的数
        EditorApplication.isPlaying = true;
        Debug.Log("[Perf] 已进 Play 跑定点实测 → " + rep);
    }

    // ============================================================ 工具

    static IEnumerable<UnityEngine.Object> Refs(Component c)
    {
        SerializedObject so;
        try { so = new SerializedObject(c); } catch { yield break; }
        var it = so.GetIterator();
        while (it.NextVisible(true))
        {
            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
            var v = it.objectReferenceValue;
            if (v != null) yield return v;
        }
    }

    static GameObject GoOf(UnityEngine.Object o)
    {
        var comp = o as Component; if (comp != null) return comp.gameObject;
        return o as GameObject;
    }

    static GameObject FindByName(List<GameObject> all, string name)
    {
        return all.FirstOrDefault(g => g.name == name);
    }

    static void Collect(Transform t, List<GameObject> into)
    {
        into.Add(t.gameObject);
        for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
    }

    static Mesh MeshOf(Renderer r)
    {
        var sm = r as SkinnedMeshRenderer; if (sm != null) return sm.sharedMesh;
        var mf = r.GetComponent<MeshFilter>(); return mf == null ? null : mf.sharedMesh;
    }

    static int TrisOf(GameObject g)
    {
        var r = g.GetComponent<Renderer>(); if (r == null) return 0;
        var m = MeshOf(r); if (m == null) return 0;
        long n = 0;
        for (int i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i) / 3;
        return (int)n;
    }

    static string Path(Transform t)
    {
        var s = t.name; var p = t.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }

    static string Path0(string s, int w) { return s.Length >= w ? s.Substring(0, w) : s.PadRight(w); }

    static long EstBytes(Texture2D t)
    {
        if (t == null) return 0;
        return (long)t.width * t.height * Bpp(t.format) / 8 * 4 / 3;
    }

    static string MB(long b) { return (b / 1048576f).ToString("0.00") + " MB"; }

    static int Get(object o, string prop, int dflt)
    {
        var pi = o.GetType().GetProperty(prop); if (pi == null) return dflt;
        try { return Convert.ToInt32(pi.GetValue(o, null)); } catch { return dflt; }
    }

    static float GetF(object o, string prop, float dflt)
    {
        var pi = o.GetType().GetProperty(prop); if (pi == null) return dflt;
        try { return Convert.ToSingle(pi.GetValue(o, null)); } catch { return dflt; }
    }

    static StringBuilder Head(string title)
    {
        var b = new StringBuilder();
        b.AppendLine("== 性能体检:" + title + " ==");
        b.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "  平台 " + EditorUserBuildSettings.activeBuildTarget
            + "  画质档 " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
        return b;
    }

    static void Flush(string path)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Perf] 报告落盘失败:" + e.Message); }
        Debug.Log(sb.ToString());
        Debug.Log("[Perf] 报告已落盘 " + path);
    }
}