using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 性能⑧:遮挡剔除(编辑器侧的第二层剔除)。菜单 VRLibrary/性能·⑧ xxx。
///
/// 为什么需要它:A3 只做了"**整区开合**"(一层/二层/露台),同一个区内**互相遮挡**的几何照画不误
/// —— 站在一排书架前,A 排背后的 B、C 排照样提交,而它们的包围盒都落在视锥里,
/// Unity 的**视锥剔除救不了这种情况**(视锥剔除只看"在不在视野范围内",不看"被不被挡住")。
/// 全场景目前**没有任何 occlusion 数据**,所以这一层是空的。
///
/// 做法(两步,缺一不可):
///   ① 把**又大又实**的结构标成 `OccluderStatic`(它们负责"挡"):墙、地板、吊顶、二层板、装饰面;
///   ② 把**不动但可能被挡**的静物标成 `OccludeeStatic`(它们负责"被剔"):沿用 A1 已经打好的
///      `BatchingStatic` 集合 —— 那批已经证明过"全程不动",拿来当 occludee 是安全的。
///      **会动的东西(262 本书)不打**:Unity 本来就不会对动态物体做遮挡剔除,打了反而危险。
///
/// 之后 `StaticOcclusionCulling.Compute()` 烘一份遮挡数据(落在场景目录下的 OcclusionCullingData.asset),
/// 运行期由相机(`useOcclusionCulling`)自动使用,不需要任何脚本参与。
///
/// 回退:菜单 `性能·⑧ 回退(清遮挡数据 + 去标记)`;快照 `Logs/SmartLibrary.pre-occlusion.unity`。
/// </summary>
public static class VRLibraryOcclusion
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-occlusion.unity";
    const string Root = "SmartLibrary";

    /// <summary>"负责挡"的分组 —— 都是又大又实的结构件。其余静物交给 occludee。</summary>
    static readonly string[] OpaqueStructures =
    {
        "Walls", "Floor_Detailed", "Ceiling", "Mezzanine", "Polish1", "WallDecor",
    };

    [MenuItem("VRLibrary/性能·⑧ 遮挡剔除·现状(只报告)")]
    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑧ 遮挡剔除:现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Occ] 找不到 " + Root); return; }
        var all = new List<GameObject>(); Collect(root.transform, all);

        int rend = 0, occluder = 0, occludee = 0, neither = 0;
        var byGroup = new Dictionary<string, int[]>();
        foreach (var g in all)
        {
            if (g.GetComponent<Renderer>() == null) continue;
            rend++;
            var f = GameObjectUtility.GetStaticEditorFlags(g);
            bool oc = (f & StaticEditorFlags.OccluderStatic) != 0;
            bool oe = (f & StaticEditorFlags.OccludeeStatic) != 0;
            if (oc) occluder++;
            if (oe) occludee++;
            if (!oc && !oe) neither++;
            var top = TopName(g.transform);
            if (!byGroup.TryGetValue(top, out int[] v)) { v = new int[3]; byGroup[top] = v; }
            v[0]++; if (oc) v[1]++; if (oe) v[2]++;
        }
        sb.AppendLine("渲染器 " + rend + "  已标 Occluder " + occluder + "  已标 Occludee " + occludee + "  **两者都没标** " + neither);

        string dataPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ScenePath), "SmartLibrary", "OcclusionCullingData.asset");
        sb.AppendLine("\n遮挡数据文件 " + dataPath + " 存在=" + File.Exists(dataPath)
            + "  大小=" + (File.Exists(dataPath) ? (new FileInfo(dataPath).Length / 1024) + " KB" : "-"));
        sb.AppendLine("StaticOcclusionCulling.umbraDataSize = " + (StaticOcclusionCulling.umbraDataSize / 1024) + " KB");
        sb.AppendLine("烘焙参数:smallestOccluder=" + StaticOcclusionCulling.smallestOccluder
            + " m²  smallestHole=" + StaticOcclusionCulling.smallestHole
            + " m  backfaceThreshold=" + StaticOcclusionCulling.backfaceThreshold);
        var cam = Camera.main;
        sb.AppendLine("相机 useOcclusionCulling = " + (cam == null ? "找不到相机" : cam.useOcclusionCulling.ToString())
            + "(运行期靠它自动生效,不需要脚本)");
        sb.AppendLine("\n---- 按分组(总 / Occluder / Occludee)----");
        foreach (var kv in byGroup.OrderByDescending(k => k.Value[0]).Take(14))
            sb.AppendLine("  " + kv.Value[0].ToString().PadLeft(5) + " / " + kv.Value[1].ToString().PadLeft(5)
                + " / " + kv.Value[2].ToString().PadLeft(5) + "  " + kv.Key);
        Flush(sb.ToString(), "Logs/perf-occlusion.txt");
    }

    [MenuItem("VRLibrary/性能·⑧ 标记并烘焙遮挡剔除(改场景,可能要几分钟)")]
    public static void Bake()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) { Debug.LogError("[Occ] 当前场景不是 SmartLibrary"); return; }
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Occ] 找不到 " + Root); return; }
        SnapshotOnce();

        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑧ 标记并烘焙遮挡剔除 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var all = new List<GameObject>(); Collect(root.transform, all);
        int occ = 0, oee = 0, skipped = 0;
        foreach (var g in all)
        {
            if (g.GetComponent<Renderer>() == null) continue;
            var flags = GameObjectUtility.GetStaticEditorFlags(g);
            var top = TopName(g.transform);

            if (OpaqueStructures.Contains(top))
            {
                if ((flags & StaticEditorFlags.OccluderStatic) == 0)
                { GameObjectUtility.SetStaticEditorFlags(g, flags | StaticEditorFlags.OccluderStatic); occ++; }
                // 结构件自己也可能是 occludee(被更外层的墙挡住),一并标上
                flags = GameObjectUtility.GetStaticEditorFlags(g);
            }
            // occludee 只给"已经确认不动"的那批(A1 打的 BatchingStatic)
            if ((flags & StaticEditorFlags.BatchingStatic) != 0)
            {
                if ((flags & StaticEditorFlags.OccludeeStatic) == 0)
                { GameObjectUtility.SetStaticEditorFlags(g, flags | StaticEditorFlags.OccludeeStatic); oee++; }
            }
            else skipped++;
        }
        sb.AppendLine("新标 OccluderStatic " + occ + " 件(分组:" + string.Join("/", OpaqueStructures) + ")");
        sb.AppendLine("新标 OccludeeStatic " + oee + " 件(来自已确认不动的 BatchingStatic 集合)");
        sb.AppendLine("跳过 " + skipped + " 件(会动/有脚本 —— 动态物体本就不参与遮挡剔除,标了反而危险)");

        // 烘焙参数:smallestOccluder 是"多小算小到不值得当遮挡体",smallestHole 是"多大的缝就当看得见"
        StaticOcclusionCulling.smallestOccluder = 5f;
        StaticOcclusionCulling.smallestHole = 0.25f;      // 默认值,洞小就忽略 → 少一点"漏剔除"
        StaticOcclusionCulling.backfaceThreshold = 100f;
        sb.AppendLine("参数:smallestOccluder=" + StaticOcclusionCulling.smallestOccluder
            + "  smallestHole=" + StaticOcclusionCulling.smallestHole
            + "  backfaceThreshold=" + StaticOcclusionCulling.backfaceThreshold);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        sb.AppendLine("\n开始 Compute()(同步,期间编辑器会弹进度条)…");
        StaticOcclusionCulling.Compute();
        sb.AppendLine("Compute() 返回。umbraDataSize = " + (StaticOcclusionCulling.umbraDataSize / 1024) + " KB");

        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene) + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("回退:菜单 性能·⑧ 回退;或 copy " + Snapshot + " → " + ScenePath);
        Flush(sb.ToString(), "Logs/perf-occlusion.txt");
    }

    [MenuItem("VRLibrary/性能·⑧ 回退(清遮挡数据 + 去标记)")]
    public static void Revert()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑧ 回退 ==");
        StaticOcclusionCulling.Clear();
        var scene = EditorSceneManager.GetActiveScene();
        var root = GameObject.Find(Root);
        int n = 0;
        if (root != null)
        {
            var all = new List<GameObject>(); Collect(root.transform, all);
            foreach (var g in all)
            {
                var f = GameObjectUtility.GetStaticEditorFlags(g);
                var nf = f & ~(StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                if (nf != f) { GameObjectUtility.SetStaticEditorFlags(g, nf); n++; }
            }
        }
        sb.AppendLine("已清遮挡数据;去掉 Occluder/Occludee 标记 " + n + " 件");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Flush(sb.ToString(), "Logs/perf-occlusion.txt");
    }

    // ------------------------------------------------------------ 工具

    static void Collect(Transform t, List<GameObject> into)
    {
        into.Add(t.gameObject);
        for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
    }

    static string TopName(Transform t)
    {
        while (t.parent != null && t.parent.name != Root) t = t.parent;
        return t.name;
    }

    static void SnapshotOnce()
    {
        try
        {
            if (File.Exists(Snapshot)) { Debug.Log("[Occ] 已有快照,不覆盖:" + Snapshot); return; }
            Directory.CreateDirectory("Logs");
            File.Copy(ScenePath, Snapshot);
            Debug.Log("[Occ] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[Occ] 快照失败:" + e.Message); }
    }

    static void Flush(string text, string path)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Occ] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}