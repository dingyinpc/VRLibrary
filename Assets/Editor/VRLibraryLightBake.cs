using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 性能⑥:烘焙光照。菜单 VRLibrary/性能·⑥ xxx。
///
/// 现状(Logs/perf-static.txt):**0 张光照贴图 + 19 盏灯全是 Realtime**(平行 1 + 点 14 + 聚光 4),
/// 实时阴影 pass 还会把 25 m 内的几何**再画一遍** —— 这是 draw call 的大头之一。
/// 烘焙之后:点/聚光可以整批改成 Baked(运行期一次都不算),平行光也可以去掉实时阴影,
/// 阴影 pass 整个消失。
///
/// ⚠ 前置条件必须先量:光照贴图需要 **UV1(第二套 UV)**,而这个场景全部由 Unity 内置基本体拼成
/// (12 个唯一网格:Cube/Sphere/Cylinder/Plane…)。内置网格只有 UV0,没有 UV1 的话烘出来的
/// 光照是错的(不是"没效果",是"脏")。所以本文件第一件事就是**体检 UV1**,体检不过就不动烘焙。
///
/// 回退:菜单 VRLibrary/性能·⑥ 回退(清光照数据 + 灯回 Realtime + 去 ContributeGI 标记)。
/// </summary>
public static class VRLibraryLightBake
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string ReportPath = "Logs/perf-bake.txt";
    const string Root = "SmartLibrary";

    // ============================================================ 前置体检

    [MenuItem("VRLibrary/性能·⑥ 烘焙前置体检(只报告)")]
    public static void Preflight()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 烘焙前置体检 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Bake] 找不到 " + Root); return; }
        var all = new List<GameObject>(); Collect(root.transform, all);

        // 唯一网格的 UV 通道
        var meshes = new Dictionary<Mesh, int>();
        foreach (var g in all)
        {
            var mf = g.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            meshes[mf.sharedMesh] = (meshes.TryGetValue(mf.sharedMesh, out int c) ? c : 0) + 1;
        }
        int withUv1 = 0, withoutUv1 = 0, objectsWithoutUv1 = 0;
        sb.AppendLine("\n---- 网格 UV1 体检(光照贴图的硬前提)----");
        foreach (var kv in meshes.OrderByDescending(k => k.Value))
        {
            var m = kv.Key;
            bool has = m.uv2 != null && m.uv2.Length > 0;
            if (has) withUv1++; else { withoutUv1++; objectsWithoutUv1 += kv.Value; }
            sb.AppendLine("  " + (has ? "有 UV1" : "**没有 UV1**") + "  " + m.name.PadRight(22)
                + " 用它的物体 " + kv.Value.ToString().PadLeft(5)
                + "  顶点 " + m.vertexCount + "  子网格 " + m.subMeshCount
                + (m.isReadable ? "  可读" : "  不可读"));
        }
        sb.AppendLine("  合计:有 UV1 的网格 " + withUv1 + " 个,没有的 " + withoutUv1 + " 个,受影响物体 " + objectsWithoutUv1 + " 个");
        sb.AppendLine(withoutUv1 == 0
            ? "  ✅ 前置条件过:可以直接烘。"
            : "  ❌ 前置条件不过:直接烘会得到脏光照 —— 要么先给这些网格生成 UV1(得把内置网格复制成可写资产再改所有 MeshFilter),要么放弃烘焙。");

        // 灯与静态标记
        var lights = all.SelectMany(g => g.GetComponents<Light>()).ToArray();
        sb.AppendLine("\n---- 灯 ----");
        sb.AppendLine("  平行 " + lights.Count(l => l.type == LightType.Directional)
            + "  点 " + lights.Count(l => l.type == LightType.Point)
            + "  聚光 " + lights.Count(l => l.type == LightType.Spot)
            + "  Realtime " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime)
            + "  Baked " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked)
            + "  Mixed " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed));
        sb.AppendLine("  运行期阴影(会额外画一遍几何):"
            + string.Join(",", lights.Where(l => l.shadows != LightShadows.None).Select(l => l.name + "(" + l.type + ")").ToArray()));

        int rends = 0, gi = 0;
        foreach (var g in all)
        {
            if (g.GetComponent<Renderer>() == null) continue;
            rends++;
            if ((GameObjectUtility.GetStaticEditorFlags(g) & StaticEditorFlags.ContributeGI) != 0) gi++;
        }
        sb.AppendLine("\n---- 静态标记 ----");
        sb.AppendLine("  渲染器 " + rends + "  已打 ContributeGI " + gi + "  未打 " + (rends - gi));

        var lms = LightmapSettings.lightmaps;
        sb.AppendLine("\n---- 现状 ----");
        sb.AppendLine("  光照贴图 " + lms.Length + " 张  环境光 " + RenderSettings.ambientMode
            + "  雾 " + RenderSettings.fog + "  天空盒 " + (RenderSettings.skybox == null ? "无" : RenderSettings.skybox.name));
        var ls = Lightmapping.GetLightingSettingsForScene(EditorSceneManager.GetActiveScene());
        if (ls != null)
            sb.AppendLine("  烘焙设置:lightmapper=" + ls.lightmapper + " 分辨率(texels/unit)=" + ls.lightmapResolution
                + " 上限=" + ls.lightmapMaxSize + " 方向性=" + LightmapSettings.lightmapsMode + " AO=" + ls.ao
                + " bakedGI=" + ls.bakedGI + " realtimeGI=" + ls.realtimeGI);
        else sb.AppendLine("  !! 场景还没有 LightingSettings 资产");

        Flush(sb.ToString());
    }

    // ============================================================ 打标记 / 烘焙 / 回退

    [MenuItem("VRLibrary/性能·⑥ 打 ContributeGI 标记(改场景)")]
    public static void MarkGi()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Bake] 找不到 " + Root); return; }
        var all = new List<GameObject>(); Collect(root.transform, all);
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 打 ContributeGI ==");
        int n = 0, skip = 0;
        foreach (var g in all)
        {
            if (g.GetComponent<Renderer>() == null) continue;
            var flags = GameObjectUtility.GetStaticEditorFlags(g);
            // 只给"已经因为静态合批而确认过不动"的物体打 GI 标记 —— 会动的东西烘焙进去就是错的光照
            if ((flags & StaticEditorFlags.BatchingStatic) == 0) { skip++; continue; }
            if ((flags & StaticEditorFlags.ContributeGI) != 0) continue;
            GameObjectUtility.SetStaticEditorFlags(g, flags | StaticEditorFlags.ContributeGI);
            n++;
        }
        sb.AppendLine("新打 ContributeGI " + n + " 件;跳过(没打 BatchingStatic,也就是会动/有脚本的)" + skip + " 件");
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑥ 配置烘焙参数(轻量档)")]
    public static void Configure()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var ls = Lightmapping.GetLightingSettingsForScene(scene);
        if (ls == null)
        {
            ls = new LightingSettings();
            Lightmapping.lightingSettings = ls;
        }
        ls.name = "VRLibrary 轻量烘焙";
        ls.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
        ls.lightmapResolution = 12f;          // texels/unit:一体机上够用就行的量级
        ls.lightmapMaxSize = 1024;            // 单张上限,控制显存
        ls.lightmapPadding = 2;
        // 方向性是 LightmapSettings 上的静态设置,不在 LightingSettings 里(写 ls.directionality 编译不过)
        LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
        ls.ao = false;                        // 环境光遮蔽在移动端性价比低
        ls.bakedGI = true;
        ls.realtimeGI = false;
        ls.mixedBakeMode = MixedLightingMode.IndirectOnly;
        ls.lightmapCompression = LightmapCompression.NormalQuality;
        Lightmapping.lightingSettings = ls;
        AssetDatabase.SaveAssets();
        Flush("== 性能⑥ 烘焙参数(轻量档)==\n  lightmapper=" + ls.lightmapper
            + "\n  分辨率=" + ls.lightmapResolution + " texels/unit  单张上限=" + ls.lightmapMaxSize
            + "\n  方向性=" + LightmapSettings.lightmapsMode + "  AO=" + ls.ao
            + "\n  bakedGI=" + ls.bakedGI + " realtimeGI=" + ls.realtimeGI
            + "\n  压缩=" + ls.lightmapCompression);
    }

    [MenuItem("VRLibrary/性能·⑥ 开始烘焙(异步)")]
    public static void Bake()
    {
        if (Lightmapping.isRunning) { Debug.LogWarning("[Bake] 已经在烘了"); return; }
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) { Debug.LogError("[Bake] 当前场景不是 SmartLibrary"); return; }
        EditorSceneManager.SaveScene(scene);
        Flush("== 性能⑥ 开始烘焙 ==\n时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "\n异步烘焙已启动,完成后 Unity 会把 Lighting Data 落到场景目录;"
            + "\n用菜单 VRLibrary/性能·⑥ 烘焙结果(只报告) 读结果。");
        Lightmapping.BakeAsync();
    }

    [MenuItem("VRLibrary/性能·⑥ 烘焙结果(只报告)")]
    public static void BakeResult()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 烘焙结果 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  isRunning=" + Lightmapping.isRunning);
        var lms = LightmapSettings.lightmaps;
        long bytes = 0;
        foreach (var lm in lms)
        {
            if (lm.lightmapColor != null) bytes += (long)lm.lightmapColor.width * lm.lightmapColor.height * 4;
            sb.AppendLine("  " + (lm.lightmapColor == null ? "(空)" : lm.lightmapColor.name + " " + lm.lightmapColor.width + "×" + lm.lightmapColor.height));
        }
        sb.AppendLine("光照贴图 " + lms.Length + " 张,未压缩约 " + (bytes / 1048576f).ToString("0.00") + " MB");
        var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
        sb.AppendLine("灯:Realtime " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime)
            + "  Baked " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked)
            + "  Mixed " + lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed)
            + "  开阴影 " + lights.Count(l => l.shadows != LightShadows.None));
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑥ 回退(清光照数据 + 灯回 Realtime + 去 GI 标记)")]
    public static void Revert()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 回退 ==");
        Lightmapping.Clear();
        Lightmapping.ClearDiskCache();
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
        {
            if (l.lightmapBakeType != LightmapBakeType.Realtime)
            {
                l.lightmapBakeType = LightmapBakeType.Realtime;
                EditorUtility.SetDirty(l);
                sb.AppendLine("  " + l.name + " → Realtime");
            }
        }
        var root = GameObject.Find(Root);
        int n = 0;
        if (root != null)
        {
            var all = new List<GameObject>(); Collect(root.transform, all);
            foreach (var g in all)
            {
                var f = GameObjectUtility.GetStaticEditorFlags(g);
                if ((f & StaticEditorFlags.ContributeGI) != 0)
                {
                    GameObjectUtility.SetStaticEditorFlags(g, f & ~StaticEditorFlags.ContributeGI);
                    n++;
                }
            }
        }
        sb.AppendLine("去掉 ContributeGI " + n + " 件;光照数据已清");
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Flush(sb.ToString());
    }

    // ============================================================ 前置补齐:UV1 与光探针

    /// <summary>
    /// 体检发现 6 个导入模型(SM_* 家具与吊顶)没有 UV1 —— 直接烘会得到脏光照。
    /// 它们的导入设置里有一个开关就是干这个的(ModelImporter.generateSecondaryUV),
    /// 打开重导即可,不必去动场景里的 MeshFilter。
    /// </summary>
    [MenuItem("VRLibrary/性能·⑥ 给模型生成 UV1(改导入设置)")]
    public static void GenerateUv1()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 给模型生成 UV1 ==");
        var guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets/Textures/Carpet", "Assets/Textures/InteriorProps" });
        int done = 0, already = 0;
        foreach (var g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) { sb.AppendLine("  ?? 不是模型导入器:" + path); continue; }
            if (mi.generateSecondaryUV) { already++; sb.AppendLine("  已有 UV1:" + path); continue; }
            mi.generateSecondaryUV = true;
            mi.SaveAndReimport();
            done++;
            sb.AppendLine("  已打开 Generate Lightmap UVs 并重导:" + path);
        }
        sb.AppendLine("新开 " + done + " 个,原本就有 " + already + " 个");
        Flush(sb.ToString());
        Preflight();      // 立刻复检一次,不留"以为改好了"的余地
    }

    /// <summary>
    /// 布一组光探针。**这一步不是可选项**:烘完把灯改成 Baked 之后,光照只存在于光照贴图里,
    /// 而**会动的东西(262 本书、手里的东西)拿不到光照贴图** —— 它们只能靠光探针插值取光。
    /// 不布探针,书会变成一片死黑/死亮。
    /// </summary>
    [MenuItem("VRLibrary/性能·⑥ 布光探针(改场景)")]
    public static void Probes()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Bake] 找不到 " + Root); return; }
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 布光探针 ==");

        // 用地面那组(Floor_Detailed)的包围盒当建筑范围
        var floor = FindByName(root.transform, "Floor_Detailed");
        if (floor == null) { sb.AppendLine("  !! 找不到 Floor_Detailed,拿不到建筑范围"); Flush(sb.ToString()); return; }
        var rends = floor.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) { sb.AppendLine("  !! Floor_Detailed 里没有渲染器"); Flush(sb.ToString()); return; }
        var b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        sb.AppendLine("  建筑范围 " + b.min.ToString("0.0") + " ~ " + b.max.ToString("0.0"));

        var old = FindByName(root.transform, "VRLibraryProbes");
        if (old != null) { UnityEngine.Object.DestroyImmediate(old.gameObject); sb.AppendLine("  删掉旧探针组重建"); }

        var go = new GameObject("VRLibraryProbes");
        go.transform.SetParent(root.transform, false);
        var grp = go.AddComponent<LightProbeGroup>();
        var pos = new List<Vector3>();
        const float step = 3f;
        foreach (float y in new[] { b.min.y + 1.2f, b.min.y + 4.2f })
            for (float x = b.min.x + step * 0.5f; x < b.max.x; x += step)
                for (float z = b.min.z + step * 0.5f; z < b.max.z; z += step)
                    pos.Add(new Vector3(x, y, z));
        grp.probePositions = pos.ToArray();
        sb.AppendLine("  探针 " + pos.Count + " 个,间距 " + step + " m,两层(y="
            + (b.min.y + 1.2f).ToString("0.0") + " / " + (b.min.y + 4.2f).ToString("0.0") + ")");

        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("  保存场景:" + EditorSceneManager.SaveScene(scene));
        Flush(sb.ToString());
    }

    /// <summary>烘完之后把灯全部改成 Baked:运行期一盏实时灯都不算,实时阴影 pass 也整个消失。</summary>
    [MenuItem("VRLibrary/性能·⑥ 灯全部改 Baked(改场景)")]
    public static void LightsToBaked()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑥ 灯全部改 Baked ==");
        int n = 0;
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
        {
            if (l.lightmapBakeType == LightmapBakeType.Baked) continue;
            l.lightmapBakeType = LightmapBakeType.Baked;
            EditorUtility.SetDirty(l);
            sb.AppendLine("  " + l.name + " (" + l.type + ") → Baked,阴影设置保留但不实时算");
            n++;
        }
        sb.AppendLine("改 " + n + " 盏。⚠ 灯的改动要让光照贴图重算一次才生效(改完必须再烘一次)");
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));
        Flush(sb.ToString());
    }

    static Transform FindByName(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindByName(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    // ============================================================ 工具

    static void Collect(Transform t, List<GameObject> into)
    {
        into.Add(t.gameObject);
        for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
    }

    static void Flush(string text)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(ReportPath, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Bake] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}