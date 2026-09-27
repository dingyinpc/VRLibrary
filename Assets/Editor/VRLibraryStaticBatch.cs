using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 性能①:给"永远不会动"的静物补 BatchingStatic。菜单 VRLibrary/性能·① 静态合批补标(改场景并自动保存)。
///
/// 为什么值得做(实测,Logs/perf-static.txt):
///   全场 2352 个渲染器里 **1739 个一个静态标记都没有**;运行期最坏视角 **DrawCall 2918 / 批次 2265**,
///   而三角面只有 9.2 万 —— 典型的"批次拥挤"而不是"模型太重"。
///   合批的前提是"这东西不动",而全场 2352 个渲染器里**只有 262 件书是可抓的**
///   (XRGrabInteractable 262 = LibraryBook 262),其余全是结构、书架、地面、吊顶、装饰。
///
/// 判定规则(**只标"绝对安全"的**,宁可少标不可错标):
///   要标:有 MeshRenderer + sharedMesh,
///   且 **自身与所有祖先都没有任何 MonoBehaviour**、没有 Rigidbody,
///   且不在 XR 装置子树里,也不在"被脚本驱动的物体"闭包里。
/// 为什么"没有 MonoBehaviour"这条就够安全:
///   这个场景里会动的东西**全部挂着自己的脚本**(LibraryBook/HoloOrbiter/TerraceScreen/KioskTeleporter…),
///   而不挂脚本的纯几何体没有任何东西能移动它 —— 除非它是被**别的脚本**通过引用拖动的
///   (TerraceScreen.screen、BookSearchController.marker 这种),所以再加一张"被驱动闭包"排除表。
///   这两条加起来不会误伤,也不会漏掉。
///
/// 幂等:重复跑只是把已经标上的再标一遍;回退用 Logs/SmartLibrary.pre-batch.unity(首跑落盘,不覆盖)或
///       git checkout HEAD -- Assets/_Scenes/SmartLibrary.unity。
/// </summary>
public static class VRLibraryStaticBatch
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-batch.unity";
    const string Root = "SmartLibrary";

    /// <summary>会移动物体的脚本 —— 它们自己所在的物体与后代一律不标。</summary>
    static readonly string[] Movers =
    {
        "LibraryBook", "HoloOrbiter", "TerraceScreen", "KioskTeleporter", "TerraceCinema",
        "LibraryHud", "SprintController", "BookSearchController", "BookReader",
        "LibraryUIController", "DayNightController", "VRLibrarySmoke", "BookReturnSocket",
        "VRLibraryBoot", "VideoDecodeProbe",
    };

    [MenuItem("VRLibrary/性能·① 静态合批补标(改场景并自动保存)")]
    public static void Mark()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) { Debug.LogError("[Batch] 当前场景不是 SmartLibrary:" + scene.path); return; }
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Batch] 找不到根节点 " + Root); return; }
        SnapshotOnce();

        var all = new List<GameObject>();
        Collect(root.transform, all);

        // ---- 驱动闭包:被"会动的脚本"通过引用拖动的物体(它自己没脚本,但会跟着动)
        var driven = new HashSet<GameObject>();
        int drivenFrom = 0;
        foreach (var g in all)
            foreach (var c in g.GetComponents<Component>())
            {
                if (c == null || !Movers.Contains(c.GetType().Name)) continue;
                foreach (var v in Refs(c))
                {
                    var t = v as Transform; var vg = v as GameObject;
                    if (t == null && vg != null) t = vg.transform;
                    if (t == null) continue;
                    foreach (var k in Subtree(t.gameObject)) { if (driven.Add(k)) drivenFrom++; }
                }
            }

        var sb = new StringBuilder();
        sb.AppendLine("== 性能① 静态合批补标 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  场景物体 " + all.Count);
        sb.AppendLine("被驱动闭包(排除) " + driven.Count + " 件(来自会动脚本的引用)");

        int marked = 0, already = 0;
        var reasons = new Dictionary<string, int>();
        var byGroup = new Dictionary<string, int>();
        foreach (var g in all)
        {
            var r = g.GetComponent<MeshRenderer>();
            if (r == null) { Bump(reasons, "没有 MeshRenderer"); continue; }
            var mf = g.GetComponent<MeshFilter>();          // sharedMesh 在 MeshFilter 上,不在 MeshRenderer 上
            if (mf == null || mf.sharedMesh == null) { Bump(reasons, "没有网格"); continue; }
            var flags = GameObjectUtility.GetStaticEditorFlags(g);
            if ((flags & StaticEditorFlags.BatchingStatic) != 0) { already++; continue; }

            string why = BlockReason(g, driven);
            if (why != null) { Bump(reasons, why); continue; }

            GameObjectUtility.SetStaticEditorFlags(g, flags | StaticEditorFlags.BatchingStatic);
            marked++;
            var top = TopName(g.transform);
            byGroup[top] = (byGroup.TryGetValue(top, out int x) ? x : 0) + 1;
        }

        sb.AppendLine("本次新标 BatchingStatic " + marked + " 件;原本已有 " + already + " 件");
        sb.AppendLine("\n---- 没标的按原因 ----");
        foreach (var kv in reasons.OrderByDescending(k => k.Value)) sb.AppendLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
        sb.AppendLine("\n---- 本次标记按分组 ----");
        foreach (var kv in byGroup.OrderByDescending(k => k.Value)) sb.AppendLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);

        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("\n保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败")
            + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("回退:copy " + Snapshot + " → " + ScenePath + " 后 openScene;或 git checkout HEAD -- " + ScenePath);
        Flush(sb.ToString(), "Logs/perf-batch-mark.txt");
    }

    [MenuItem("VRLibrary/性能·① 复核(只报告)")]
    public static void Verify()
    {
        var root = GameObject.Find(Root);
        if (root == null) { Debug.LogError("[Batch] 找不到根节点 " + Root); return; }
        var all = new List<GameObject>();
        Collect(root.transform, all);
        int rend = 0, bat = 0, txt = 0, txtBat = 0;
        var byGroup = new Dictionary<string, int[]>();
        foreach (var g in all)
        {
            if (g.GetComponent<MeshRenderer>() == null) continue;
            rend++;
            bool b = (GameObjectUtility.GetStaticEditorFlags(g) & StaticEditorFlags.BatchingStatic) != 0;
            if (b) bat++;
            if (g.GetComponent<TextMesh>() != null) { txt++; if (b) txtBat++; }
            var top = TopName(g.transform);
            if (!byGroup.TryGetValue(top, out int[] v)) { v = new int[2]; byGroup[top] = v; }
            v[0]++; if (b) v[1]++;
        }
        var sb = new StringBuilder();
        sb.AppendLine("== 性能① 复核:" + EditorSceneManager.GetActiveScene().name
            + "  时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
        sb.AppendLine("渲染器 " + rend + "  已标 BatchingStatic " + bat + "  未标 " + (rend - bat)
            + "(未标的应当只有:可抓的书 262 + XR 装置 + 被驱动件)");
        sb.AppendLine("TextMesh " + txt + " 个,其中已标静态 " + txtBat + " 个");
        sb.AppendLine("\n---- 按分组(总 / 已标)----");
        foreach (var kv in byGroup.OrderByDescending(k => k.Value[0]).Take(28))
            sb.AppendLine("  " + kv.Value[0].ToString().PadLeft(5) + " / " + kv.Value[1].ToString().PadLeft(5) + "  " + kv.Key);
        Flush(sb.ToString(), "Logs/perf-batch-verify.txt");
    }

    // ------------------------------------------------------------ 判定

    static string BlockReason(GameObject g, HashSet<GameObject> driven)
    {
        if (g.GetComponent<Rigidbody>() != null) return "自身有 Rigidbody";
        for (var t = g.transform; t != null; t = t.parent)
        {
            var cur = t.gameObject;
            if (cur.name == "XR Interaction Setup") return "在 XR 装置子树里";
            if (driven.Contains(cur)) return "被脚本引用驱动(会跟着动)";
            if (cur.GetComponent<Rigidbody>() != null) return "祖先有 Rigidbody";
            var mb = cur.GetComponents<MonoBehaviour>();
            if (mb.Length > 0)
            {
                for (int i = 0; i < mb.Length; i++)
                    if (mb[i] != null) return "自身或祖先挂着脚本 " + mb[i].GetType().Name;
                return "自身或祖先有丢失的脚本(类型找不到了,不敢当静物)";
            }
        }
        return null;
    }

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

    static IEnumerable<GameObject> Subtree(GameObject g)
    {
        yield return g;
        for (int i = 0; i < g.transform.childCount; i++)
            foreach (var k in Subtree(g.transform.GetChild(i).gameObject)) yield return k;
    }

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

    static void Bump(Dictionary<string, int> d, string k)
    {
        d[k] = (d.TryGetValue(k, out int x) ? x : 0) + 1;
    }

    static void SnapshotOnce()
    {
        try
        {
            if (File.Exists(Snapshot)) { Debug.Log("[Batch] 已有快照,不覆盖:" + Snapshot); return; }
            Directory.CreateDirectory("Logs");
            File.Copy(ScenePath, Snapshot);
            Debug.Log("[Batch] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[Batch] 快照失败:" + e.Message); }
    }

    static void Flush(string text, string path)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[Batch] 报告落盘失败:" + e.Message); }
        Debug.Log(text);
        Debug.Log("[Batch] 报告 " + path);
    }
}